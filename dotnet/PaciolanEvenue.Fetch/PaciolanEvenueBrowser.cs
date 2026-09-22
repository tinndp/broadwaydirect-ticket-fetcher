using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BroadwayDirect.Core.Proxy;
using BroadwayDirect.Fetch;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PaciolanEvenue.Fetch;

/// <summary>
/// One real (non-headless) WebView2 window used to get past <b>PerimeterX</b> and then run
/// same-origin fetch() calls in the page context. The WebView2 counterpart of
/// python/paciolanevenue/client.py's PaciolanEvenueClient._open()/_settle().
///
/// UNLIKE <see cref="BroadwayDirect.Fetch.ProxyEnvironmentPool"/> (Cloudflare - one warmed session
/// reused across calls, keyed by proxy) and <see cref="StubHub.Fetch.DataDomeBrowser"/>
/// (DataDome - one persistent control, cookies cleared per event), this class opens a BRAND NEW
/// WebView2 environment (new profile dir, new proxy {SESSIONID}) on every <see cref="OpenFreshAsync"/>
/// call, matching the Python client's own <c>_open()</c> exactly: PerimeterX was NOT reliably
/// passed by one attempt during probing (see EVENUE_PERIMETERX_FINDINGS.md), so a genuinely fresh
/// egress IP + browser profile per retry is the actual mitigation, not context/cookie reuse. This
/// is the "retry-with-fresh-session" finding from that document, ported literally.
/// </summary>
public sealed class PaciolanEvenueBrowser : IAsyncDisposable
{
    /// <summary>Confirmed against real evenue.net PerimeterX block pages during the Python
    /// package's live runs (2026-09-21) - see EVENUE_PERIMETERX_FINDINGS.md. "Please verify you
    /// are a human" is still an unverified guess (kept for parity).</summary>
    private static readonly string[] BlockMarkers =
        { "Access to this page has been denied", "px-captcha", "Please verify you are a human" };

    private const char ResultSeparator = '\u0001';

    private readonly WebView2Host _host;
    private readonly string _proxyTemplate;
    private readonly PaciolanEvenueFetchOptions _opt;

    private readonly Dictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly object _pendingGate = new();

    private CoreWebView2Environment? _env;
    private WebView2? _control;
    private System.Windows.Forms.Form? _form;

    /// <summary>Session id actually used for the CURRENT WebView2 environment's proxy username
    /// (after "{SESSIONID}" substitution), or "" when no proxy is configured. Exposed for
    /// logging.</summary>
    public string CurrentSessionId { get; private set; } = "";

    public PaciolanEvenueBrowser(WebView2Host host, string proxyTemplate, PaciolanEvenueFetchOptions opt)
    {
        _host = host;
        _proxyTemplate = proxyTemplate ?? "";
        _opt = opt;
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(_opt.TimeoutSeconds);

    private static string FreshSessionId() => "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>Substitutes the literal "{SESSIONID}" placeholder (if present) in the proxy
    /// template's username with a fresh id - same convention as this repo's other {SESSIONID}
    /// proxies (TM/Broadway in the .NET Rowing bot, PACIOLAN_PROXY_USER in the Python package).</summary>
    private static string ApplySession(string template, string sessionId) =>
        string.IsNullOrEmpty(template) ? template : template.Replace("{SESSIONID}", sessionId);

    /// <summary>Disposes any existing WebView2 environment/control, opens a BRAND NEW one (fresh
    /// profile dir, fresh proxy {SESSIONID}), navigates to <paramref name="url"/>, and polls for a
    /// PerimeterX block marker to clear. Throws PaciolanEvenueBlockedException if it never clears -
    /// caller (PaciolanEvenueClient) decides whether to retry with yet another fresh session.</summary>
    public async Task OpenFreshAsync(string url)
    {
        await _host.RunOnUiThreadAsync(async () =>
        {
            if (_control != null)
            {
                _control.Dispose();
                _form?.Dispose();
                _control = null;
                _form = null;
                _env = null;
            }

            var sessionId = FreshSessionId();
            CurrentSessionId = sessionId;
            var proxy = ApplySession(_proxyTemplate, sessionId);

            var userDataFolder = Path.Combine(
                Path.GetTempPath(), "PaciolanEvenueFetch",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))));
            Directory.CreateDirectory(userDataFolder);

            var envOptions = new CoreWebView2EnvironmentOptions();
            string? proxyUser = null, proxyPassword = null;
            if (!string.IsNullOrEmpty(proxy))
            {
                var parsed = ProxyUri.Parse(proxy);
                envOptions.AdditionalBrowserArguments = $"--proxy-server={parsed.SwitchValue}";
                proxyUser = parsed.User;
                proxyPassword = parsed.Password;
            }

            _env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);

