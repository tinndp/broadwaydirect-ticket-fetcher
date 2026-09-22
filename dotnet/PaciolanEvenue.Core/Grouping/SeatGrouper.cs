using PaciolanEvenue.Core.Models;

namespace PaciolanEvenue.Core.Grouping;

/// <summary>
/// Groups individual available seats into sellable "listings" (runs of adjacent seats in the same
/// level/section/row/price level) - a direct port of BroadwayDirect.Core.Grouping.SeatGrouper's
/// algorithm, per the user's "same rule as Broadway" instruction. The BUCKET KEY still uses the
/// full LevelSectionCd (Level+Section combined) so two different sections sharing a level are
/// never merged - only the OUTPUT (ListingGroup.Level / .Section) is split, per the Python port's
/// field-naming decision (2026-09-21/22).
///
/// Two things could NOT be ported as-is from Broadway and are called out rather than guessed:
///   1. Accessible/ADA seats: eVenue's seat rows have no confirmed ADA field - the closest
///      candidate, MARKER_ID/SEAT_MARKER_ACTIVE, is UNCONFIRMED. This grouper does NOT exclude
///      anything based on it.
///   2. SectionRules defaults: see SectionRules's own doc comment - deliberately inert here.
/// </summary>
public static class SeatGrouper
{
    /// <summary>Same shape as Broadway's ClassifySection: a Box-prefix exemption (no suffix,
    /// always Consecutive), else seatNum &gt; threshold -&gt; CENTER/Consecutive, else
    /// SIDES/OddEven (split further by parity in GroupIntoListings). A seat with no parseable
    /// seat number always comes back Consecutive/no-suffix.</summary>
    public static (string Suffix, string SeatingType) ClassifySection(string section, int? seatNum, SectionRules rules)
    {
        var sectionUpper = (section ?? "").ToUpperInvariant();
        if (rules.BoxPrefixes.Any(p => p.Length > 0 && sectionUpper.StartsWith(p.ToUpperInvariant())))
            return ("", "Consecutive");

        if (seatNum == null)
            return ("", "Consecutive");

        return seatNum.Value > rules.CenterSeatThreshold
            ? (rules.CenterSuffix, "Consecutive")
            : (rules.SidesSuffix, "OddEven");
    }

    /// <summary>Groups already-Available==true-filtered seats into listings. Bucket key =
    /// (LevelSectionCd incl. suffix, row, price level, seating type, parity if OddEven). Seats
    /// whose SeatCd did not parse to a number cannot be tested for numeric adjacency; each becomes
    /// its own singleton listing, seating type "Ungrouped".</summary>
    public static List<ListingGroup> GroupIntoListings(IEnumerable<SeatRow> availableSeats, SectionRules? rules = null)
    {
        rules ??= SectionRules.Default();

        var buckets = new Dictionary<(string Label, string Row, string PriceLevelCd, string SeatingType, int Parity), List<SeatRow>>();
        var bucketOrder = new List<(string Label, string Row, string PriceLevelCd, string SeatingType, int Parity)>();
        // Suffix per bucket, keyed the same way - needed because MakeListing appends it onto
        // ListingGroup.Level (e.g. "OK SIDES"), matching grouping.py's _make_listing exactly (see
        // that fix, 2026-09-22). Currently a no-op in practice since SectionRules' default suffixes
        // are both "" (see SectionRules's own doc comment), but was silently dropped before this
        // fix - would have diverged from the Python port the moment real per-venue suffixes are
        // configured.
        var bucketSuffix = new Dictionary<(string Label, string Row, string PriceLevelCd, string SeatingType, int Parity), string>();
        var ungrouped = new List<SeatRow>();

        foreach (var s in availableSeats)
        {
            var (suffix, seatingType) = ClassifySection(s.Section, s.SeatNum, rules);

            if (s.SeatNum == null)
            {
                ungrouped.Add(s);
                continue;
            }

            var label = suffix.Length > 0 ? $"{s.LevelSectionCd} {suffix}" : s.LevelSectionCd;
            var parity = seatingType == "OddEven" ? s.SeatNum.Value % 2 : -1;
            var key = (Label: label, Row: s.RowCd, PriceLevelCd: s.PriceLevelCd, SeatingType: seatingType, Parity: parity);

            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<SeatRow>();
                buckets[key] = list;
                bucketOrder.Add(key);
                bucketSuffix[key] = suffix;
            }
            list.Add(s);
        }

        var listings = new List<ListingGroup>();

        foreach (var key in bucketOrder)
        {
            var group = buckets[key];
            var suffix = bucketSuffix[key];
            var step = key.SeatingType == "OddEven" ? 2 : 1;
            var sorted = group.OrderBy(s => s.SeatNum!.Value).ToList();

            var run = new List<SeatRow> { sorted[0] };
            for (var i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var cur = sorted[i];
                if (cur.SeatNum!.Value - prev.SeatNum!.Value == step)
                {
                    run.Add(cur);
                }
                else
                {
                    listings.Add(MakeListing(suffix, key.Row, key.PriceLevelCd, key.SeatingType, run));
                    run = new List<SeatRow> { cur };
                }
            }
            listings.Add(MakeListing(suffix, key.Row, key.PriceLevelCd, key.SeatingType, run));
        }

        foreach (var s in ungrouped)
        {
            listings.Add(new ListingGroup
            {
                Level = s.Level,
                Section = s.Section,
                Row = s.RowCd,
                PriceLevelCd = s.PriceLevelCd,
                SeatKeys = new List<string> { s.SeatKey },
                SeatCds = new List<string> { s.SeatCd },
                SeatingType = "Ungrouped",
            });
        }

        return listings;
    }

    private static ListingGroup MakeListing(string suffix, string row, string priceLevelCd, string seatingType, List<SeatRow> run)
    {
        // A run of exactly one seat is always reported as Consecutive, even if it landed in an
        // OddEven bucket - matches Broadway's MakeListing (OddEven only means something for more
        // than one seat).
        if (run.Count <= 1) seatingType = "Consecutive";

        // Every seat in `run` shares the same bucket, hence the same Level/Section. Level carries
        // the grouping-rule suffix too (e.g. "OK SIDES") - matches grouping.py's _make_listing:
        // `label = f"{level} {suffix}".strip() if suffix else level`.
        var level = suffix.Length > 0 ? $"{run[0].Level} {suffix}" : run[0].Level;
        return new ListingGroup
        {
            Level = level,
            Section = run[0].Section,
            Row = row,
            PriceLevelCd = priceLevelCd,
            SeatKeys = run.Select(s => s.SeatKey).ToList(),
            SeatNums = run.Select(s => s.SeatNum!.Value).ToList(),
            SeatCds = run.Select(s => s.SeatCd).ToList(),
            SeatingType = seatingType,
        };
    }
}
