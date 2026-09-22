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

PaciolanEvenueListing[] BuildDocs(string sourceEventId, List<ListingGroup> listings, List<PriceLevel> priceLevels)
{
    return listings.Select(listing =>
    {
        var candidates = priceLevels.Where(p => p.Pl == listing.PriceLevelCd).ToList();
        var chosen = candidates.FirstOrDefault(p => p.Pt == "P") ?? candidates.FirstOrDefault();
        var price = ListingPrice(listing, priceLevels);
        var lowSeat = listing.SeatNums.Count > 0 ? listing.SeatNums[0] : (int?)null;
        var highSeat = listing.SeatNums.Count > 0 ? listing.SeatNums[^1] : (int?)null;
        var seating = listing.SeatingType == "OddEven" ? "Odd/Even" : listing.SeatingType;
        var priceLevelId = long.TryParse(listing.PriceLevelCd, out var pid) ? pid : 0;

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
            PriceLevelCd = listing.PriceLevelCd,
            Zone = chosen?.PlDesc ?? "",
            DisplayPrice = price,
            PriceClass = chosen?.Pt ?? "",
            SeatKeys = string.Join(",", listing.SeatKeys),
            LastApiSyncedDateTimeUtc = DateTime.UtcNow,
        };
        doc.Id = ListingIdentity.BuildPaciolanEvenue(sourceEventId, listing.Level, listing.Section, listing.Row, lowSeat, highSeat);
        return doc;
    }).ToArray();
}

// POST /api/seatavailability { eventId, url, proxy? } -> { eventId, event, priceLevels, listings,
// coverage }. Same request contract as the Python paciolanevenue package's api.py (eventId must
// equal the itemCd parsed from url). url = the event page
// (https://{host}/event/{seasonCd}/{itemCd}).
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

    var proxy = "";
    if (!string.IsNullOrWhiteSpace(req.Proxy))
    {
        try { proxy = ProxyUri.Normalize(req.Proxy); }
        catch (FormatException ex) { return Results.BadRequest(new { error = $"invalid proxy: {ex.Message}" }); }
    }

    EventPageData ev;
    SeatCrawlResult seatResult;
    var client = GetClient(proxy);
    try
    {
        ev = await client.GetEventAsync(host, seasonCd, itemCd);
        seatResult = await client.GetSeatAvailabilityAsync(ev);
    }
    catch (PaciolanEvenueBlockedException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    var available = seatResult.Rows.Where(r => r.Available).ToList();
    var listings = SeatGrouper.GroupIntoListings(available);
    var sourceEventId = $"{host}:{seasonCd}:{itemCd}";

    try
    {
        var s = GetStore() ?? throw new InvalidOperationException("MongoDB is unreachable - listing data cannot be persisted");
        s.SaveEventInventory(sourceEventId, BuildDocs(sourceEventId, listings, ev.PriceLevels));
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
        }),
        coverage = seatResult.CoverageNote,
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
