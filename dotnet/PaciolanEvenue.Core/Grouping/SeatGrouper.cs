using System.Text.RegularExpressions;
using PaciolanEvenue.Core.Models;

namespace PaciolanEvenue.Core.Grouping;

/// <summary>
/// Seats -> listings, the rule agreed with the user on 2026-09-25 after 72 real events on 12 eVenue
/// hosts (python/EVENUE_INVENTORY_RULES.md). 1:1 port of python/paciolanevenue/grouping.py
/// build_listings - same output on the same input (checked by the parity test vectors).
///
/// 1. Only price levels with a public price (PL_PT_PRICES) are sold.
/// 2. Sellable seat: price level SEATING_TYPES "G" (GA) -> SEATSTATUS whose HOLDCODES type is
///    "available", whatever AVAILABLE says (quantity-only pages report AVAILABLE=0 for every seat
///    while selling them); anything else -> AVAILABLE == 1. No maps_eventMap data -> AVAILABLE == 1.
/// 3. GA: ONE quantity listing per price level (Section = PL_DESC, Row = "GA", no seat numbers).
/// 4. Reserved: seats of one (section, row, price level, seat tag) grouped into runs. A SECTION is
///    Odd/Even when, over EVERY numbered seat of the map (sold too), it has at least 4 seats, all odd
///    or all even, and no n / n+1 pair. Runs step 2 there, else 1; a missing number ends a run; a
///    1-seat run is Consecutive.
/// 5. Lettered codes (W1, 10w, 1A): number = seat number, letters = tag, only equal tags group.
///    Codes with no digits: one listing per (section, row, price level) without seat numbers.
/// </summary>
public static partial class SeatGrouper
{
    public const string SellableGaHoldType = "available";

    [GeneratedRegex(@"^([A-Za-z]*)(\d+)([A-Za-z]*)$")]
    private static partial Regex CodeRegex();

