using System.Text.Json;
using PaciolanEvenue.Core.Grouping;
using PaciolanEvenue.Core.Models;
using PaciolanEvenue.Core.Parsing;
using PaciolanEvenue.Core.Storage;

namespace PaciolanEvenue.Tests;

/// <summary>The listing rule agreed on 2026-09-25 (python/paciolanevenue/docs/EVENUE_INVENTORY_RULES.md). The
/// Fixtures/paciolan_rules files are real responses captured 2026-09-25; expected_python.json is what
/// python/paciolanevenue produces on them - the .NET port must match it listing by listing.</summary>
public class GroupingTests
{
    private static readonly PriceLevel[] Pl1 = { new() { Pl = "1", PlDesc = "Tier 1", Pt = "P", PtDesc = "Public", Price = 5000 } };

    private static SeatRow Seat(string levelSection, string row, string seatCd, string plcd = "1", bool available = true, string status = "O")
    {
        var colon = levelSection.IndexOf(':');
        return new SeatRow
        {
            LevelSectionCd = levelSection,
            Level = colon >= 0 ? levelSection[..colon] : levelSection,
            Section = colon >= 0 ? levelSection[(colon + 1)..] : levelSection,
            RowCd = row,
            SeatCd = seatCd,
            SeatNum = int.TryParse(seatCd, out var n) ? n : null,
            PriceLevelCd = plcd,
            SeatStatus = status,
            Available = available,
        };
    }

    private static EventPageData Ev(IEnumerable<PriceLevel>? pls = null, Dictionary<string, string>? hold = null, Dictionary<string, string>? seating = null) =>
        new() { Host = "h", SeasonCd = "S", ItemCd = "I", PriceLevels = (pls ?? Pl1).ToList(), HoldCodes = hold, SeatingTypes = seating };

