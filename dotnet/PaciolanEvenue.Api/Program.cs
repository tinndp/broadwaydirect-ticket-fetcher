using System.Collections.Concurrent;
using BroadwayDirect.Core.Proxy;
using PaciolanEvenue.Core.Grouping;
using PaciolanEvenue.Core.Models;
using PaciolanEvenue.Core.Parsing;
using PaciolanEvenue.Core.Storage;
using PaciolanEvenue.Fetch;

var builder = WebApplication.CreateBuilder(args);

var fetchOptions = builder.Configuration.GetSection("Fetch").Get<PaciolanEvenueFetchOptions>() ?? new PaciolanEvenueFetchOptions();
builder.Services.AddSingleton(fetchOptions);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// One PaciolanEvenueClient per proxy TEMPLATE (each holds its own WebView2Host; the actual
// {SESSIONID} is substituted fresh on every OpenFreshAsync inside PaciolanEvenueBrowser - see that
// class's own doc comment for why, unlike BroadwayDirect's session reuse). Mirrors StubHub.Api's
// _get_client-equivalent cache.
var clients = new ConcurrentDictionary<string, PaciolanEvenueClient>();
PaciolanEvenueClient GetClient(string proxyTemplate) =>
    clients.GetOrAdd(proxyTemplate, p => new PaciolanEvenueClient(p, fetchOptions));

// Ported from python/paciolanevenue/api.py's _pick_proxy/_env_proxy_template (added 2026-09-22 -
// this fallback chain was missing entirely before: proxy was ONLY ever taken from the request
// body). Priority: request body > PACIOLAN_PROXY_* env vars > no proxy. Does NOT port the Python
// side's further fallback to a gitignored _local_proxy.py module or a PACIOLAN_PROXY_LIST_PATH
// round-robin pool - neither has an obvious .NET equivalent file/convention yet; add one if this
// needs a rotating pool later.
string PickProxy(string? requested)
{
    if (!string.IsNullOrWhiteSpace(requested))
        return ProxyUri.Normalize(requested);

    var host = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_HOST");
    var port = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_PORT");
    var user = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_USER") ?? "";
    var pass = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_PASS") ?? "";
    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port))
        return $"http://{user}:{pass}@{host}:{port}";

    return "";
}

// Lazily connects to MongoDB on first use. This is the ONLY place listing data lands (see
// PaciolanEvenueInventoryStore's own doc comment for the collection-shape decision), so a null
// store or a failed write is FATAL to the request - unlike BroadwayDirect.Api's best-effort mirror.
// Connection comes from MONGO_URI/MONGO_DB env vars only - never hard-code real credentials here
// (see this repo's StubHub.Api/Program.cs for what NOT to do: a real Mongo password is committed
// there in plain text - rotate it and remove the hardcoded line rather than copying the pattern).
PaciolanEvenueInventoryStore? store = null;
var storeInitFailed = false;
var storeLock = new object();

PaciolanEvenueInventoryStore? GetStore()
{
    lock (storeLock)
    {
        if (store != null || storeInitFailed) return store;
        try
        {
            var uri = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
            var dbName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect";
            store = new PaciolanEvenueInventoryStore(uri, dbName);
        }
        catch (Exception e)
        {
            storeInitFailed = true;
            Console.Error.WriteLine($"  !! MongoDB unreachable: {e.Message}");
        }
        return store;
    }
}

// Picks the Public ("P") price for this listing's price level, else the first available price
// type for that level, else 0. Divides by 100 (units ~confirmed cents, not 100% - see README.md
// "Open questions"). Matches the Python port's _listing_price() and the .NET Rowing bot's
// PaciolanEvenueSessionForm.
decimal ListingPrice(ListingGroup listing, List<PriceLevel> priceLevels)
{
    var candidates = priceLevels.Where(p => p.Pl == listing.PriceLevelCd).ToList();
    if (candidates.Count == 0) return 0m;
    var chosen = candidates.FirstOrDefault(p => p.Pt == "P") ?? candidates[0];
    return Math.Round(chosen.Price / 100m, 2);
}

PaciolanEvenueListing[] BuildDocs(string sourceEventId, List<ListingGroup> listings, EventPageData ev)
{
    var priceLevels = ev.PriceLevels;
    return listings.Select(listing =>
    {
        var candidates = priceLevels.Where(p => p.Pl == listing.PriceLevelCd).ToList();
        var chosen = candidates.FirstOrDefault(p => p.Pt == "P") ?? candidates.FirstOrDefault();
        var price = ListingPrice(listing, priceLevels);
        var lowSeat = listing.SeatNums.Count > 0 ? listing.SeatNums[0] : (int?)null;
        var highSeat = listing.SeatNums.Count > 0 ? listing.SeatNums[^1] : (int?)null;
        var seating = listing.SeatingType; // already POS vocabulary (SeatGrouper)
        long? priceLevelId = long.TryParse(listing.PriceLevelCd, out var pid) ? pid : null;
        static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;
        static int? Qty(int? v) => v is null or 0 ? null : v; // eVenue 0 = "not set" -> null

        var doc = new PaciolanEvenueListing
        {
            SourceEventId = sourceEventId,
            Level = listing.Level,
            Section = listing.Section,
            Row = listing.Row,
            LowSeat = lowSeat,
            HighSeat = highSeat,
            Quantity = listing.Quantity,
            Seating = seating,
            Price = price,
            PriceLevelId = priceLevelId,
            Zone = Nz(chosen?.PlDesc),
            DisplayPrice = price,
            PriceClass = Nz(chosen?.Pt),
            SeatKeys = Nz(string.Join(",", listing.SeatKeys)), // GA quantity listing: no seat numbers
            LastApiSyncedDateTimeUtc = DateTime.UtcNow,
            MinQuantity = Qty(ev.MinQty),
            MaxQuantity = Qty(ev.MaxQty),
            QuantityIncrement = Qty(ev.MultipleQty),
            PriceLevelMinQuantity = Qty(chosen?.PlptMinQty),
            PriceLevelMaxQuantity = Qty(chosen?.PlptMaxQty),
            PriceLevelQuantityIncrement = Qty(chosen?.PlptMultiple),
            SeatStatusType = SeatGrouper.SeatStatusType(listing, ev),
            SeatTag = string.IsNullOrEmpty(listing.SeatTag) ? null : listing.SeatTag,
        };
        doc.Id = ListingIdentity.BuildPaciolanEvenue(sourceEventId, listing.Level, listing.Section, listing.Row, lowSeat, highSeat, listing.SeatTag);
        return doc;
    }).ToArray();
}

