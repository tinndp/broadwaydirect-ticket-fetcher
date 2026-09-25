using System.Text.Json;
using PaciolanEvenue.Core.Models;

namespace PaciolanEvenue.Core.Parsing;

/// <summary>GraphQL maps_eventMap {HOLDCODES, SEATING_TYPES} - the same query (same arguments)
/// evenue.net's own map component sends on a seat-map event page (captured 2026-09-25,
/// purduesports F26/F04). Port of python/paciolanevenue/parser.py event_map_query/parse_event_map.</summary>
public static class EventMapQuery
{
    public const string GqlPath = "/pac-api/consumer/gql";

    private static string Q(string? v) => JsonSerializer.Serialize(v ?? "");

    /// <summary>The JSON request body ({"query": ...}).</summary>
    public static string BuildBody(EventPageData ev)
    {
        var args = $"dataAccountId: {Q(ev.DataAccountId)}, seasonCd: {Q(ev.SeasonCd)}, facilityCd: {Q(ev.FacCd)}, " +
                   $"itemCd: {Q(ev.ItemCd)}, configCd: {Q(ev.ConfigurationCd)}, availability: \"A|S\", " +
                   $"policyCd: {Q(ev.PolicyCd)}, policyType: {Q(ev.PolicyType)}, distributorId: {Q(ev.DistributorId)}, " +
                   $"baseMapId: {Q(ev.BaseMapId)}";
        var query = "query { maps_eventMap(" + args + ") { SEATING_TYPES { pl seatingType } " +
                    "HOLDCODES { holdcode title message type } } }";
        return JsonSerializer.Serialize(new { query });
    }

    /// <summary>Fills ev.HoldCodes / ev.SeatingTypes. Throws when the body has no maps_eventMap object.</summary>
    public static void Apply(EventPageData ev, string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("maps_eventMap", out var map) || map.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("no maps_eventMap in response: " +
                                                (responseBody.Length > 200 ? responseBody[..200] : responseBody));
        var hold = new Dictionary<string, string>();
        if (map.TryGetProperty("HOLDCODES", out var hc) && hc.ValueKind == JsonValueKind.Array)
            foreach (var h in hc.EnumerateArray())
                hold[Str(h, "holdcode")] = Str(h, "type");
        var seating = new Dictionary<string, string>();
        if (map.TryGetProperty("SEATING_TYPES", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var x in st.EnumerateArray())
                seating[Str(x, "pl")] = Str(x, "seatingType");
        ev.HoldCodes = hold;
        ev.SeatingTypes = seating;
    }

    private static string Str(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
}