    private static (List<SeatRow> Seats, EventPageData Ev) Load(string name, bool withMap = true)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "paciolan_rules");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")));
        var root = doc.RootElement;
        var meta = root.GetProperty("meta");
        var ev = new EventPageData
        {
            Host = meta.GetProperty("host").GetString()!, SeasonCd = meta.GetProperty("season").GetString()!,
            ItemCd = meta.GetProperty("item").GetString()!,
            PriceLevels = root.GetProperty("pl_pt").EnumerateArray().Select(x => new PriceLevel
            {
                Pl = x.GetProperty("PL").ToString(), PlDesc = x.GetProperty("PL_DESC").GetString() ?? "",
                Pt = x.GetProperty("PT").GetString() ?? "", PtDesc = x.GetProperty("PT_DESC").GetString() ?? "",
                Price = x.GetProperty("PRICE").GetInt64(),
            }).ToList(),
        };
        if (withMap)
        {
            ev.HoldCodes = root.GetProperty("hold_codes").EnumerateArray()
                .ToDictionary(h => h.GetProperty("holdcode").GetString()!, h => h.GetProperty("type").GetString() ?? "");
            ev.SeatingTypes = root.GetProperty("seating_types").EnumerateArray()
                .ToDictionary(x => x.GetProperty("pl").ToString(), x => x.GetProperty("seatingType").GetString() ?? "");
        }
        var body = JsonSerializer.Serialize(new object[] { root.GetProperty("rows"), root.GetProperty("columns") });
        return (SeatAvailabilityParser.Parse(body, ev).Rows, ev);
    }

    [Fact]
    public void SplitSeatCode_reads_number_and_letters()
    {
        Assert.Equal((12, ""), SeatGrouper.SplitSeatCode("12"));
        Assert.Equal((1, "W"), SeatGrouper.SplitSeatCode("W1"));
        Assert.Equal((10, "w"), SeatGrouper.SplitSeatCode("10w"));
        Assert.Equal((1, "A"), SeatGrouper.SplitSeatCode("1A"));
        Assert.Equal(((int?)null, "INFANT"), SeatGrouper.SplitSeatCode("INFANT"));
    }

    [Fact]
    public void Consecutive_run_is_one_listing_and_a_missing_number_ends_it()
    {
        var ls = SeatGrouper.BuildListings(new[] { 8, 9, 10, 15, 16 }.Select(n => Seat("E:105", "10", n.ToString())).ToList(), Ev());
        Assert.Equal(new[] { "10:8,10:9,10:10", "10:15,10:16" }, ls.OrderBy(l => l.SeatNums[0]).Select(l => string.Join(",", l.SeatKeys)));
        Assert.All(ls, l => Assert.Equal("Consecutive", l.SeatingType));
        Assert.All(ls, l => Assert.Equal(("E", "105"), (l.Level, l.Section)));
    }

    [Fact]
    public void Sold_seats_shape_the_section_and_unpriced_levels_are_never_listed()
    {
        var seats = Enumerable.Range(1, 6).Select(n => Seat("E:1", "A", n.ToString(), available: n is 1 or 3)).ToList();
        seats.Add(Seat("E:1", "B", "1", plcd: "9"));
        var ls = SeatGrouper.BuildListings(seats, Ev());
        Assert.Equal(new[] { 1, 3 }, ls.Select(l => l.SeatNums.Single()).OrderBy(x => x));
        Assert.All(ls, l => Assert.Equal("Consecutive", l.SeatingType));
    }

    [Fact]
    public void Odd_even_needs_four_same_parity_seats_without_neighbours()
    {
        var seats = new[] { 15, 17, 19, 21 }.Select(n => Seat("L:LEFT", "A", n.ToString()))
            .Concat(new[] { 16, 18, 20 }.Select(n => Seat("L:RGHT", "A", n.ToString())))
            .Concat(new[] { 1, 3, 5, 6 }.Select(n => Seat("L:CTR", "A", n.ToString()))).ToList();
        Assert.Equal(new[] { "L:LEFT" }, SeatGrouper.OddEvenSections(seats));

        var ls = SeatGrouper.BuildListings(new[] { 15, 17, 19, 23, 25, 29 }.Select(n => Seat("L:LEFT", "A", n.ToString())).ToList(), Ev())
            .OrderBy(l => l.SeatNums[0]).Select(l => (l.SeatNums.First(), l.SeatNums.Last(), l.SeatingType));
        Assert.Equal(new[] { (15, 19, "Odd/Even"), (23, 25, "Odd/Even"), (29, 29, "Consecutive") }, ls);
    }

    [Fact]
    public void Codes_without_digits_become_one_listing_and_no_Ungrouped_value_is_left()
    {
        var ls = SeatGrouper.BuildListings(new List<SeatRow> { Seat("GA:GEN", "GEN", "INFANT"), Seat("GA:GEN", "GEN", "LAP"), Seat("E:1", "A", "10w") }, Ev());
        var nc = ls.Single(l => l.SeatTag == "NC1");
        Assert.Equal((0, 2), (nc.SeatNums.Count, nc.Quantity));
        Assert.All(ls, l => Assert.Equal("Consecutive", l.SeatingType));
    }

    [Fact]
    public void Ga_counts_available_hold_codes_whatever_the_AVAILABLE_flag_says()
    {
        var hold = new Dictionary<string, string> { ["O"] = "available", ["w"] = "accessible", ["K"] = "hidden" };
        var seats = new[] { (1, "O"), (2, "O"), (3, "w"), (4, "K"), (5, "X") }
            .Select(x => Seat("GA:GA", "1", x.Item1.ToString(), available: false, status: x.Item2)).ToList();
        var ls = SeatGrouper.BuildListings(seats, Ev(hold: hold, seating: new() { ["1"] = "G" }));
        var g = Assert.Single(ls);
        Assert.Equal((2, "GA", "GA", "Tier 1", "PL1", "G"), (g.Quantity, g.Level, g.Row, g.Section, g.SeatTag, g.SeatingTypeCd));
        Assert.Empty(g.SeatNums);
    }

    [Fact]
    public void Without_event_map_it_falls_back_to_the_AVAILABLE_flag()
    {
        var (seats, ev) = Load("ku_iowa_state_06_ga", withMap: false);
        Assert.DoesNotContain(seats, s => s.Available);
        Assert.Empty(SeatGrouper.BuildListings(seats, ev));
    }

    [Theory]
    [InlineData("ucla_royce_370")]
    [InlineData("ku_iowa_state_06_ga")]
    [InlineData("mgoblue_v07_wc")]
    public void Real_events_match_python_listing_by_listing(string name)
    {
        var (seats, ev) = Load(name);
        var ls = SeatGrouper.BuildListings(seats, ev);
        var sourceEventId = $"{ev.Host}:{ev.SeasonCd}:{ev.ItemCd}";
        var net = ls.ToDictionary(l => ListingIdentity.BuildPaciolanEvenue(sourceEventId, l.Level, l.Section, l.Row,
            l.SeatNums.Count > 0 ? l.SeatNums[0] : null, l.SeatNums.Count > 0 ? l.SeatNums[^1] : null, l.SeatTag));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "paciolan_rules", "expected_python.json")));
        var exp = doc.RootElement.GetProperty(name);
        Assert.Equal(exp.GetProperty("source_event_id").GetString(), sourceEventId);
        var py = exp.GetProperty("docs").EnumerateArray().ToList();
        Assert.Equal(py.Count, net.Count);
        foreach (var p in py)
        {
            var id = p.GetProperty("_id").GetString()!;
            Assert.True(net.ContainsKey(id), $"python _id {id} missing in .NET output");
            var l = net[id];
            Assert.Equal(p.GetProperty("Level").GetString(), l.Level);
            Assert.Equal(p.GetProperty("Section").GetString(), l.Section);
            Assert.Equal(p.GetProperty("Row").GetString(), l.Row);
            Assert.Equal(p.GetProperty("Quantity").GetInt32(), l.Quantity);
            Assert.Equal(p.GetProperty("Seating").GetString(), l.SeatingType);
            Assert.Equal(p.GetProperty("PriceLevelId").GetInt64(), long.Parse(l.PriceLevelCd)); // PriceLevelId = PRICELEVELCD as a number
            Assert.Equal(p.GetProperty("SeatKeys").ValueKind == JsonValueKind.Null ? "" : p.GetProperty("SeatKeys").GetString(), string.Join(",", l.SeatKeys));
            Assert.Equal(p.GetProperty("SeatStatusType").ValueKind == JsonValueKind.Null ? null : p.GetProperty("SeatStatusType").GetString(),
                SeatGrouper.SeatStatusType(l, ev));
        }
    }

    [Fact]
    public void Royce_hall_odd_even_sections_and_nothing_lost()
    {
        var (seats, ev) = Load("ucla_royce_370");
        Assert.Equal(new[] { "1:BLCOR", "1:BLCTR", "1:BLEFT", "1:BRCOR", "1:BRCTR", "1:BRGHT", "1:LEFT", "1:RGHT" },
            SeatGrouper.OddEvenSections(seats).OrderBy(x => x, StringComparer.Ordinal));
        var ls = SeatGrouper.BuildListings(seats, ev);
        Assert.Equal(seats.Count(s => s.Available), ls.Sum(l => l.Quantity));
        Assert.Equal(70, ls.Count(l => l.SeatingType == "Odd/Even"));
    }

    [Fact]
    public void Event_map_query_and_response()
    {
        var ev = new EventPageData { SeasonCd = "F26", ItemCd = "F04", DataAccountId = "242", FacCd = "RA", ConfigurationCd = "T",
            PolicyCd = "IBM:DEFAULT:I", PolicyType = "I", DistributorId = "IBM", BaseMapId = "1947" };
        var q = JsonDocument.Parse(EventMapQuery.BuildBody(ev)).RootElement.GetProperty("query").GetString()!;
        Assert.Contains("itemCd: \"F04\"", q);
        Assert.Contains("availability: \"A|S\"", q);
        Assert.Contains("HOLDCODES", q);
        EventMapQuery.Apply(ev, "{\"data\":{\"maps_eventMap\":{\"SEATING_TYPES\":[{\"pl\":\"6\",\"seatingType\":\"G\"}],\"HOLDCODES\":[{\"holdcode\":\"O\",\"title\":\"Open Seats\",\"message\":null,\"type\":\"available\"}]}}}");
        Assert.Equal("available", ev.HoldCodes!["O"]);
        Assert.Equal("G", ev.SeatingTypes!["6"]);
    }
}
