using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AXS.Core.Models;
using AXS.Core.Parsing;
using Microsoft.Playwright;

namespace AXS.Fetch.Playwright;

/// <summary>Challenge never cleared, AXS hard-block page, or the commerce API answered
/// 403/429/440 - worth retrying with a fresh proxy session. python AxsBlocked.</summary>
public sealed class AxsBlockedException : Exception
{
    public AxsBlockedException(string message) : base(message) { }
}

/// <summary>No AXS ticket flow (no ticket URL, sold elsewhere, stuck in Queue-it...). Not a
/// block - don't retry. python NotOnSale.</summary>
public sealed class AxsNotOnSaleException : Exception
{
    public AxsNotOnSaleException(string message) : base(message) { }
}

/// <summary>What the ticket app's own commerce calls returned: capture name -> raw JSON text
/// (same names as python/axs/client.py CAPTURES), plus notes.</summary>
public sealed class AxsInventory
{
    public Dictionary<string, string> Captured { get; } = new();
    public List<string> Notes { get; } = new();
}

public sealed class AxsFetchOptions
{
    public string ProxyTemplate { get; set; } = "";
    public int Retries { get; set; } = 4;
    public bool Headless { get; set; }
    /// <summary>"chrome" = installed Google Chrome (required for AXS's Turnstile); null = bundled Chromium.</summary>
    public string? Channel { get; set; } = "chrome";
    public double SettleMaxSeconds { get; set; } = 40;
    public double InventoryTimeoutSeconds { get; set; } = 150;
    public double OfferSearchGraceSeconds { get; set; } = 25;
    public double QueueMaxSeconds { get; set; } = 600;
    public double SleepSeconds { get; set; } = 2;
    /// <summary>Drop --enable-automation and set --disable-blink-features=AutomationControlled.</summary>
    public bool ReduceAutomationFlags { get; set; } = true;
    public double NoProxyBackoffSeconds { get; set; } = 60;
    /// <summary>(eventId, captureName, rawJson) - persist raw responses.</summary>
    public Action<long, string, string>? OnRaw { get; set; }
}

/// <summary>
/// Fetches one AXS event (metadata + full inventory) through a real browser - 1:1 port of
/// python/axs/client.py AxsClient (see its module doc for the why):
///   * installed Chrome (Channel="chrome"), headless=false, poll the title until Cloudflare clears;
///   * AXS's own hard-block page ("actively prevent automated bots") fails fast;
///   * the ticket page is loaded and the responses the AXS app itself makes are CAPTURED
///     (IPage.Response) - replaying them gets 429 / 403 WAF;
///   * any commerce 403/429/440 or a stuck challenge -> close everything, reopen with a fresh
///     proxy {SESSIONID} (new IP); without a proxy back off 60s x attempt (same IP).
/// The browser stays open across events (Cloudflare clearance is reused) until a retry reopens it.
/// </summary>
public sealed class AxsPlaywrightClient : IAsyncDisposable
{
    private static readonly string[] ChallengeTitles =
        { "just a moment", "axs access info", "verification in progress", "attention required", "loading https://" };
    private static readonly string[] HardBlockMarkers = { "actively prevent automated bots" };
    private const string CommerceHost = "unifiedapicommerce";

    /// <summary>capture name -> substring of the commerce API URL (RECON.md 3b). Veritix =
    /// price/sections/offer_search; Marketplace = mp_offers (eventinfo/mapinfo kept as raw only
    /// if they arrive first, never waited for).</summary>
    private static readonly (string Name, string Fragment)[] Captures =
    {
        ("phase", "/phase?"),
        ("price", "/price?"),
        ("sections", "/sections?"),
        ("offer_search", "/offer/search?"),
        ("mp_eventinfo", "/axsmarketplace/eventinfo?"),
        ("mp_offers", "/axsmarketplace/offers?"),
        ("mp_mapinfo", "/axsmarketplace/mapinfo?"),
    };

    private readonly AxsFetchOptions _o;
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public string SessionId { get; private set; } = "";

    public AxsPlaywrightClient(AxsFetchOptions options) { _o = options; }

    private static void Log(string msg) => Console.Error.WriteLine("  " + msg);

