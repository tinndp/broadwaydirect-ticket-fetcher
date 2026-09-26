using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AXS.Core.Grouping;
using AXS.Core.Parsing;
using AXS.Core.Storage;
using AXS.Fetch.Playwright;

// POST /api/eventinventory {eventId, url, proxy?} - same contract as python/axs/api.py:
//   {eventId, event, raw, price_levels, listings, coverage, notes}
// Env: MONGO_URI (default mongodb://localhost:27017), MONGO_DB (default broadwaydirect),
//      AXS_PROXY_HOST/PORT/USER/PASS, AXS_RETRIES (default 4), AXS_OUTPUT_DIR (default output/axs).
// Mongo mirror is best-effort, and happens whenever inventory was actually fetched - even with 0
// listings (sold out clears stale docs); nothing fetched (blocked / not on sale) -> Mongo untouched.
// One warmed browser per proxy, each behind its own lock (AXS rate-limits hard per IP).

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var outputDir = Environment.GetEnvironmentVariable("AXS_OUTPUT_DIR") ?? "output/axs";
var retries = int.TryParse(Environment.GetEnvironmentVariable("AXS_RETRIES"), out var r) ? r : 4;
var clients = new ConcurrentDictionary<string, (AxsPlaywrightClient Client, SemaphoreSlim Lock)>();

(AxsPlaywrightClient Client, SemaphoreSlim Lock) ClientFor(string proxy) =>
    clients.GetOrAdd(proxy, p => (new AxsPlaywrightClient(new AxsFetchOptions
    {
        ProxyTemplate = p,
        Retries = retries,
        OnRaw = AxsOutput.RawWriter(outputDir),
    }), new SemaphoreSlim(1, 1)));

static bool InventoryFetched(AxsInventory inv) =>
    inv.Captured.ContainsKey("mp_offers") || inv.Captured.ContainsKey("offer_search") || inv.Captured.ContainsKey("price");

void MirrorToMongo(AXS.Core.Models.Event ev, List<AXS.Core.Models.Listing> listings)
{
    try
    {
        var store = new AxsInventoryStore(
            Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017",
            Environment.GetEnvironmentVariable("MONGO_DB") ?? "broadwaydirect");
        var n = store.SaveEventInventory(ev, listings);
        Console.Error.WriteLine($"  wrote {n} listing(s) to {AxsDocuments.CollectionName}");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"  !! failed to mirror event {ev.EventId} to MongoDB: {e.Message}");
    }
}

app.MapPost("/api/eventinventory", async (EventInventoryRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId) || string.IsNullOrWhiteSpace(req.Url))
        return Results.Json(new { error = "eventId and url are required" }, statusCode: 400);
    var parsed = AxsParser.ParseEventUrl(req.Url);
    if (parsed == null)
        return Results.Json(new { error = "url must look like https://www.axs.com/events/{eventId}/{slug}" }, statusCode: 400);
    if (parsed.Item1.ToString() != req.EventId.Trim())
        return Results.Json(new { error = $"eventId mismatch: url has {parsed.Item1}, body has '{req.EventId}'" }, statusCode: 400);
    string proxy;
    try { proxy = AxsOutput.PickProxy(req.Proxy); }
    catch (FormatException e) { return Results.Json(new { error = $"invalid proxy: {e.Message}" }, statusCode: 400); }

    var (client, gate) = ClientFor(proxy);
    var sw = Stopwatch.StartNew();
    AXS.Core.Models.Event ev;
    AxsInventory inv;
    await gate.WaitAsync();
    try
    {
        try { ev = await client.GetEventAsync(req.Url); }
        catch (NotAnEventPageException e) { return Results.Json(new { error = $"{req.EventId} is not an event page: {e.Message}" }, statusCode: 404); }
        catch (AxsBlockedException e) { return Results.Json(new { error = $"blocked: {e.Message}" }, statusCode: 502); }
        try { inv = await client.GetInventoryAsync(ev); }
        catch (AxsNotOnSaleException e) { inv = new AxsInventory(); inv.Notes.Add("not on sale: " + e.Message); }
        catch (AxsBlockedException e) { return Results.Json(new { error = $"blocked on the ticket page: {e.Message}" }, statusCode: 502); }
    }
    finally { gate.Release(); }

    var res = AxsGrouper.BuildResult(ev, inv.Captured, inv.Notes);
    res.Coverage += $" elapsed={sw.Elapsed.TotalSeconds:0}s";
    AxsOutput.WriteResult(outputDir, res);
    if (InventoryFetched(inv))
        MirrorToMongo(ev, res.Listings); // 0 listings -> clears this event's stale docs

    var body = AxsOutput.ToJson(res);
    var raw = new JsonObject();
    foreach (var (name, json) in inv.Captured)
        if (name != "phase") // phase echoes the caller's IP
            raw[name] = JsonNode.Parse(json);
    return Results.Json(new JsonObject
    {
        ["eventId"] = req.EventId,
        ["event"] = body["event"]!.DeepClone(),
        ["raw"] = raw,
        ["price_levels"] = body["price_levels"]!.DeepClone(),
        ["listings"] = body["listings"]!.DeepClone(),
        ["coverage"] = res.Coverage,
        ["notes"] = body["notes"]!.DeepClone(),
    });
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var c in clients.Values) c.Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.Run();

record EventInventoryRequest(string EventId, string Url, string? Proxy);
