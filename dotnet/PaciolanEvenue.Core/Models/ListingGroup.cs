namespace PaciolanEvenue.Core.Models;

/// <summary>One sellable listing produced by SeatGrouper. Level/Section are separate fields (not a
/// combined SectionLabel) - matches python/paciolanevenue/models.py Listing.</summary>
public class ListingGroup
{
    public string Level { get; init; } = "";
    public string Section { get; init; } = "";
    public string Row { get; init; } = "";
    public string PriceLevelCd { get; init; } = "";
    public List<string> SeatKeys { get; init; } = new();

    /// <summary>Empty for a GA quantity listing and for seat codes with no digits.</summary>
    public List<int> SeatNums { get; init; } = new();
    public List<string> SeatCds { get; init; } = new();

    /// <summary>POS vocabulary only: "Consecutive" | "Odd/Even".</summary>
    public string SeatingType { get; init; } = "Consecutive";

    /// <summary>Letters of a lettered seat code ("W" for W1, "w" for 10w), "PL{code}" for a GA quantity
    /// listing, "NC{code}" for codes without digits; "" for plain numbered seats. Part of the listing
    /// fingerprint only when set (ListingIdentity).</summary>
    public string SeatTag { get; init; } = "";

    /// <summary>maps_eventMap SEATING_TYPES of the price level ("R" / "G" / ""). Internal - not stored in Mongo.</summary>
    public string SeatingTypeCd { get; init; } = "";

    /// <summary>Distinct SEATSTATUS codes of the listing's seats, sorted. Internal - only their HOLDCODES
    /// type is stored (SeatStatusType), the codes themselves mean different things per school.</summary>
    public List<string> SeatStatuses { get; init; } = new();

    /// <summary>GA quantity listing: no seat keys, just a count.</summary>
    public int? QuantityOverride { get; init; }

    public int Quantity => QuantityOverride ?? SeatKeys.Count;
}
