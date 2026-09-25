using PaciolanEvenue.Core.Models;
using PaciolanEvenue.Core.Parsing;

namespace PaciolanEvenue.Fetch.Playwright;

/// <summary>
/// Fetches one eVenue event's page + seat availability through a real browser (Playwright,
/// cross-platform), getting past PerimeterX. 1:1 port of python/paciolanevenue/client.py's
/// PaciolanEvenueClient - see PaciolanEvenuePlaywrightBrowser's doc comment for the technology
/// choice, and this class's WebView2 sibling (PaciolanEvenue.Fetch/PaciolanEvenueClient.cs) for
/// the exact same retry/soft-block logic, ported here identically.
/// </summary>
public sealed class PaciolanEvenuePlaywrightClient : IAsyncDisposable
{
    private readonly PaciolanEvenuePlaywrightBrowser _browser;
    private readonly int _retries;
    private readonly double _sleepSeconds;
    private readonly int _captureWaitSeconds;

    private (string Host, string SeasonCd, string ItemCd)? _currentKey;
    private string _currentEventUrl = "";

    public PaciolanEvenuePlaywrightClient(string proxyTemplate = "", bool headless = false,
        int retries = 4, double sleepSeconds = 0.5, int timeoutSeconds = 20, double settleMaxSeconds = 15.0,
        int captureWaitSeconds = 15)
    {
        _retries = retries;
        _sleepSeconds = sleepSeconds;
        _captureWaitSeconds = captureWaitSeconds;
        _browser = new PaciolanEvenuePlaywrightBrowser(proxyTemplate, headless, timeoutSeconds, settleMaxSeconds);
    }

