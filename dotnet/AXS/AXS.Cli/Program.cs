using System.Diagnostics;
using AXS.Core.Grouping;
using AXS.Core.Parsing;
using AXS.Fetch.Playwright;

// Manual-test CLI - mirrors `python3 -m axs.cli event` (args, output files, exit codes):
//   dotnet run --project AXS.Cli -- event --url https://www.axs.com/events/1457730/tom-jones-21-event-tickets \
//       [--url ...] [--proxy "http://user-{SESSIONID}:pass@host:port"] [--retries 4] [--out output/axs] [--headless] [--bundled-chromium]
// Proxy is optional: --proxy, or AXS_PROXY_HOST/PORT/USER/PASS; without either the machine's IP is used.
// Exit codes: 0 all OK, 1 usage, 2 at least one event failed.

if (args.Length == 0 || args[0] != "event")
{
    Console.Error.WriteLine("usage: event --url <eventUrl> [--url ...] [--proxy P] [--retries N] [--out DIR] [--headless] [--bundled-chromium]");
    return 1;
}

var urls = new List<string>();
string? proxyArg = null;
var outDir = "output/axs";
var options = new AxsFetchOptions();
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--url": urls.Add(args[++i]); break;
        case "--proxy": proxyArg = args[++i]; break;
        case "--retries": options.Retries = int.Parse(args[++i]); break;
        case "--out": outDir = args[++i]; break;
        case "--headless": options.Headless = true; break;
        case "--bundled-chromium": options.Channel = null; break;
        default: Console.Error.WriteLine($"unknown arg: {args[i]}"); return 1;
    }
}
if (urls.Count == 0) { Console.Error.WriteLine("at least one --url is required"); return 1; }

options.ProxyTemplate = AxsOutput.PickProxy(proxyArg);
options.OnRaw = AxsOutput.RawWriter(outDir);

var failed = new List<string>();
await using (var client = new AxsPlaywrightClient(options))
{
    foreach (var url in urls)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ev = await client.GetEventAsync(url);
            Console.Error.WriteLine($"event {ev.EventId}: {ev.Name} @ {ev.VenueName} {ev.EventDtLocal} ({ev.EventDtUtc}) status='{ev.Status}'");
            AxsInventory inv;
            try { inv = await client.GetInventoryAsync(ev); }
            catch (AxsNotOnSaleException e) { inv = new AxsInventory(); inv.Notes.Add("not on sale: " + e.Message); }
            var res = AxsGrouper.BuildResult(ev, inv.Captured, inv.Notes);
            res.Coverage += $" elapsed={sw.Elapsed.TotalSeconds:0}s";
            var path = AxsOutput.WriteResult(outDir, res);
            Console.WriteLine($"coverage: {res.Coverage} notes=[{string.Join(", ", res.Notes)}]");
            Console.WriteLine($"wrote {path}");
        }
        catch (NotAnEventPageException e) { Console.Error.WriteLine($"NOT AN EVENT {url}: {e.Message}"); failed.Add(url); }
        catch (AxsBlockedException e) { Console.Error.WriteLine($"BLOCKED {url}: {e.Message}"); failed.Add(url); }
    }
}
if (failed.Count > 0)
{
    Console.WriteLine($"{failed.Count}/{urls.Count} event(s) failed: [{string.Join(", ", failed)}]");
    return 2;
}
return 0;
