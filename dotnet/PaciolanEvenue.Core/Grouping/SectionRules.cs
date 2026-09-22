namespace PaciolanEvenue.Core.Grouping;

/// <summary>Section-naming / grouping rules - same shape as BroadwayDirect.Core's SectionRules
/// (user asked for "same rule as Broadway"), but the DEFAULTS are deliberately inert: Broadway's
/// threshold (100) and prefixes describe one specific theater's chart, never confirmed against
/// eVenue's section numbering. CenterSeatThreshold = 0 means every numbered seat takes the plain
/// Consecutive branch (no SIDES/CENTER split) until real per-venue rules are supplied.</summary>
public class SectionRules
{
    public List<string> BoxPrefixes { get; set; } = new();
    public int CenterSeatThreshold { get; set; } = 0;
    public string SidesSuffix { get; set; } = "";
    public string CenterSuffix { get; set; } = "";

    public static SectionRules Default() => new();
}
