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

    [Fact]
    public void Parse_copies_quantity_rules_verbatim()
    {
        // Real soonersports F26/F03 values (2026-09-25): MINQTY 0, MAXQTY 8, MULTIPLEQTY 0. 0 stays 0.
        var html = LoadFixture().Replace("\"POLICYTYPE\": \"I\",",
            "\"POLICYTYPE\": \"I\",\"MINQTY\":0,\"MAXQTY\":8,\"MULTIPLEQTY\":0,\"STUDENTMAXQTY\":0,");
        Assert.Contains("\"MAXQTY\":8", html);
        var ev = EventPageParser.Parse(html, "purduesports.evenue.net", "F26", "F06");
        Assert.Equal((int?)0, ev.MinQty);
        Assert.Equal((int?)8, ev.MaxQty);
        Assert.Equal((int?)0, ev.MultipleQty);
    }

    [Fact]
    public void Parse_leaves_quantity_rules_null_when_evenue_omits_them()
    {
        var ev = EventPageParser.Parse(LoadFixture(), "purduesports.evenue.net", "F26", "F06");
        Assert.Null(ev.MaxQty);
        // the fixture's PL_PT_PRICES rows send PLPT_MINQTY 1 / PLPT_MAXQTY 8 but no PLPT_MULTIPLE
        Assert.All(ev.PriceLevels, pl => { Assert.Equal((int?)1, pl.PlptMinQty); Assert.Equal((int?)8, pl.PlptMaxQty); Assert.Null(pl.PlptMultiple); });
    }
}
