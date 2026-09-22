using PaciolanEvenue.Core.Models;
using PaciolanEvenue.Core.Parsing;

namespace PaciolanEvenue.Tests;

public class SeatAvailabilityParserTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_seat_availability.json"));

    private static EventPageData Ev() => new()
    {
        Host = "purduesports.evenue.net", SeasonCd = "F26", ItemCd = "F06",
        TotalCapacityFromSsr = 6, AvailableFromSsr = 4,
    };

    [Fact]
    public void Parse_splits_LevelSectionCd_and_computes_SeatKey()
    {
        var result = SeatAvailabilityParser.Parse(LoadFixture(), Ev());
        Assert.Equal(6, result.Rows.Count);

        var first = result.Rows[0];
        Assert.Equal("E:105", first.LevelSectionCd);
        Assert.Equal("E", first.Level);
        Assert.Equal("105", first.Section);
        Assert.Equal("10", first.RowCd);
        Assert.Equal("8", first.SeatCd);
        Assert.Equal(8, first.SeatNum);
        Assert.True(first.Available);
        // seat_key is Row:SeatCd only - Level/Section both dropped (final field-naming decision).
        Assert.Equal("10:8", first.SeatKey);
    }

    [Fact]
    public void Parse_flags_non_numeric_seat_codes()
    {
        var result = SeatAvailabilityParser.Parse(LoadFixture(), Ev());
        var ga = result.Rows.Single(r => r.LevelSectionCd == "GA");
        Assert.Null(ga.SeatNum);
        Assert.Equal("INFANT", ga.SeatCd);
    }

    [Fact]
    public void Parse_reads_marker_id_when_present()
    {
        var result = SeatAvailabilityParser.Parse(LoadFixture(), Ev());
        var marked = result.Rows.Single(r => r.LevelSectionCd == "W:200");
        Assert.Equal("18", marked.MarkerId);
        Assert.True(marked.SeatMarkerActive);
        Assert.False(marked.Available);
    }

    [Fact]
    public void Parse_coverage_note_reports_capacity_and_available_match()
    {
        var result = SeatAvailabilityParser.Parse(LoadFixture(), Ev());
        Assert.Contains("rows=6", result.CoverageNote);
        Assert.Contains("available=4", result.CoverageNote);
        Assert.Contains("capacityMatch=yes(6)", result.CoverageNote);
        Assert.Contains("availableMatch=yes(4)", result.CoverageNote);
    }
}
