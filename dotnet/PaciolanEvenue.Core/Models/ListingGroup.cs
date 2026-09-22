namespace PaciolanEvenue.Core.Models;

/// <summary>A run of contiguous seats produced by SeatGrouper - the sellable unit. Level/Section
/// are separate fields (not a combined SectionLabel) - matches the Python port's final
/// Listing.level / Listing.section split (see ~/etech/broadwaydirect-ticket-fetcher/python/
/// paciolanevenue/models.py and the ETECH.Application.MarkAutomation Rowing integration).</summary>
public class ListingGroup
{
    public string Level { get; init; } = "";
    public string Section { get; init; } = "";
    public string Row { get; init; } = "";
    public string PriceLevelCd { get; init; } = "";
    public List<string> SeatKeys { get; init; } = new();

    /// <summary>Empty when every seat in this listing had a non-numeric SeatCd (SeatingType ==
    /// "Ungrouped").</summary>
    public List<int> SeatNums { get; init; } = new();
    public List<string> SeatCds { get; init; } = new();

    /// <summary>"Consecutive", "OddEven", or "Ungrouped".</summary>
    public string SeatingType { get; init; } = "Consecutive";

    public int Quantity => SeatKeys.Count;
}
