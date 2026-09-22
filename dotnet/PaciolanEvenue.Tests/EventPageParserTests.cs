using PaciolanEvenue.Core.Parsing;

namespace PaciolanEvenue.Tests;

public class EventPageParserTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_event_page.html"));

    [Fact]
    public void Parse_recognizes_a_real_event_page()
    {
        var html = LoadFixture();
        var ev = EventPageParser.Parse(html, "purduesports.evenue.net", "F26", "F06");

        Assert.True(ev.IsEventPage);
        Assert.Equal("242", ev.DataAccountId);
        Assert.Equal("242", ev.DistributorId);
        Assert.Equal("242:DEFAULT:I", ev.PolicyCd);
        Assert.Equal("I", ev.PolicyType);
        Assert.Equal("Purdue vs Test Opponent", ev.EventName);
        Assert.Equal("Ross-Ade Stadium", ev.FacilityTitle);
        Assert.False(ev.SoldOut);
        Assert.Equal(3, ev.TotalCapacityFromSsr);
        Assert.Equal(2, ev.AvailableFromSsr);
    }

    [Fact]
    public void Parse_reads_EVENTDT_as_a_true_UTC_instant()
    {
        var ev = EventPageParser.Parse(LoadFixture(), "purduesports.evenue.net", "F26", "F06");
        Assert.NotNull(ev.EventDtUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero), ev.EventDtUtc);
    }

    [Fact]
    public void Parse_reads_price_levels()
    {
        var ev = EventPageParser.Parse(LoadFixture(), "purduesports.evenue.net", "F26", "F06");
        Assert.Equal(2, ev.PriceLevels.Count);
        var first = ev.PriceLevels[0];
        Assert.Equal("1", first.Pl);
        Assert.Equal("East Midfield", first.PlDesc);
        Assert.Equal("P", first.Pt);
        Assert.Equal(9500, first.Price);
    }

    [Fact]
    public void Parse_returns_IsEventPage_false_for_a_non_event_page()
    {
        var html = "<html><body>not an event page, no __NEXT_DATA__ here</body></html>";
        var ev = EventPageParser.Parse(html, "purduesports.evenue.net", "F26", "DOES-NOT-EXIST");
        Assert.False(ev.IsEventPage);
    }

    [Fact]
    public void BuildPath_matches_the_documented_recon_shape()
    {
        var ev = EventPageParser.Parse(LoadFixture(), "purduesports.evenue.net", "F26", "F06");
        var path = SeatAvailabilityParser.BuildPath(ev);
        Assert.StartsWith("/pac-api/seat-availability/event-id/242%3AF26%3AF06/seats", path);
        Assert.Contains("availability=A%7CS", path);
    }
}
