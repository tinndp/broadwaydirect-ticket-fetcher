using System.Text.Json;
using System.Text.Json.Nodes;
using AXS.Core.Models;
using BroadwayDirect.Core.Proxy;

namespace AXS.Fetch.Playwright;

/// <summary>File output + proxy selection shared by AXS.Cli and AXS.Api - port of
/// python/axs/output.py and cli.env_proxy(). result.json uses snake_case keys, so it is
/// directly comparable with the Python side's result.json.</summary>
public static class AxsOutput
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>OnRaw hook: {outDir}/{eventId}/raw_{name}.json. "event" is always the first raw of
    /// a fetch, so it clears that event's previous raw_*.json (no stale files from older runs).</summary>
    public static Action<long, string, string> RawWriter(string outDir) => (eventId, name, json) =>
    {
        var dir = Path.Combine(outDir, eventId.ToString());
        Directory.CreateDirectory(dir);
        if (name == "event")
            foreach (var f in Directory.GetFiles(dir, "raw_*.json")) File.Delete(f);
        File.WriteAllText(Path.Combine(dir, $"raw_{name}.json"), json);
    };

    public static JsonObject ToJson(AxsResult r) => new()
    {
        ["event"] = JsonSerializer.SerializeToNode(r.Event, Json),
        ["coverage"] = r.Coverage,
        ["notes"] = JsonSerializer.SerializeToNode(r.Notes, Json),
        ["price_levels"] = JsonSerializer.SerializeToNode(r.PriceLevels, Json),
        ["listings"] = JsonSerializer.SerializeToNode(r.Listings, Json),
    };

    public static string WriteResult(string outDir, AxsResult r)
    {
        var dir = Path.Combine(outDir, r.Event.EventId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "result.json");
        File.WriteAllText(path, ToJson(r).ToJsonString(Json));
        return path;
    }

    /// <summary>AXS_PROXY_HOST/PORT/USER/PASS (USER may contain {SESSIONID}) - python cli.env_proxy().</summary>
    public static string? EnvProxy()
    {
        var host = Environment.GetEnvironmentVariable("AXS_PROXY_HOST");
        var port = Environment.GetEnvironmentVariable("AXS_PROXY_PORT");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port)) return null;
        var user = Environment.GetEnvironmentVariable("AXS_PROXY_USER") ?? "";
        var pass = Environment.GetEnvironmentVariable("AXS_PROXY_PASS") ?? "";
        return user != "" ? $"http://{user}:{pass}@{host}:{port}" : $"http://{host}:{port}";
    }

    /// <summary>explicit value (URI or host:port:user:pass) > env > none.</summary>
    public static string PickProxy(string? requested) =>
        !string.IsNullOrWhiteSpace(requested) ? ProxyUri.Normalize(requested) : (EnvProxy() ?? "");
}
