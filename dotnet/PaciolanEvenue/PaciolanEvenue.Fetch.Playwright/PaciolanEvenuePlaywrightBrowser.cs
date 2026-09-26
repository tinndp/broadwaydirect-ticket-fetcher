using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

namespace PaciolanEvenue.Fetch.Playwright;

/// <summary>A real PerimeterX block was detected (page or API) - distinct from an ordinary network
/// error. Matches python/paciolanevenue/client.py's PerimeterXBlocked and
/// PaciolanEvenue.Fetch (WebView2)'s PaciolanEvenueBlockedException.</summary>
public sealed class PaciolanEvenuePlaywrightBlockedException : Exception
{
    public PaciolanEvenuePlaywrightBlockedException(string message) : base(message) { }
}

/// <summary>The page loaded fine (not a PerimeterX block) but did not render a single event - the
/// itemCd is wrong/does not exist. Retrying with a fresh proxy session will NOT fix this. Matches
/// python/paciolanevenue/parser.py's NotAnEventPage.</summary>
public sealed class PaciolanEvenuePlaywrightNotAnEventException : Exception
{
    public PaciolanEvenuePlaywrightNotAnEventException(string message) : base(message) { }
}

/// <summary>
/// One real (headless=false) Chromium window used to get past PerimeterX and then run same-origin
/// fetch() calls in the page context - the cross-platform counterpart of
/// PaciolanEvenue.Fetch/PaciolanEvenueBrowser.cs (WebView2), using Microsoft.Playwright instead
/// (works on macOS/Linux, not just Windows). Ported from python/paciolanevenue/client.py's
/// PaciolanEvenueClient._open()/_settle()/_fetch_in_page(), the PROVEN-working implementation
/// (2/2 real runs against Purdue/Oklahoma) - this is the closest analog to that file of anything in
/// this repo, since both use Playwright's page.EvaluateAsync directly (unlike WebView2's
/// ExecuteScriptAsync, which does not reliably await a returned Promise - see
/// BroadwayDirect.Fetch/ProxyEnvironmentPool.cs for that full analysis; not needed here).
///
/// Uses plain Microsoft.Playwright, NOT patchright (patchright is Python-only, no .NET port
/// exists). The ORIGINAL .NET 8 demo (~/Desktop/crawler-playbook/PaciolanEvenue/
/// PaciolanEvenueCrawler/PaciolanBrowser.cs) found vanilla Playwright reliably blocked by
/// PerimeterX in isolation - but that was BEFORE the retry-with-fresh-proxy-session discovery
/// (python/paciolanevenue/docs/EVENUE_PERIMETERX_FINDINGS.md) and the soft-block (missing __NEXT_DATA__, no text marker)
/// discovery, both ported in here. Whether retry alone gets vanilla Playwright through as
/// reliably as patchright is NOT yet proven either way - this class does not claim to guarantee a
/// PerimeterX bypass, same caveat as every other PerimeterX client in this repo.
/// </summary>
public sealed class PaciolanEvenuePlaywrightBrowser : IAsyncDisposable
{
    /// <summary>Confirmed against real evenue.net PerimeterX block pages (2026-09-21/22) - see
    /// python/paciolanevenue/docs/EVENUE_PERIMETERX_FINDINGS.md. "Please verify you are a human" is still an unverified
    /// guess (kept for parity).</summary>
    private static readonly string[] BlockMarkers =
        { "Access to this page has been denied", "px-captcha", "Please verify you are a human" };

    private readonly string _proxyTemplate;
    private readonly bool _headless;
    private readonly int _timeoutSeconds;
    private readonly double _settleMaxSeconds;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public IPage? Page { get; private set; }

    /// <summary>
    /// The event page's OWN seat-availability responses, captured via <see cref="IPage.Response"/>
    /// (listener attached before navigation in <see cref="OpenFreshAsync"/>): decoded URL -> (status, body).
    /// Why (python/paciolanevenue/docs/EVENUE_OPTIMIZATION_FINDINGS.md, 2026-09-24): our own in-page fetch() runs in the page's
    /// MAIN world here (Microsoft.Playwright has no isolated-world evaluate, unlike patchright Python) and
    /// PerimeterX answered it 403 on every run (0/1 stock, 0/2 Patchright); the page calls the same API
    /// itself, and capturing that response worked 3/3 runs with stock Microsoft.Playwright 1.48.
    /// </summary>
    private readonly Dictionary<string, (int Status, string Body)> _capturedSeatResponses = new();
    private readonly object _captureGate = new();