    /// <summary>"12" -> (12, ""), "W1" -> (1, "W"), "10w" -> (10, "w"), "INFANT" -> (null, "INFANT").</summary>
    public static (int? Num, string Tag) SplitSeatCode(string? seatCd)
    {
        var m = CodeRegex().Match(seatCd ?? "");
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var n))
            return (null, seatCd ?? "");
        return (n, m.Groups[1].Value + m.Groups[3].Value);
    }

    /// <summary>LevelSectionCd of every Odd/Even section, judged on the whole seat map.</summary>
    public static HashSet<string> OddEvenSections(IEnumerable<SeatRow> allSeats)
    {
        var nums = new Dictionary<string, HashSet<int>>();
        foreach (var s in allSeats)
        {
            var (n, tag) = SplitSeatCode(s.SeatCd);
            if (n == null || tag.Length > 0) continue;
            if (!nums.TryGetValue(s.LevelSectionCd, out var set)) nums[s.LevelSectionCd] = set = new HashSet<int>();
            set.Add(n.Value);
        }
        return nums.Where(kv => kv.Value.Count >= 4 && kv.Value.Select(n => n % 2).Distinct().Count() == 1 &&
                                !kv.Value.Any(n => kv.Value.Contains(n + 1)))
                   .Select(kv => kv.Key).ToHashSet();
    }

    public static bool IsSellable(SeatRow seat, EventPageData ev)
    {
        if (ev.HoldCodes == null || ev.SeatingTypes == null)
            return seat.Available;
        if (ev.SeatingTypes.TryGetValue(seat.PriceLevelCd, out var st) && st == "G")
            return ev.HoldCodes.TryGetValue(seat.SeatStatus, out var type) && type == SellableGaHoldType;
        return seat.Available;
    }

    /// <summary>allSeats = the WHOLE seat-availability response (every status) - needed to judge
    /// Odd/Even sections. Returns listings for sellable seats only.</summary>
    public static List<ListingGroup> BuildListings(IReadOnlyList<SeatRow> allSeats, EventPageData ev)
    {
        var priced = ev.PriceLevels.Select(p => p.Pl).ToHashSet();
        var plDesc = new Dictionary<string, string>();
        foreach (var pl in ev.PriceLevels)
            plDesc.TryAdd(pl.Pl, pl.PlDesc);
        var seatingTypes = ev.SeatingTypes ?? new Dictionary<string, string>();
        string TypeOf(string pl) => seatingTypes.TryGetValue(pl, out var t) ? t : "";

        var listings = new List<ListingGroup>();
        var ga = new Dictionary<string, List<SeatRow>>();
        var reservedKeys = new List<(string Lsc, string Row, string Pl, string? Tag)>();
        var reserved = new Dictionary<(string Lsc, string Row, string Pl, string? Tag), List<(int? N, SeatRow S)>>();
        foreach (var s in allSeats)
        {
            if (!priced.Contains(s.PriceLevelCd) || !IsSellable(s, ev)) continue;
            if (TypeOf(s.PriceLevelCd) == "G")
            {
                if (!ga.TryGetValue(s.PriceLevelCd, out var g)) ga[s.PriceLevelCd] = g = new List<SeatRow>();
                g.Add(s);
                continue;
            }
            var (n, tag) = SplitSeatCode(s.SeatCd);
            var key = (s.LevelSectionCd, s.RowCd, s.PriceLevelCd, n != null ? tag : (string?)null);
            if (!reserved.TryGetValue(key, out var items))
            {
                reserved[key] = items = new List<(int?, SeatRow)>();
                reservedKeys.Add(key);
            }
            items.Add((n, s));
        }

        foreach (var pl in ga.Keys.OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal))
        {
            var seats = ga[pl];
            listings.Add(new ListingGroup
            {
                Level = "GA",
                Section = plDesc.TryGetValue(pl, out var d) && d.Length > 0 ? d : "General Admission",
                Row = "GA",
                PriceLevelCd = pl,
                SeatTag = $"PL{pl}",
                SeatingType = "Consecutive",
                SeatingTypeCd = "G",
                SeatStatuses = Distinct(seats.Select(x => x.SeatStatus)),
                QuantityOverride = seats.Count,
            });
        }

        var oddEven = OddEvenSections(allSeats);
        foreach (var key in reservedKeys)
        {
            var items = reserved[key];
            var first = items[0].S;
            if (key.Tag == null) // no digits in the code at all
            {
                var seats = items.Select(x => x.S).ToList();
                listings.Add(new ListingGroup
                {
                    Level = first.Level, Section = first.Section, Row = key.Row, PriceLevelCd = key.Pl,
                    SeatingTypeCd = TypeOf(key.Pl),
                    SeatKeys = seats.Select(x => x.SeatKey).ToList(),
                    SeatCds = seats.Select(x => x.SeatCd).ToList(),
                    SeatTag = $"NC{key.Pl}",
                    SeatStatuses = Distinct(seats.Select(x => x.SeatStatus)),
                });
                continue;
            }

            var step = key.Tag.Length == 0 && oddEven.Contains(key.Lsc) ? 2 : 1;
            var byNum = new SortedDictionary<int, SeatRow>();
            foreach (var (n, s) in items.OrderBy(x => x.N!.Value))
                byNum.TryAdd(n!.Value, s);

            var run = new List<int>();
            void Flush()
            {
                if (run.Count == 0) return;
                var seats = run.Select(x => byNum[x]).ToList();
                listings.Add(new ListingGroup
                {
                    Level = first.Level, Section = first.Section, Row = key.Row, PriceLevelCd = key.Pl,
                    SeatingTypeCd = TypeOf(key.Pl),
                    SeatKeys = seats.Select(x => x.SeatKey).ToList(),
                    SeatNums = new List<int>(run),
                    SeatCds = seats.Select(x => x.SeatCd).ToList(),
                    SeatTag = key.Tag,
                    SeatingType = step == 2 && run.Count > 1 ? "Odd/Even" : "Consecutive",
                    SeatStatuses = Distinct(seats.Select(x => x.SeatStatus)),
                });
                run = new List<int>();
            }
            foreach (var n in byNum.Keys)
            {
                if (run.Count > 0 && n - run[^1] != step) Flush();
                run.Add(n);
            }
            Flush();
        }
        return listings;
    }

    /// <summary>Standard eVenue category of the listing's seats: the HOLDCODES types of their SEATSTATUS
    /// codes, e.g. "available" or "accessible, available". Null when HOLDCODES could not be read.
    /// (SEATSTATUS codes are per school - "c" is Camera at one school, Companion Seat at another - so
    /// only this standard type is stored.)</summary>
    public static string? SeatStatusType(ListingGroup listing, EventPageData ev)
    {
        if (ev.HoldCodes == null || ev.HoldCodes.Count == 0) return null;
        var types = listing.SeatStatuses
            .Select(c => ev.HoldCodes.TryGetValue(c, out var t) ? t : null)
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!)
            .Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        return types.Count > 0 ? string.Join(", ", types) : null;
    }

    private static List<string> Distinct(IEnumerable<string> xs) =>
        xs.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
}
