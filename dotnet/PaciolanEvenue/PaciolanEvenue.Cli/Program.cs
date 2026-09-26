using System.Text.Json;
using PaciolanEvenue.Core.Grouping;
using PaciolanEvenue.Fetch.Playwright;

// Minimal CLI for manual testing - not the HTTP API (see PaciolanEvenue.Api for that). Mirrors
// python/paciolanevenue/cli.py's args, output shape and exit codes exactly:
//   dotnet run --project PaciolanEvenue.Cli -- --host purduesports.evenue.net --season F26 --item F06

string? host = null, season = null, item = null, outDir = "output";
var retries = 4;
var headless = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--season": season = args[++i]; break;
        case "--item": item = args[++i]; break;
        case "--retries": retries = int.Parse(args[++i]); break;
        case "--headless": headless = true; break;
        case "--out": outDir = args[++i]; break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 1;
    }
}

if (host == null || season == null || item == null)
{
    Console.Error.WriteLine("usage: --host <host> --season <seasonCd> --item <itemCd> [--retries N] [--headless] [--out dir]");
    return 1;
}

// Same PACIOLAN_PROXY_* env var convention as PaciolanEvenue.Api's PickProxy() and the Python
// side's _env_proxy(). No _local_proxy.py-equivalent fallback here (see PaciolanEvenue.Api's own
// note on that gap).
string? EnvProxy()
{
    var envHost = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_HOST");
    var envPort = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_PORT");
    var envUser = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_USER") ?? "";
    var envPass = Environment.GetEnvironmentVariable("PACIOLAN_PROXY_PASS") ?? "";
    return !string.IsNullOrEmpty(envHost) && !string.IsNullOrEmpty(envPort)
        ? $"http://{envUser}:{envPass}@{envHost}:{envPort}"
        : null;
}

var client = new PaciolanEvenuePlaywrightClient(EnvProxy() ?? "", headless, retries);
try
{
    PaciolanEvenue.Core.Models.EventPageData ev;
    try
    {
        ev = await client.GetEventAsync(host, season, item);
    }
    catch (PaciolanEvenuePlaywrightNotAnEventException e)
    {
        Console.Error.WriteLine($"NOT AN EVENT: {e.Message}");
        return 1;
    }
    catch (PaciolanEvenuePlaywrightBlockedException e)
    {
        Console.Error.WriteLine($"BLOCKED: {e.Message}");
        return 2;
    }

    Console.WriteLine($"event: {ev.EventName} @ {ev.FacilityTitle} (hideTime={ev.HideTime})");

    PaciolanEvenue.Core.Parsing.SeatCrawlResult seatResult;
    try
    {
        seatResult = await client.GetSeatAvailabilityAsync(ev);
    }
    catch (PaciolanEvenuePlaywrightBlockedException e)
    {
        Console.Error.WriteLine($"BLOCKED on seat-availability: {e.Message}");
        return 2;
    }

    var mapNote = await client.GetEventMapAsync(ev);
    Console.WriteLine($"coverage: {seatResult.CoverageNote} {mapNote}");
    var listings = SeatGrouper.BuildListings(seatResult.Rows, ev);
    var ga = listings.Where(l => l.SeatingTypeCd == "G").Sum(l => l.Quantity);
    Console.WriteLine($"{listings.Count} listings, {listings.Sum(l => l.Quantity)} tickets (GA quantity {ga})");

    var dir = Path.Combine(outDir, host, season, item);
    Directory.CreateDirectory(dir);
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    await File.WriteAllTextAsync(Path.Combine(dir, "event.json"),
        JsonSerializer.Serialize(new { @event = ev, priceLevels = ev.PriceLevels }, jsonOptions));
    await File.WriteAllTextAsync(Path.Combine(dir, "listings.json"),
        JsonSerializer.Serialize(listings, jsonOptions));
    Console.WriteLine($"wrote {dir}/event.json and listings.json");
    return 0;
}
finally
{
    await client.DisposeAsync();
}