    /// <summary>Latest captured seat-availability response whose decoded URL contains
    /// <paramref name="decodedPathFragment"/>, or null.</summary>
    public (int Status, string Body)? TryGetCapturedSeatResponse(string decodedPathFragment)
    {
        lock (_captureGate)
        {
            foreach (var kv in _capturedSeatResponses)
                if (kv.Key.Contains(decodedPathFragment, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        }
        return null;
    }

    /// <summary>Session id actually used for the CURRENT browser's proxy username (after
    /// "{SESSIONID}" substitution), or "" when no proxy is configured.</summary>
    public string CurrentSessionId { get; private set; } = "";

    public PaciolanEvenuePlaywrightBrowser(string proxyTemplate, bool headless = false,
        int timeoutSeconds = 20, double settleMaxSeconds = 15.0)
    {
        _proxyTemplate = proxyTemplate ?? "";
        _headless = headless;
        _timeoutSeconds = timeoutSeconds;
        _settleMaxSeconds = settleMaxSeconds;
    }

    private static string FreshSessionId() => "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static string ApplySession(string template, string sessionId) =>
        string.IsNullOrEmpty(template) ? template : template.Replace("{SESSIONID}", sessionId);

    /// <summary>Parses "scheme://[user:pass@]host:port" into Playwright's Proxy shape. Accepts the
    /// raw "host:port:user:pass" providers hand out too, via BroadwayDirect.Core.Proxy.ProxyUri.Normalize
    /// (caller normalizes before passing in, matching every other proxy entry point in this repo).</summary>
    private static Proxy? ParseProxy(string? proxyUrl)
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

    /// <summary>Closes any existing browser and opens a BRAND NEW one (fresh process, fresh proxy
    /// {SESSIONID}) - matches python client.py's own _open() exactly: PerimeterX was NOT reliably
    /// passed by one attempt during probing, so a genuinely fresh browser + egress IP per retry is
    /// the actual mitigation (python/paciolanevenue/docs/EVENUE_PERIMETERX_FINDINGS.md), not context/cookie reuse.</summary>
    public async Task OpenFreshAsync(string url)
    {
        await CloseAsync();

        var sessionId = FreshSessionId();
        CurrentSessionId = sessionId;
        var proxyUrl = ApplySession(_proxyTemplate, sessionId);

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _headless,
            Proxy = ParseProxy(proxyUrl),
        });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        Page = await _context.NewPageAsync();
        lock (_captureGate) _capturedSeatResponses.Clear();
        Page.Response += OnPageResponse;

        try
        {
            await Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _timeoutSeconds * 1000,
            });
        }
        catch (Exception e)
        {
            throw new PaciolanEvenuePlaywrightBlockedException($"navigation to {url} failed/timed out: {e.Message}");
        }

        await SettleAsync();
    }

    /// <summary>POLLS (not a fixed sleep) until the page no longer looks like a PerimeterX
    /// challenge, up to settleMaxSeconds - matches client.py's _settle() exactly, including the
    /// post-clear wait_for_load_state("networkidle", timeout=3000) step (Playwright supports this
    /// natively - no readyState-polling workaround needed here, unlike the WebView2 port where
    /// that step was originally dropped and had to be added back, see PaciolanEvenueBrowser.cs's
    /// own bug-fix note). Throws PaciolanEvenuePlaywrightBlockedException if a block marker is
    /// still present when the budget runs out.</summary>
    private async Task SettleAsync()
    {
        var elapsedMs = 0.0;
        var maxMs = _settleMaxSeconds * 1000;
        while (elapsedMs < maxMs)
        {
            var html = await Page!.ContentAsync();
            if (!BlockMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 3000 });
                }
                catch
                {
                    // best-effort - some pages (e.g. the seat map) keep background polling alive forever
                }
                return;
            }
            await Page.WaitForTimeoutAsync(500);
            elapsedMs += 500;
        }

        var finalHtml = await Page!.ContentAsync();
        var matched = BlockMarkers.Where(m => finalHtml.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
        throw new PaciolanEvenuePlaywrightBlockedException(
            $"page still shows a block marker after {_settleMaxSeconds}s: {string.Join(", ", matched)}");
    }

    /// <summary>Calls fetch(path, {credentials:'include'}) INSIDE the current page (uses the tab's
    /// real network stack/cookies/TLS fingerprint) and returns (status, body). Playwright's
    /// EvaluateAsync correctly awaits the async function directly - no postMessage workaround
    /// needed (see class doc comment).</summary>
    /// <param name="postJsonBody">null = GET; otherwise POST with this JSON body (GraphQL).</param>
    public async Task<(int Status, string Body)> FetchInPageAsync(string path, string? postJsonBody = null)
    {
        var json = await Page!.EvaluateAsync<string>(
            @"async ({ path, timeoutMs, postBody }) => {
                const ctrl = new AbortController();
                const t = setTimeout(() => ctrl.abort(), timeoutMs);
                try {
                    const init = postBody === null
                        ? { credentials: 'include', signal: ctrl.signal }
                        : { method: 'POST', credentials: 'include', signal: ctrl.signal,
                            headers: { 'content-type': 'application/json' }, body: postBody };
                    const r = await fetch(path, init);
                    const body = await r.text();
                    return JSON.stringify({ status: r.status, body });
                } catch (e) {
                    return JSON.stringify({ status: 0, body: String((e && e.message) || e) });
                } finally {
                    clearTimeout(t);
                }
            }",
            new { path, timeoutMs = _timeoutSeconds * 1000, postBody = postJsonBody });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? "");
    }

    private async void OnPageResponse(object? sender, IResponse resp)
    {
        try
        {
            if (resp.Request.Method != "GET" || !resp.Url.Contains("/pac-api/seat-availability/", StringComparison.OrdinalIgnoreCase))
                return;
            var body = await resp.TextAsync();
            lock (_captureGate) _capturedSeatResponses[Uri.UnescapeDataString(resp.Url)] = (resp.Status, body);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  !! could not read captured seat-availability response: {e.Message}");
        }
    }

    private async Task CloseAsync()
    {
        if (_context != null) { try { await _context.CloseAsync(); } catch { } }
        if (_browser != null) { try { await _browser.CloseAsync(); } catch { } }
        _playwright?.Dispose();
        _context = null;
        _browser = null;
        _playwright = null;
        Page = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
