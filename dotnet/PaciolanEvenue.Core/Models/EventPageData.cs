namespace PaciolanEvenue.Core.Models;

/// <summary>Everything read off one event page (/event/{seasonCd}/{itemCd}) - the 4 identifiers
/// the seat-availability API needs, read by EventPageParser from the page's own __NEXT_DATA__
/// script tag. Nothing is guessed or hard-coded per school.</summary>
public class EventPageData
{
    public string Host { get; set; } = "";
    public string SeasonCd { get; set; } = "";
    public string ItemCd { get; set; } = "";

    /// <summary>True only when the page actually rendered a single event (Next.js
    /// pageProps.context == "eventdetailpage").</summary>
    public bool IsEventPage { get; set; }

    public string DataAccountId { get; set; } = "";
    public string DistributorId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string LinkId { get; set; } = "";
    public string PolicyCd { get; set; } = "";
    public string PolicyType { get; set; } = "";

    public string EventName { get; set; } = "";
    public string FacilityTitle { get; set; } = "";

    /// <summary>EVENTDT from SSR - a real UTC instant. Null if missing/unparseable.</summary>
    public DateTimeOffset? EventDtUtc { get; set; }

    /// <summary>EVENTDTFAC from SSR, kept as the RAW string. Despite the trailing "Z" it is NOT a
    /// UTC instant - venue-local wall-clock time with "Z" appended. Do not parse this as UTC.</summary>
    public string? EventDtFacRaw { get; set; }

    public bool HideTime { get; set; }
    public bool HideDateTime { get; set; }
    public bool SoldOut { get; set; }

    /// <summary>SSR TOTALCAPACITY / AVAILABLE - cross-check only against what the
    /// seat-availability API actually returns.</summary>
    public int? TotalCapacityFromSsr { get; set; }
    public int? AvailableFromSsr { get; set; }

    /// <summary>Event-level purchase-quantity rules, verbatim from SSR (MINQTY / MAXQTY /
    /// MULTIPLEQTY). Null = not sent (the Mongo document also stores 0 as null - eVenue's "not set").</summary>
    public int? MinQty { get; set; }
    public int? MaxQty { get; set; }
    public int? MultipleQty { get; set; }

    /// <summary>What the maps_eventMap GraphQL query needs: FAC_CD, CONFIGURATIONCD, props.baseMapId.</summary>
    public string FacCd { get; set; } = "";
    public string ConfigurationCd { get; set; } = "";
    public string BaseMapId { get; set; } = "";

    /// <summary>ALLOWSEATMAP. False = quantity-only / best-available page, which does NOT request
    /// seat availability itself (so there is nothing to capture - see PaciolanEvenuePlaywrightClient).</summary>
    public bool? AllowSeatMap { get; set; }

    /// <summary>maps_eventMap HOLDCODES (SEATSTATUS code -> type: available / accessible / limited /
    /// hidden) and SEATING_TYPES (price level -> "R" reserved / "G" GA), verbatim. Null = not fetched
    /// or failed - SeatGrouper then falls back to AVAILABLE == 1.</summary>
    public Dictionary<string, string>? HoldCodes { get; set; }
    public Dictionary<string, string>? SeatingTypes { get; set; }

    public List<PriceLevel> PriceLevels { get; set; } = new();

    public string EventUrl => $"https://{Host}/event/{SeasonCd}/{ItemCd}";
}
