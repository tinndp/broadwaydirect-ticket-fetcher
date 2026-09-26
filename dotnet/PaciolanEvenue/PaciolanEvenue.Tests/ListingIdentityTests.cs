using PaciolanEvenue.Core.Storage;

namespace PaciolanEvenue.Tests;

public class ListingIdentityTests
{
    [Fact]
    public void BuildPaciolanEvenue_is_deterministic_and_13_chars()
    {
        var id1 = ListingIdentity.BuildPaciolanEvenue("purduesports.evenue.net:F26:F06", "E", "105", "10", 8, 10);
        var id2 = ListingIdentity.BuildPaciolanEvenue("purduesports.evenue.net:F26:F06", "E", "105", "10", 8, 10);

        Assert.Equal(id1, id2);
        Assert.Equal(13, id1.Length);
        Assert.Equal(id1, id1.ToUpperInvariant());
        Assert.True(id1.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')));
    }

    [Fact]
    public void Different_sections_sharing_a_level_do_not_collide()
    {
        var id1 = ListingIdentity.BuildPaciolanEvenue("purduesports.evenue.net:F26:F06", "E", "105", "10", 8, 10);
        var id2 = ListingIdentity.BuildPaciolanEvenue("purduesports.evenue.net:F26:F06", "E", "106", "10", 8, 10);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Fingerprint_shape_matches_the_Python_ported_test_vector()
    {
        // Python: build_listing_id("purduesports.evenue.net:F26:F06", "E_10_8_10")
        var direct = ListingIdentity.Build("purduesports.evenue.net:F26:F06", "E_10_8_10");
        var viaHelper = ListingIdentity.BuildPaciolanEvenue("purduesports.evenue.net:F26:F06", "E", "", "10", 8, 10);
        Assert.Equal(direct, viaHelper);
    }

    [Fact]
    public void Missing_low_high_seat_renders_as_the_literal_None_like_Python()
    {
        var fp = ListingIdentity.PaciolanEvenueFingerprint("E", "105", "GEN", null, null);
        Assert.Equal("E:105_GEN_None_None", fp);
    }
}