            // A real, rendering window (off-screen) - PerimeterX blocks headless Chromium, same
            // reason headless=False is required on the Python side.
            _form = new System.Windows.Forms.Form
            {
                ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-3000, -3000),
                Width = 1280,
                Height = 900,
            };
            _control = new WebView2 { Dock = System.Windows.Forms.DockStyle.Fill };
            _form.Controls.Add(_control);
            _form.Show();

            await _control.EnsureCoreWebView2Async(_env);
            _control.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            if (!string.IsNullOrEmpty(proxyUser))
            {
                _control.CoreWebView2.BasicAuthenticationRequested += (_, e) =>
                {
                    e.Response.UserName = proxyUser;
                    e.Response.Password = proxyPassword ?? "";
                };
            }

            try
            {
                await NavigateAsync(url, Timeout);
            }
            catch (Exception e)
            {
                throw new PaciolanEvenueBlockedException($"navigation to {url} failed/timed out: {e.Message}");
            }

            await SettleAsync();
            return true;
        });
    }

    /// <summary>POLLS (not a fixed sleep) until the page no longer looks like a PerimeterX
    /// challenge, up to SettleMaxSeconds - same idea as the Python client's _settle(). Throws
    /// PaciolanEvenueBlockedException if a block marker is still present when the budget runs
    /// out.</summary>
    private async Task SettleAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_opt.SettleMaxSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var html = await ReadStringAsync("document.documentElement.outerHTML");
            if (!BlockMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return;
            await Task.Delay(500);
        }

        var finalHtml = await ReadStringAsync("document.documentElement.outerHTML");
        var matched = BlockMarkers.Where(m => finalHtml.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
        throw new PaciolanEvenueBlockedException(
            $"page still shows a block marker after {_opt.SettleMaxSeconds}s: {string.Join(", ", matched)}");
    }

    private async Task NavigateAsync(string url, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult();
        _control!.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            _control.CoreWebView2.Navigate(url);
            using var cts = new CancellationTokenSource(timeout);
            await using (cts.Token.Register(() => tcs.TrySetException(
                             new TimeoutException($"paciolanevenue: timeout navigating to {url}"))))
            {
                await tcs.Task;
            }
        }
        finally
        {
            _control.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private async Task<string> ReadStringAsync(string expression)
    {
        var json = await _control!.CoreWebView2.ExecuteScriptAsync(expression);
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>Returns the current page's full HTML (document.documentElement.outerHTML) for
    /// EventPageParser to regex the __NEXT_DATA__ script tag out of. Reading outerHTML is a
    /// synchronous script, so ExecuteScriptAsync marshals its return value correctly (unlike an
    /// async fetch() call - see FetchInPageAsync's own note on why that one needs postMessage).</summary>
    public Task<string> ReadOuterHtmlAsync() =>
        _host.RunOnUiThreadAsync(() => ReadStringAsync("document.documentElement.outerHTML"));

    /// <summary>Runs fetch(path, {credentials:'include'}) INSIDE the current page and returns
    /// (status, body) via postMessage - NOT via ExecuteScriptAsync's return value (see
    /// BroadwayDirect.Fetch/ProxyEnvironmentPool.cs's NOTE for why).</summary>
    public Task<(int Status, string Body)> FetchInPageAsync(string path)
    {
        return _host.RunOnUiThreadAsync(async () =>
        {
            var requestId = Guid.NewGuid().ToString("N");
            var requestIdJson = JsonSerializer.Serialize(requestId);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate) { _pending[requestId] = tcs; }

            try
            {
                var script = $$"""
                    (async () => {
                        try {
                            const r = await fetch({{JsonSerializer.Serialize(path)}}, {
                                credentials: 'include',
                                headers: { "Accept": "application/json, text/plain, */*" }
                            });
                            const body = await r.text();
                            window.chrome.webview.postMessage({{requestIdJson}} + "\u0001" + String(r.status) + "\u0001" + body);
                        } catch (e) {
                            window.chrome.webview.postMessage({{requestIdJson}} + "\u0001" + "0\u0001" + String(e));
                        }
                    })();
                    """;

                await _control!.CoreWebView2.ExecuteScriptAsync(script);

                using var cts = new CancellationTokenSource(Timeout);
                using var reg = cts.Token.Register(() => tcs.TrySetException(
                    new TimeoutException($"paciolanevenue: timed out waiting for the postMessage result from {path}")));

                var text = await tcs.Task;
                var sep = text.IndexOf(ResultSeparator);
                if (sep < 0 || !int.TryParse(text[..sep], out var status))
                    throw new InvalidOperationException($"paciolanevenue: could not parse the postMessage result. Raw: {text}");

                var body = text[(sep + 1)..];
                return (status, body);
            }
            finally
            {
                lock (_pendingGate) { _pending.Remove(requestId); }
            }
        });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();
        if (msg == null) return;
        var sep = msg.IndexOf(ResultSeparator);
        if (sep < 0) return;
        var requestId = msg[..sep];
        var payload = msg[(sep + 1)..];

        TaskCompletionSource<string>? tcs;
        lock (_pendingGate)
        {
            if (!_pending.Remove(requestId, out tcs)) return;
        }
        tcs.TrySetResult(payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_control == null) return;
        await _host.RunOnUiThreadAsync(() =>
        {
            _control.Dispose();
            _form?.Dispose();
            return Task.CompletedTask;
        });
        _control = null;
        _form = null;
        _env = null;
    }
}

/// <summary>A real PerimeterX block was detected (page or API) - distinct from an ordinary
/// network error. Matches python/paciolanevenue/client.py's PerimeterXBlocked.</summary>
public sealed class PaciolanEvenueBlockedException : Exception
{
    public PaciolanEvenueBlockedException(string message) : base(message) { }
}
