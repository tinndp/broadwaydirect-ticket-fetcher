using System.Text.Json;
using PaciolanEvenue.Core.Models;

namespace PaciolanEvenue.Core.Parsing;

public sealed class SeatCrawlResult
{
    public List<SeatRow> Rows { get; init; } = new();
    public List<string> ColumnNames { get; init; } = new();

    /// <summary>Cross-check note against the event page's own SSR numbers (RECON.md section 4
    /// "Đủ dữ liệu").</summary>
    public string CoverageNote { get; init; } = "";
}

/// <summary>
/// Parses the response body of GET /pac-api/seat-availability/event-id/{dataAccountId}:
/// {seasonCd}:{itemCd}/seats (fetched by PaciolanEvenue.Fetch - this class does not call the
/// network itself). One response = the ENTIRE seat map for the event, no pagination, but it
/// includes sold seats too - callers must filter on SeatRow.Available.
/// </summary>
public static class SeatAvailabilityParser
{
    /// <summary>Column order as observed on both Purdue and Oklahoma during recon.</summary>
    public static readonly string[] ExpectedColumns =
    {
        "LEVELSECTIONCD", "ROWCD", "SEATCD", "PRICELEVELCD", "SEATSTATUS",
        "MARKER_ID", "SEAT_MARKER_ACTIVE", "SLP_PRICE", "AVAILABLE", "HIDDEN",
    };

    public static string BuildPath(EventPageData ev)
    {
        var eventId = $"{ev.DataAccountId}:{ev.SeasonCd}:{ev.ItemCd}";
        // "A|S" (available + sold) was the value used by evenue.net's own front-end during
        // recon; "A" alone did NOT reduce the row count (RECON.md 3.1) - filtering happens
        // client-side on Available.
        return $"/pac-api/seat-availability/event-id/{Uri.EscapeDataString(eventId)}/seats"
             + $"?distributorId={Uri.EscapeDataString(ev.DistributorId)}"
             + "&availability=A%7CS"
             + $"&policyCd={Uri.EscapeDataString(ev.PolicyCd)}"
             + $"&policyType={Uri.EscapeDataString(ev.PolicyType)}";
    }

    public static SeatCrawlResult Parse(string body, EventPageData ev, Action<string>? log = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"seat-availability for {ev.ItemCd}: response was not the expected [rows, columnNames] JSON array ({e.Message}).");
        }
        using var _ = doc;

        var top = doc.RootElement;
        if (top.ValueKind != JsonValueKind.Array || top.GetArrayLength() < 2)
            throw new InvalidOperationException(
                $"seat-availability for {ev.ItemCd}: unexpected top-level shape (count={(top.ValueKind == JsonValueKind.Array ? top.GetArrayLength() : 0)}).");

        var rowsArr = top[0];
        var colsArr = top[1];
        if (rowsArr.ValueKind != JsonValueKind.Array || colsArr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"seat-availability for {ev.ItemCd}: unexpected top-level shape.");

        var columns = colsArr.EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "")
            .ToList();
        if (!columns.SequenceEqual(ExpectedColumns))
            log?.Invoke($"  !! [{ev.ItemCd}] seat-availability columns differ from the recon baseline: [{string.Join(",", columns)}]. Parsing positionally anyway - verify output.");

        var rows = new List<SeatRow>();
        var nonNumericSeatCount = 0;
        foreach (var r in rowsArr.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() < 10) continue;
            var levelSection = Str(r[0]);
            var colonIdx = levelSection.IndexOf(':');
            var seatCd = Str(r[2]);
            int? seatNum = int.TryParse(seatCd, out var n) ? n : null;
            if (seatNum == null) nonNumericSeatCount++;

            rows.Add(new SeatRow
            {
                LevelSectionCd = levelSection,
                Level = colonIdx >= 0 ? levelSection[..colonIdx] : levelSection,
                Section = colonIdx >= 0 ? levelSection[(colonIdx + 1)..] : levelSection,
                RowCd = Str(r[1]),
                SeatCd = seatCd,
                SeatNum = seatNum,
                PriceLevelCd = Str(r[3]),
                SeatStatus = Str(r[4]),
                MarkerId = r[5].ValueKind == JsonValueKind.Null ? null : Str(r[5]),
                SeatMarkerActive = r[6].ValueKind == JsonValueKind.Null ? null : IntOf(r[6]) == 1,
                Available = IntOf(r[8]) == 1,
                Hidden = IntOf(r[9]) == 1,
            });
        }

        var available = rows.Count(x => x.Available);
        var coverageParts = new List<string> { $"rows={rows.Count}", $"available={available}", $"nonNumericSeatCd={nonNumericSeatCount}" };
        if (ev.TotalCapacityFromSsr is { } cap)
            coverageParts.Add(rows.Count == cap ? $"capacityMatch=yes({cap})" : $"capacityMatch=NO(ssr={cap})");
        if (ev.AvailableFromSsr is { } ssrAvail)
            coverageParts.Add(available == ssrAvail ? $"availableMatch=yes({ssrAvail})" : $"availableMatch=NO(ssr={ssrAvail})");

        return new SeatCrawlResult { Rows = rows, ColumnNames = columns, CoverageNote = string.Join(" ", coverageParts) };
    }

    private static string Str(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind == JsonValueKind.Null ? "" : e.ToString();

    private static int? IntOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;
}
