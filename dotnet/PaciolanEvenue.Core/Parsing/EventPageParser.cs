using System.Text.Json;
using System.Text.RegularExpressions;
using PaciolanEvenue.Core.Models;

namespace PaciolanEvenue.Core.Parsing;

/// <summary>
/// Reads everything the seat-availability API needs (plus the event's own SSR-rendered details
/// and price table) out of one event page's __NEXT_DATA__ script tag. Ported from
/// ~/Desktop/crawler-playbook/PaciolanEvenue/PaciolanEvenueCrawler/EventPageParser.cs (real-tested
/// against Purdue/Oklahoma pages) - System.Text.Json instead of Newtonsoft.Json.Linq, to match this
/// repo's dotnet/ convention (see BroadwayDirectFetchClient.cs). Logic/field extraction unchanged.
/// Nothing here is hard-coded per school; every id comes from the page.
/// </summary>
public static partial class EventPageParser
{
    [GeneratedRegex("<script id=\"__NEXT_DATA__\"[^>]*>(?<json>[\\s\\S]*?)</script>")]
    private static partial Regex NextDataRegex();

    public static EventPageData Parse(string html, string host, string seasonCd, string itemCd)
    {
        var result = new EventPageData { Host = host, SeasonCd = seasonCd, ItemCd = itemCd };

        var match = NextDataRegex().Match(html ?? "");
        if (!match.Success)
            return result; // IsEventPage stays false - caller treats this itemCd as not-found

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(match.Groups["json"].Value);
        }
        catch (JsonException)
        {
            return result;
        }
        using var _ = doc;

        var root = doc.RootElement;
        if (!TryGet(root, "props", out var props1) || !TryGet(props1, "pageProps", out var pageProps))
            return result;
        var context = TryGet(pageProps, "context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.String
            ? ctxEl.GetString()
            : null;
        JsonElement props = default;
        var hasProps = TryGet(pageProps, "component", out var component) &&
                       TryGet(component, "props", out props) && props.ValueKind == JsonValueKind.Object;

        // "eventdetailpage" is the Next.js route context confirmed on both Purdue and Oklahoma
        // event pages during recon; anything else (e.g. "grouplist") means this itemCd did not
        // resolve to a real single event, even though the HTTP status was 200.
        if (context != "eventdetailpage" || !hasProps)
            return result;

        result.IsEventPage = true;
        result.DataAccountId = GetStr(props, "dataAccountId");
        result.DistributorId = GetStr(props, "distributorId");
        result.SiteId = GetStr(props, "siteId");
        result.LinkId = GetStr(props, "linkId");

        if (!TryGet(props, "ssrData", out var ssrData) ||
            !TryGet(ssrData, "discovery_eventDetailMPT", out var discoveryArr) ||
            discoveryArr.ValueKind != JsonValueKind.Array ||
            discoveryArr.GetArrayLength() == 0)
            return result; // has a __NEXT_DATA__/props shape but no event row - be conservative

        var ev = discoveryArr[0];

        // fallback per RECON.md 3.1 - unconfirmed whether POLICYCD is ever actually absent.
        var policyCd = GetStr(ev, "POLICYCD");
        result.PolicyCd = policyCd.Length > 0 ? policyCd : $"{result.DistributorId}:DEFAULT:I";
        var policyType = GetStr(ev, "POLICYTYPE");
        result.PolicyType = policyType.Length > 0 ? policyType : "I";

        var eventName = GetStr(ev, "EVENTNAME");
        result.EventName = eventName.Length > 0 ? eventName : GetStr(ev, "ITEMNAME");
        result.FacilityTitle = GetStr(ev, "FAC_TITLE");
        result.EventDtUtc = ParseUtc(GetStr(ev, "EVENTDT"));
        var eventDtFac = GetStr(ev, "EVENTDTFAC");
        result.EventDtFacRaw = eventDtFac.Length > 0 ? eventDtFac : null;
        result.HideTime = GetBool(ev, "HIDE_TIME");
        result.HideDateTime = GetBool(ev, "HIDE_DATE_TIME");
        result.SoldOut = GetBool(ev, "SOLD_OUT");
        result.TotalCapacityFromSsr = GetInt(ev, "TOTALCAPACITY");
        result.AvailableFromSsr = GetInt(ev, "AVAILABLE");
        result.MinQty = GetInt(ev, "MINQTY");
        result.MaxQty = GetInt(ev, "MAXQTY");
        result.MultipleQty = GetInt(ev, "MULTIPLEQTY");
        result.FacCd = GetStr(ev, "FAC_CD");
        result.ConfigurationCd = GetStr(ev, "CONFIGURATIONCD");
        result.BaseMapId = GetStr(props, "baseMapId");
        result.AllowSeatMap = TryGet(ev, "ALLOWSEATMAP", out var asm) && asm.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? asm.ValueKind == JsonValueKind.True : null;

        if (TryGet(ev, "PL_PT_PRICES", out var plArr) && plArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var pl in plArr.EnumerateArray())
            {
                if (pl.ValueKind != JsonValueKind.Object) continue;
                result.PriceLevels.Add(new PriceLevel
                {
                    Pl = GetStr(pl, "PL"),
                    PlDesc = GetStr(pl, "PL_DESC"),
                    Pt = GetStr(pl, "PT"),
                    PtDesc = GetStr(pl, "PT_DESC"),
                    Price = GetLong(pl, "PRICE") ?? 0,
                    PerTicketFee = GetLong(pl, "PER_TICKET_FEE") ?? 0,
                    FacilityFee = GetLong(pl, "FACILITY_FEE") ?? 0,
                    PlptMinQty = GetInt(pl, "PLPT_MINQTY"),
                    PlptMaxQty = GetInt(pl, "PLPT_MAXQTY"),
                    PlptMultiple = GetInt(pl, "PLPT_MULTIPLE"),
                });
            }
        }

        return result;
    }

    /// <summary>Parses a value already confirmed to be a true UTC instant (EVENTDT - has a
    /// trailing "Z" AND was cross-checked against analytics beacons during recon). Do NOT use this
    /// for EVENTDTFAC - see EventPageData.EventDtFacRaw.</summary>
    private static DateTimeOffset? ParseUtc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt) ? dt : null;
    }

    private static bool TryGet(JsonElement obj, string prop, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out value);
    }

    private static string GetStr(JsonElement obj, string prop, string fallback = "")
    {
        if (!TryGet(obj, prop, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString();
    }

    private static bool GetBool(JsonElement obj, string prop) =>
        TryGet(obj, prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement obj, string prop) =>
        TryGet(obj, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long? GetLong(JsonElement obj, string prop) =>
        TryGet(obj, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