    /// <summary>Returns EventPageData (incl. PriceLevels). Retries with a fresh proxy session/fresh
    /// browser up to `retries` times if PerimeterX blocks the page (hard block, OR the soft-block
    /// signature: real title, HTML has no __NEXT_DATA__ at all - see the doc comment inline below;
    /// found via a real Windows run against a KNOWN-GOOD event, F26/F03, on the WebView2 sibling).
    /// Throws PaciolanEvenuePlaywrightNotAnEventException immediately (no retry) if the page IS
    /// real Next.js output but genuinely not a single-event page (wrong itemCd).</summary>
    public async Task<EventPageData> GetEventAsync(string host, string seasonCd, string itemCd, CancellationToken ct = default)
    {
        var url = $"https://{host}/event/{seasonCd}/{itemCd}";
        Exception? lastErr = null;

        for (var attempt = 1; attempt <= _retries; attempt++)
        {
            try
            {
                await _browser.OpenFreshAsync(url);
                var html = await _browser.Page!.ContentAsync();

                if (!html.Contains("__NEXT_DATA__", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PaciolanEvenuePlaywrightBlockedException(
                        $"paciolanevenue: {url} has no __NEXT_DATA__ at all - suspected PerimeterX soft-block (real title, stripped content). {DescribeHtmlForDiagnosis(html)}");
                }

                var ev = EventPageParser.Parse(html, host, seasonCd, itemCd);
                if (!ev.IsEventPage)
                    throw new PaciolanEvenuePlaywrightNotAnEventException(
                        $"paciolanevenue: {url} did not render a single event (context != 'eventdetailpage') - not a PerimeterX block, not retrying. {DescribeHtmlForDiagnosis(html)}");

                _currentKey = (host, seasonCd, itemCd);
                _currentEventUrl = url;
                return ev;
            }
            catch (PaciolanEvenuePlaywrightBlockedException e)
            {
                lastErr = e;
                Console.Error.WriteLine($"  !! attempt {attempt}/{_retries} blocked: {e.Message}");
                if (attempt < _retries)
                    await Task.Delay(TimeSpan.FromSeconds(_sleepSeconds * attempt * 2), ct);
            }
        }

        throw new PaciolanEvenuePlaywrightBlockedException($"gave up after {_retries} proxy sessions: {lastErr?.Message}");
    }

    /// <summary>Must be called after GetEventAsync for the SAME event. Returns (rows,
    /// coverageNote). Uses the seat-availability response the event page requested ITSELF
    /// (captured, not fetched - see python/EVENUE_OPTIMIZATION_FINDINGS.md). Retries the WHOLE
    /// session (fresh proxy id + re-navigate, which re-captures) on a 403 / non-JSON body / no
    /// captured response, same as GetEventAsync's retry.</summary>
    public async Task<SeatCrawlResult> GetSeatAvailabilityAsync(EventPageData ev, CancellationToken ct = default)
    {
        if (_currentKey is not { } key || key.Host != ev.Host || key.SeasonCd != ev.SeasonCd || key.ItemCd != ev.ItemCd)
            throw new InvalidOperationException("paciolanevenue: GetSeatAvailabilityAsync called for a different event than the open session - call GetEventAsync first");

        // Match the page's own call by its (decoded) event-id segment - query string and %-encoding differ
        // between our BuildPath and what the page sends.
        var eventSegment = $"/pac-api/seat-availability/event-id/{ev.DataAccountId}:{ev.SeasonCd}:{ev.ItemCd}/seats";
        string? lastErr = null;

        for (var attempt = 1; attempt <= _retries; attempt++)
        {
            int status;
            string body;
            // The event page requests seat availability by itself shortly after load (~2-3s after
            // settle in recon). We capture that response instead of calling fetch() ourselves - see
            // PaciolanEvenuePlaywrightBrowser.TryGetCapturedSeatResponse for why.
            // EXCEPT quantity-only pages (ALLOWSEATMAP=False, e.g. GA soccer/volleyball): they never
            // request it (recon 2026-09-25), so there is nothing to capture - fetch it in the page.
            var captured = ev.AllowSeatMap == false
                ? await _browser.FetchInPageAsync(SeatAvailabilityParser.BuildPath(ev))
                : await WaitForCapturedSeatResponseAsync(eventSegment, ct);
            // Seat-map page that has not requested it yet (slow map component over a proxy, seen on
            // UCLA Royce Hall 2026-09-25): try our own in-page fetch once before a new session.
            if (!captured.HasValue)
            {
                Console.Error.WriteLine($"  .. no captured seat-availability after {_captureWaitSeconds}s, fetching it in the page");
                captured = await _browser.FetchInPageAsync(SeatAvailabilityParser.BuildPath(ev));
            }
            if (captured.HasValue)
            {
                status = captured.Value.Status;
                body = captured.Value.Body;
            }
            else
            {
                status = 0;
                body = $"the event page did not request seat availability within {_captureWaitSeconds}s";
            }

            if (status == 200)
            {
                try
                {
                    return SeatAvailabilityParser.Parse(body, ev, msg => Console.Error.WriteLine(msg));
                }
                catch (InvalidOperationException e)
                {
                    lastErr = $"HTTP 200 but body was not valid JSON: {e.Message}";
                }
            }
            else
            {
                lastErr = $"HTTP {status}: {(body.Length > 200 ? body[..200] : body)}";
            }

            Console.Error.WriteLine($"  !! seat-availability attempt {attempt}/{_retries} failed: {lastErr}");
            if (attempt < _retries)
            {
                await Task.Delay(TimeSpan.FromSeconds(_sleepSeconds * attempt * 2), ct);
                await _browser.OpenFreshAsync(_currentEventUrl);
            }
        }

        throw new PaciolanEvenuePlaywrightBlockedException($"seat-availability gave up after {_retries} attempts: {lastErr}");
    }

    /// <summary>Fills ev.HoldCodes / ev.SeatingTypes from GraphQL maps_eventMap (in-page POST, same
    /// query as the event page's map component). Call after GetSeatAvailabilityAsync for the same
    /// event. Never throws: on failure both stay null (grouping falls back to AVAILABLE == 1) and the
    /// returned note says why - it goes into the coverage line.</summary>
    public async Task<string> GetEventMapAsync(EventPageData ev)
    {
        try
        {
            var (status, body) = await _browser.FetchInPageAsync(EventMapQuery.GqlPath, EventMapQuery.BuildBody(ev));
            if (status != 200) return $"map=unavailable(HTTP {status})";
            EventMapQuery.Apply(ev, body);
            return "map=ok";
        }
        catch (Exception e)
        {
            ev.HoldCodes = null;
            ev.SeatingTypes = null;
            return $"map=unavailable({(e.Message.Length > 80 ? e.Message[..80] : e.Message)})";
        }
    }

    private async Task<(int Status, string Body)?> WaitForCapturedSeatResponseAsync(string eventSegment, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(_captureWaitSeconds);
        while (true)
        {
            var r = _browser.TryGetCapturedSeatResponse(eventSegment);
            if (r.HasValue || DateTime.UtcNow >= until)
                return r;
            await Task.Delay(250, ct);
        }
    }

    private static string DescribeHtmlForDiagnosis(string html)
    {
        try
        {
            html ??= "";
            var titleMatch = System.Text.RegularExpressions.Regex.Match(html, "<title[^>]*>([^<]*)</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "(no <title>)";
            var hasNextData = html.IndexOf("__NEXT_DATA__", StringComparison.OrdinalIgnoreCase) >= 0;
            var contextMatch = System.Text.RegularExpressions.Regex.Match(html, "\"context\"\\s*:\\s*\"([^\"]*)\"");
            var context = contextMatch.Success ? contextMatch.Groups[1].Value : "(not found)";
            return $"Diag: title={title} hasNextData={hasNextData} context={context} len={html.Length}";
        }
        catch (Exception e)
        {
            return $"Diag: (failed to describe HTML: {e.Message})";
        }
    }

    public async ValueTask DisposeAsync() => await _browser.DisposeAsync();
}
