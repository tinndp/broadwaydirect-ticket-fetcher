using BroadwayDirect.Fetch;
using PaciolanEvenue.Core.Models;
using PaciolanEvenue.Core.Parsing;

namespace PaciolanEvenue.Fetch;

/// <summary>
/// Fetches one eVenue event's page + seat availability through a real browser (WebView2), getting
/// past PerimeterX. 1:1 port of python/paciolanevenue/client.py's PaciolanEvenueClient - see that
/// file's module docstring for the full strategy (retry-with-fresh-proxy-session, NOT a stealth
/// trick - PerimeterX bypass here is not guaranteed, see README_DOTNET.md / EVENUE_PERIMETERX_FINDINGS.md).
///
/// One <see cref="WebView2Host"/> (STA pump) + one <see cref="PaciolanEvenueBrowser"/> per client -
/// PaciolanEvenue.Api pools one client per proxy template, matching python/paciolanevenue/api.py's
/// _pick_proxy() + per-request client lifetime.
/// </summary>
public sealed class PaciolanEvenueClient : IAsyncDisposable
{
    private readonly PaciolanEvenueFetchOptions _opt;
    private readonly WebView2Host _host;
    private readonly PaciolanEvenueBrowser _browser;

    private (string Host, string SeasonCd, string ItemCd)? _currentKey;
    private string _currentEventUrl = "";

    public PaciolanEvenueClient(string proxyTemplate = "", PaciolanEvenueFetchOptions? options = null)
    {
        _opt = options ?? new PaciolanEvenueFetchOptions();
        _host = new WebView2Host();
        _browser = new PaciolanEvenueBrowser(_host, proxyTemplate, _opt);
    }

    /// <summary>Returns EventPageData (incl. PriceLevels). Retries with a fresh proxy session up
    /// to Options.Retries times if PerimeterX blocks the page. Throws PaciolanEvenueBlockedException
    /// after exhausting retries, or PaciolanEvenueNotAnEventException immediately (no retry) if the
    /// page is not a real event page (wrong itemCd - retrying with a new IP would not fix that) -
    /// matches python/paciolanevenue/client.py's own NotAnEventPage vs PerimeterXBlocked
    /// distinction exactly (see PaciolanEvenueNotAnEventException's own doc comment for why this
    /// used to be a plain InvalidOperationException, and the bug that caused).</summary>
    public async Task<EventPageData> GetEventAsync(string host, string seasonCd, string itemCd, CancellationToken ct = default)
    {
        var url = $"https://{host}/event/{seasonCd}/{itemCd}";
        Exception? lastErr = null;

        for (var attempt = 1; attempt <= _opt.Retries; attempt++)
        {
            try
            {
                await _browser.OpenFreshAsync(url);
                var html = await _browser.ReadOuterHtmlAsync();
                var ev = EventPageParser.Parse(html, host, seasonCd, itemCd);
                if (!ev.IsEventPage)
                    throw new PaciolanEvenueNotAnEventException(
                        $"paciolanevenue: {url} did not render a single event (context != 'eventdetailpage') - not a PerimeterX block, not retrying.");

                _currentKey = (host, seasonCd, itemCd);
                _currentEventUrl = url;
                return ev;
            }
            catch (PaciolanEvenueBlockedException e)
            {
                lastErr = e;
                Console.Error.WriteLine($"  !! attempt {attempt}/{_opt.Retries} blocked: {e.Message}");
                if (attempt < _opt.Retries)
                    await Task.Delay(TimeSpan.FromSeconds(_opt.SleepSeconds * attempt * 2), ct);
            }
        }

        throw new PaciolanEvenueBlockedException($"gave up after {_opt.Retries} proxy sessions: {lastErr?.Message}");
    }

    /// <summary>Must be called after GetEventAsync for the SAME event (reuses the open
    /// session/page). Returns (rows, coverageNote). If the API errors or returns a non-JSON body
    /// (still a block, served with HTTP 200), this retries the WHOLE session (fresh proxy id +
    /// re-navigate), same as GetEventAsync's retry.</summary>
    public async Task<SeatCrawlResult> GetSeatAvailabilityAsync(EventPageData ev, CancellationToken ct = default)
    {
        if (_currentKey is not { } key || key.Host != ev.Host || key.SeasonCd != ev.SeasonCd || key.ItemCd != ev.ItemCd)
            throw new InvalidOperationException("paciolanevenue: GetSeatAvailabilityAsync called for a different event than the open session - call GetEventAsync first");

        var path = SeatAvailabilityParser.BuildPath(ev);
        string? lastErr = null;

        for (var attempt = 1; attempt <= _opt.Retries; attempt++)
        {
            int status;
            string body;
            try
            {
                (status, body) = await _browser.FetchInPageAsync(path);
            }
            catch (Exception e)
            {
                status = 0;
                body = e.Message;
            }

            if (status == 200)
            {
                try
                {
                    return SeatAvailabilityParser.Parse(body, ev, msg => Console.Error.WriteLine(msg));
                }
                catch (InvalidOperationException e)
                {
                    // A 200 with an HTML block/challenge page instead of JSON is still a block -
                    // retry with a fresh session instead of crashing on parse, matching the Python
                    // client's own handling.
                    lastErr = $"HTTP 200 but body was not valid JSON: {e.Message}";
                }
            }
            else
            {
                lastErr = $"HTTP {status}: {(body.Length > 200 ? body[..200] : body)}";
            }

            Console.Error.WriteLine($"  !! seat-availability attempt {attempt}/{_opt.Retries} failed: {lastErr}");
            if (attempt < _opt.Retries)
            {
                await Task.Delay(TimeSpan.FromSeconds(_opt.SleepSeconds * attempt * 2), ct);
                await _browser.OpenFreshAsync(_currentEventUrl); // fresh proxy session
            }
        }

        throw new PaciolanEvenueBlockedException($"seat-availability gave up after {_opt.Retries} attempts: {lastErr}");
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _host.Dispose();
    }
}
