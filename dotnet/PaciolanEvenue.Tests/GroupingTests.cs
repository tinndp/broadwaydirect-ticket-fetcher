using PaciolanEvenue.Core.Grouping;
using PaciolanEvenue.Core.Models;
using PaciolanEvenue.Core.Parsing;

namespace PaciolanEvenue.Tests;

public class GroupingTests
{
    private static SeatRow Seat(string levelSection, string row, string seatCd, string plcd = "1", bool available = true)
    {
        var colon = levelSection.IndexOf(':');
        var seatNum = int.TryParse(seatCd, out var n) ? n : (int?)null;
        return new SeatRow
        {
            LevelSectionCd = levelSection,
            Level = colon >= 0 ? levelSection[..colon] : levelSection,
            Section = colon >= 0 ? levelSection[(colon + 1)..] : levelSection,
            RowCd = row,
            SeatCd = seatCd,
            SeatNum = seatNum,
            PriceLevelCd = plcd,
            Available = available,
        };
    }

    [Fact]
    public void DefaultRules_are_always_consecutive_no_suffix()
    {
        var (suffix, seatingType) = SeatGrouper.ClassifySection("101", 5, SectionRules.Default());
        Assert.Equal("", suffix);
        Assert.Equal("Consecutive", seatingType);

        (suffix, seatingType) = SeatGrouper.ClassifySection("101", 500, SectionRules.Default());
        Assert.Equal("", suffix);
        Assert.Equal("Consecutive", seatingType);
    }

    [Fact]
    public void Contiguous_seats_group_into_one_listing()
    {
        var seats = new[] { Seat("E:105", "10", "8"), Seat("E:105", "10", "9"), Seat("E:105", "10", "10") };
        var listings = SeatGrouper.GroupIntoListings(seats);

        Assert.Single(listings);
        Assert.Equal(3, listings[0].Quantity);
        Assert.Equal("Consecutive", listings[0].SeatingType);
    }

    [Fact]
    public void Level_only_and_section_only_are_split_on_the_listing()
    {
        var seats = new[] { Seat("E:105", "10", "8"), Seat("E:105", "10", "9") };
        var listings = SeatGrouper.GroupIntoListings(seats);

        Assert.Equal("E", listings[0].Level);
        Assert.Equal("105", listings[0].Section);
        // seat_keys drop BOTH Level and Section now - just "Row:SeatCd".
        Assert.Equal(new[] { "10:8", "10:9" }, listings[0].SeatKeys);
    }

    [Fact]
    public void Different_sections_sharing_a_level_are_not_merged()
    {
        var seats = new[] { Seat("E:105", "10", "8"), Seat("E:106", "10", "8") };
        var listings = SeatGrouper.GroupIntoListings(seats);

        Assert.Equal(2, listings.Count);
        var sections = listings.Select(l => l.Section).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "105", "106" }, sections);
    }

    [Fact]
    public void Splits_on_a_gap_in_seat_numbers()
    {
        var seats = new[] { Seat("E:105", "10", "8"), Seat("E:105", "10", "9"), Seat("E:105", "10", "10"), Seat("E:105", "10", "15"), Seat("E:105", "10", "16") };
        var listings = SeatGrouper.GroupIntoListings(seats);
        Assert.Equal(2, listings.Count);
    }

    [Fact]
    public void Splits_on_a_price_level_change()
    {
        var seats = new[] { Seat("E:105", "10", "8", "1"), Seat("E:105", "10", "9", "2") };
        var listings = SeatGrouper.GroupIntoListings(seats);
        Assert.Equal(2, listings.Count);
    }

    [Fact]
    public void Non_numeric_seat_becomes_its_own_ungrouped_listing()
    {
        var seats = new[] { Seat("E:105", "10", "8"), Seat("GA", "GEN", "INFANT") };
        var listings = SeatGrouper.GroupIntoListings(seats);
        var ungrouped = listings.Where(l => l.SeatingType == "Ungrouped").ToList();

        Assert.Single(ungrouped);
        Assert.Equal(new[] { "INFANT" }, ungrouped[0].SeatCds);
        Assert.Empty(ungrouped[0].SeatNums);
    }

    [Fact]
    public void Singleton_run_is_forced_consecutive_even_in_an_oddeven_bucket()
    {
        var rules = new SectionRules { CenterSeatThreshold = 100, SidesSuffix = "SIDES", CenterSuffix = "CENTER" };
        var seats = new[] { Seat("E:105", "10", "8") }; // seatNum=8 <= threshold -> OddEven bucket
        var listings = SeatGrouper.GroupIntoListings(seats, rules);

        Assert.Single(listings);
        Assert.Equal("Consecutive", listings[0].SeatingType);
    }

    [Fact]
    public void End_to_end_fixture_produces_3_listings()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_seat_availability.json"));
        var ev = new EventPageData { Host = "purduesports.evenue.net", SeasonCd = "F26", ItemCd = "F06" };
        var result = SeatAvailabilityParser.Parse(body, ev);
        var available = result.Rows.Where(r => r.Available).ToList();

        var listings = SeatGrouper.GroupIntoListings(available);

        Assert.Equal(3, listings.Count); // E:105(8,9) + E:106(8) + GA ungrouped(INFANT)
        Assert.Contains(listings, l => l.Level == "E" && l.Section == "105" && l.Quantity == 2);
        Assert.Contains(listings, l => l.Level == "E" && l.Section == "106" && l.Quantity == 1);
        Assert.Contains(listings, l => l.SeatingType == "Ungrouped");
    }
}