// POST /api/seatavailability { eventId, url, proxy? } -> { eventId, event, priceLevels, listings,
// coverage }. eventId must equal the itemCd parsed from url. url = the event page
// (https://{host}/event/{seasonCd}/{itemCd}).
//
// REQUEST shape matches python/paciolanevenue/api.py's SeatAvailabilityRequest exactly
// ({eventId, url, proxy?}). The RESPONSE shape does NOT match byte-for-byte: this uses
// camelCase/ASP.NET Core's default JSON naming (eventName, priceLevelCd, seatKeys...) and adds
// sourceEventId; the Python side returns snake_case (event_name, price_level_cd, seat_keys...)
// and has no sourceEventId field. Not treated as a bug to fix without a reason to - camelCase
// matches this repo's C# conventions (BroadwayDirect.Api/StubHub.Api are camelCase too), and
// nothing currently consumes both APIs and expects one shape.
app.MapPost("/api/seatavailability", async (SeatAvailabilityRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "eventId and url are required" });

    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var parsedUrl))
        return Results.BadRequest(new { error = "invalid url" });

    var pathParts = parsedUrl.AbsolutePath.Trim('/').Split('/');
    if (pathParts.Length < 3 || pathParts[0] != "event")
        return Results.BadRequest(new { error = "url must look like https://{host}/event/{seasonCd}/{itemCd}" });
    var (host, seasonCd, itemCd) = (parsedUrl.Host, pathParts[1], pathParts[2]);

    if (!string.Equals(itemCd, req.EventId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = $"eventId mismatch: url has itemCd={itemCd}, body has eventId={req.EventId}" });

    string proxy;
    try
    {
        proxy = PickProxy(req.Proxy);
    }
    catch (FormatException ex)
    {
        return Results.BadRequest(new { error = $"invalid proxy: {ex.Message}" });
    }

    EventPageData ev;
    SeatCrawlResult seatResult;
    string mapNote;
    var client = GetClient(proxy);
    try
    {
        ev = await client.GetEventAsync(host, seasonCd, itemCd);
        seatResult = await client.GetSeatAvailabilityAsync(ev);
        mapNote = await client.GetEventMapAsync(ev);
    }
    // NotAnEventPage (wrong itemCd) is not a block - matches python/paciolanevenue/api.py's own
    // 404-vs-502 split exactly. Getting this wrong (both as 502) is a real bug that was found and
    // fixed 2026-09-22: it made a genuine PerimeterX block indistinguishable from "this itemCd
    // doesn't exist" purely from the status code, which is what made a REAL, previously-confirmed
    // event (F26/F03) look like it might be a code bug during debugging.
    catch (PaciolanEvenueNotAnEventException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
    catch (PaciolanEvenueBlockedException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var listings = SeatGrouper.BuildListings(seatResult.Rows, ev);
    var sourceEventId = $"{host}:{seasonCd}:{itemCd}";

    try
    {
        var s = GetStore() ?? throw new InvalidOperationException("MongoDB is unreachable - listing data cannot be persisted");
        s.SaveEventInventory(sourceEventId, BuildDocs(sourceEventId, listings, ev));
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: $"fetch succeeded but persistence failed: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Json(new
    {
        eventId = req.EventId,
        sourceEventId,
        @event = new
        {
            ev.EventName,
            ev.FacilityTitle,
            eventDtUtc = ev.EventDtUtc,
            ev.HideTime,
            ev.SoldOut,
            totalCapacitySsr = ev.TotalCapacityFromSsr,
            availableSsr = ev.AvailableFromSsr,
        },
        priceLevels = ev.PriceLevels.Select(pl => new { pl.Pl, pl.PlDesc, pl.Pt, pl.PtDesc, pl.Price }),
        listings = listings.Select(l => new
        {
            l.Level,
            l.Section,
            l.Row,
            l.PriceLevelCd,
            l.SeatingType,
            l.Quantity,
            seatKeys = l.SeatKeys,
            l.SeatingTypeCd,
            l.SeatStatuses,
        }),
        coverage = $"{seatResult.CoverageNote} {mapNote} listings={listings.Count} tickets={listings.Sum(l => l.Quantity)} " +
                   $"ga_tickets={listings.Where(l => l.SeatingTypeCd == "G").Sum(l => l.Quantity)}",
    });
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var c in clients.Values)
        c.DisposeAsync().AsTask().GetAwaiter().GetResult();
    store?.Close();
});

app.Run();

/// <summary>proxy: optional, "scheme://[user:pass@]host:port" (may contain the literal
/// "{SESSIONID}" placeholder in the username - a fresh id is substituted per browser open, see
/// PaciolanEvenueBrowser).</summary>
internal record SeatAvailabilityRequest(string EventId, string Url, string? Proxy);