    // ---- lifecycle -------------------------------------------------------------------------

    private async Task ReopenAsync()
    {
        await CloseAsync();
        SessionId = "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var proxyUrl = string.IsNullOrEmpty(_o.ProxyTemplate) ? "" : _o.ProxyTemplate.Replace("{SESSIONID}", SessionId);
        Log($"opening browser channel={_o.Channel ?? "bundled"} " + (proxyUrl != "" ? $"via proxy session={SessionId}" : "(no proxy)"));
        _pw = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _o.Headless,
            Channel = _o.Channel,
            Proxy = ParseProxy(proxyUrl),
            // Microsoft.Playwright is not patchright: drop the obvious automation flags
            // (navigator.webdriver, "--enable-automation" infobar). See dotnet/AXS/README.md.
            IgnoreDefaultArgs = _o.ReduceAutomationFlags ? new[] { "--enable-automation" } : null,
            Args = _o.ReduceAutomationFlags ? new[] { "--disable-blink-features=AutomationControlled" } : null,
        });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
    }

    private static Proxy? ParseProxy(string proxyUrl)
    {
        if (string.IsNullOrEmpty(proxyUrl)) return null;
        var uri = new Uri(proxyUrl, UriKind.Absolute);
        var proxy = new Proxy { Server = $"{uri.Scheme}://{uri.Host}:{uri.Port}" };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Username = Uri.UnescapeDataString(parts[0]);
            proxy.Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return proxy;
    }

    public async Task CloseAsync()
    {
        if (_browser != null) { try { await _browser.CloseAsync(); } catch { } }
        _pw?.Dispose();
        _browser = null;
        _context = null;
        _pw = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    // ---- helpers ---------------------------------------------------------------------------

    private static bool IsChallengeTitle(string title)
    {
        var t = (title ?? "").ToLowerInvariant();
        return ChallengeTitles.Any(c => t.Contains(c));
    }

    private static async Task<string> TitleAsync(IPage page)
    {
        try { return await page.TitleAsync() ?? ""; } catch { return ""; }
    }

    private static async Task<bool> HardBlockedAsync(IPage page)
    {
        try
        {
            var title = (await page.TitleAsync() ?? "").ToLowerInvariant();
            if (!title.Contains("access info")) return false;
            var text = (await page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''") ?? "").ToLowerInvariant();
            return HardBlockMarkers.Any(m => text.Contains(m));
        }
        catch { return false; }
    }

    /// <summary>POLL (never a fixed sleep) until the title stops looking like a challenge.</summary>
    private async Task SettleAsync(IPage page, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_o.SettleMaxSeconds);
        var title = "";
        while (DateTime.UtcNow < deadline)
        {
            title = await TitleAsync(page);
            if (title != "" && !IsChallengeTitle(title)) return;
            if (await HardBlockedAsync(page))
                throw new AxsBlockedException($"{what}: AXS hard block page ('actively prevent automated bots') - IP flagged");
            await Task.Delay(1000);
        }
        throw new AxsBlockedException($"{what}: challenge did not clear in {_o.SettleMaxSeconds}s (title='{title}')");
    }

    /// <summary>"/veritix/pre-flow/v2/{token}/phase" -> "pre-flow/phase".</summary>
    public static string EndpointLabel(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "veritix" && !Regex.IsMatch(p, "^[vV]\\d+$") && p.Length < 40);
        var label = string.Join("/", parts);
        return label == "" ? path : label;
    }

    private async Task<T> WithRetriesAsync<T>(string what, Func<Task<T>> fn)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= _o.Retries; attempt++)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                if (_context == null) await ReopenAsync();
                var result = await fn();
                Log($"{what} ok on attempt {attempt}/{_o.Retries} in {(DateTime.UtcNow - t0).TotalSeconds:0}s");
                return result;
            }
            catch (NotAnEventPageException) { throw; }
            catch (AxsNotOnSaleException) { throw; }
            catch (AxsBlockedException e) { last = e; }
            catch (Exception e) { last = new AxsBlockedException($"{e.GetType().Name}: {FirstLine(e.Message)}"); }

            Log($"!! {what} attempt {attempt}/{_o.Retries} failed: {last.Message}");
            await CloseAsync(); // next attempt = fresh proxy session / fresh browser
            if (attempt < _o.Retries)
            {
                var hasProxy = !string.IsNullOrEmpty(_o.ProxyTemplate);
                var wait = hasProxy ? _o.SleepSeconds * attempt : _o.NoProxyBackoffSeconds * attempt;
                Log($"   retrying in {wait:0}s ({(hasProxy ? "new proxy session" : "same IP, no proxy")})");
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
        }
        throw new AxsBlockedException($"{what}: gave up after {_o.Retries} sessions: {last?.Message}");
    }

    private static string FirstLine(string s) => (s ?? "").Split('\n')[0];

    // ---- event metadata --------------------------------------------------------------------

    public Task<Event> GetEventAsync(string eventUrl) => WithRetriesAsync("event page", async () =>
    {
        var page = await _context!.NewPageAsync();
        try
        {
            await page.GotoAsync(eventUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await SettleAsync(page, "event page");
            var raw = await page.EvaluateAsync<string>("() => (document.getElementById('__NEXT_DATA__')||{}).textContent || ''");
            if (string.IsNullOrEmpty(raw)) throw new AxsBlockedException("event page has no __NEXT_DATA__ (soft block?)");
            using var doc = JsonDocument.Parse(raw);
            var ev = AxsParser.ParseEvent(doc.RootElement);
            _o.OnRaw?.Invoke(ev.EventId, "event", AxsParser.PageProps(doc.RootElement).GetRawText());
            return ev;
        }
        finally { await page.CloseAsync(); }
    });

    // ---- inventory -------------------------------------------------------------------------

    public Task<AxsInventory> GetInventoryAsync(Event ev)
    {
        if (string.IsNullOrEmpty(ev.TicketUrl))
            throw new AxsNotOnSaleException($"event {ev.EventId} has no ticket URL (status='{ev.Status}')");
        return WithRetriesAsync("inventory", () => InventoryOnceAsync(ev));
    }

    private async Task<AxsInventory> InventoryOnceAsync(Event ev)
    {
        var captured = new ConcurrentDictionary<string, string>();
        var statuses = new ConcurrentDictionary<string, int>();
        var commerceErrors = new ConcurrentQueue<string>();
        var pending = new ConcurrentBag<Task>();
        var mergeLock = new object();
        var inv = new AxsInventory();
        var page = await _context!.NewPageAsync();

        async Task Grab(IResponse resp, string name)
        {
            try
            {
                var body = await resp.TextAsync();
                if (resp.Status != 200) return;
                if (name == "offer_search")
                {
                    lock (mergeLock)
                    {
                        // several offer/search calls (e.g. per price-level batch) -> merge offers
                        if (captured.TryGetValue(name, out var prev))
                        {
                            var a = JsonNode.Parse(prev)!.AsObject();
                            var b = JsonNode.Parse(body)!.AsObject();
                            var offers = a["offers"] as JsonArray ?? new JsonArray();
                            if (b["offers"] is JsonArray more)
                                foreach (var o in more.ToList()) { more.Remove(o); offers.Add(o); }
                            a["offers"] = offers;
                            captured[name] = a.ToJsonString();
                        }
                        else captured[name] = body;
                    }
                }
                else
                {
                    using (JsonDocument.Parse(body)) { } // only keep valid JSON
                    captured[name] = body;
                }
            }
            catch (Exception e) { statuses[name + "_error"] = -1; Log($"   capture {name} failed: {FirstLine(e.Message)}"); }
        }

        void OnResponse(object? sender, IResponse resp)
        {
            var url = resp.Url;
            if (!url.Contains(CommerceHost)) return;
            // cross-origin API -> CORS preflight OPTIONS responses match the same URLs but have no body
            if (resp.Request.Method == "OPTIONS") return;
            // every commerce call (session, start-flow...) - a 403 on POST /session leaves the app
            // on "Loading..." with phase=200 and nothing else (seen 2026-09-24)
            if (resp.Status >= 400) commerceErrors.Enqueue($"{EndpointLabel(url)}:{resp.Status}");
            foreach (var (name, frag) in Captures)
                if (url.Contains(frag))
                {
                    statuses[name] = resp.Status;
                    pending.Add(Grab(resp, name));
                }
        }

        page.Response += OnResponse;
        try
        {
            await page.GotoAsync(ev.TicketUrl!, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            var t0 = DateTime.UtcNow;
            bool queueSeen = false, leftQueue = false;
            DateTime? sectionsAt = null, challengeSince = null;
            while (true)
            {
                await Task.Delay(1000);
                var url = page.Url;
                if (url.Contains("queue-it.net") || new Uri(url).Host.StartsWith("queue."))
                {
                    if (!queueSeen) { Log("in Queue-it waiting room..."); inv.Notes.Add("queue-it"); queueSeen = true; }
                    if ((DateTime.UtcNow - t0).TotalSeconds > _o.QueueMaxSeconds)
                        throw new AxsNotOnSaleException($"still in Queue-it after {_o.QueueMaxSeconds}s");
                    continue;
                }
                if (queueSeen && !leftQueue) { leftQueue = true; t0 = DateTime.UtcNow; }

                var title = await TitleAsync(page);
                if (IsChallengeTitle(title))
                {
                    challengeSince ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - challengeSince.Value).TotalSeconds > _o.SettleMaxSeconds)
                        throw new AxsBlockedException($"ticket page stuck on challenge {_o.SettleMaxSeconds}s (title='{title.ToLowerInvariant()}')");
                }
                else challengeSince = null;
                if (await HardBlockedAsync(page))
                    throw new AxsBlockedException("AXS hard block page ('actively prevent automated bots') - IP flagged");
                var bad = commerceErrors.Where(e => e.EndsWith(":403") || e.EndsWith(":429") || e.EndsWith(":440")).ToList();
                if (bad.Count > 0)
                    throw new AxsBlockedException($"commerce API refused: [{string.Join(", ", bad)}]");

                if (captured.ContainsKey("mp_offers"))
                    break; // marketplace: offers IS the inventory - eventinfo/mapinfo never waited for
                if (captured.ContainsKey("price") && captured.ContainsKey("sections") && sectionsAt == null)
                    sectionsAt = DateTime.UtcNow;
                if (captured.ContainsKey("offer_search") && captured.ContainsKey("price"))
                {
                    await Task.Delay(1000); // let a trailing offer/search batch land
                    break;
                }
                if (sectionsAt != null && (DateTime.UtcNow - sectionsAt.Value).TotalSeconds > _o.OfferSearchGraceSeconds)
                {
                    inv.Notes.Add("no offer/search call (GA or best-available flow?) - seats empty");
                    break;
                }
                if ((DateTime.UtcNow - t0).TotalSeconds > _o.InventoryTimeoutSeconds)
                {
                    if (!statuses.ContainsKey("phase"))
                    {
                        if (IsChallengeTitle(title))
                            throw new AxsBlockedException($"ticket page stuck on challenge (title='{title}')");
                        // marketplace page loaded but its offers call never fired (proxy, 2026-09-26):
                        // a silent block, not "off sale" - retry with a new session.
                        if (url.Contains("tix.axs.com"))
                            throw new AxsBlockedException($"marketplace page without offers after {_o.InventoryTimeoutSeconds}s (title='{title}')");
                        throw new AxsNotOnSaleException($"no Veritix flow after {_o.InventoryTimeoutSeconds}s (url={Trunc(url, 80)}, title='{title}')");
                    }
                    throw new AxsBlockedException($"inventory calls incomplete after {_o.InventoryTimeoutSeconds}s: " +
                        $"captured={{{string.Join(", ", statuses.Select(kv => kv.Key + ": " + kv.Value))}}} errors=[{string.Join(", ", commerceErrors)}]");
                }
            }
            await Task.WhenAll(pending.ToArray());
        }
        finally
        {
            page.Response -= OnResponse;
            await page.CloseAsync();
        }

        foreach (var name in Captures.Select(c => c.Name))
            if (captured.TryGetValue(name, out var json))
            {
                inv.Captured[name] = json;
                _o.OnRaw?.Invoke(ev.EventId, name, json);
            }
        return inv;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
