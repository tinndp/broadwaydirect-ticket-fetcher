namespace PaciolanEvenue.Core.Models;

/// <summary>One row of the seat-availability response (GET
/// /pac-api/seat-availability/event-id/{...}/seats). Column order/names come from the response's
/// own columnNames array and are validated positionally - see SeatAvailabilityParser.ExpectedColumns.</summary>
public class SeatRow
{
    /// <summary>Raw "LEVEL:SECTION", e.g. "E:105". Kept for the grouping bucket key - the Mongo
    /// document splits it into Level/Section instead of storing this combined form.</summary>
    public string LevelSectionCd { get; set; } = "";
    public string Level { get; set; } = "";
    public string Section { get; set; } = "";
    public string RowCd { get; set; } = "";

    /// <summary>Raw seat code - a STRING, not guaranteed numeric (bleacher/GA labels like "C-1"
    /// were seen). SeatNum is the parsed int form, null when it doesn't parse.</summary>
    public string SeatCd { get; set; } = "";
    public int? SeatNum { get; set; }

    public string PriceLevelCd { get; set; } = "";
    public string SeatStatus { get; set; } = "";

    /// <summary>Non-null on ~2% of seats sampled during recon. Possibly an accessible/marker seat
    /// flag but UNCONFIRMED for this deployment. Not used to exclude anything here.</summary>
    public string? MarkerId { get; set; }
    public bool? SeatMarkerActive { get; set; }

    public bool Available { get; set; }
    public bool Hidden { get; set; }

    /// <summary>Stable per-seat key for de-dup / Mongo SeatKeys: "Row:SeatCd" only - Level and
    /// Section are both dropped (they live on the listing's own Level/Section fields instead;
    /// user's final instruction 2026-09-21: drop level+section from seatkeys, keep row+seat).</summary>
    public string SeatKey => $"{RowCd}:{SeatCd}";
}
