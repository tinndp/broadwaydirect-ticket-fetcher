using System.Collections.Generic;

namespace AXS.Core.Models
{
    /// <summary>One AXS event, read off the event page's own __NEXT_DATA__
    /// (props.pageProps.discoveryEventData). 1:1 with python/axs/models.py Event - property names
    /// are the Python field names in PascalCase, so snake_case JSON output matches Python's.</summary>
    public class Event
    {
        public long EventId { get; set; }
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public long? VenueId { get; set; }
        public string VenueName { get; set; } = "";
        public string VenueCity { get; set; } = "";
        public string VenueState { get; set; } = "";
        public List<string> PerformerIds { get; set; } = new List<string>();
        /// <summary>Venue wall clock, no offset, e.g. "2026-10-04T19:00:00".</summary>
        public string EventDtLocal { get; set; }
        /// <summary>Real UTC with "Z" added (AXS omits it), e.g. "2026-10-04T23:00:00Z".</summary>
        public string EventDtUtc { get; set; }
        public string EventTz { get; set; } = "";
        public string DoorDtUtc { get; set; }
        public string OnsaleDtUtc { get; set; }
        public long? StatusId { get; set; }
        public string Status { get; set; } = "";
        /// <summary>shop.axs.com/?c=axs&amp;e=... or tix.axs.com/{token}?axssid=... - only in the HTML
        /// page's __NEXT_DATA__ (the /_next/data/ route strips it).</summary>
        public string TicketUrl { get; set; }
        public long? PublishStatus { get; set; }
        /// <summary>AXS Marketplace's own id (offers.meta.event.id), marketplace flow only.</summary>
        public long? MarketplaceEventId { get; set; }

        public string EventUrl { get { return "https://www.axs.com/events/" + EventId + "/" + Slug; } }
    }

    /// <summary>One (offer, price level, price type) price from inventory/v4/.../price. CENTS as
    /// returned by AXS (7500 = $75.00); converted to dollars only when writing Mongo/JSON.</summary>
    public class PriceLevel
    {
        public string OfferId { get; set; } = "";
        public string OfferName { get; set; } = "";
        public string PriceLevelId { get; set; } = "";
        public string Label { get; set; } = "";
        public string PriceTypeId { get; set; } = "";
        public string PriceTypeLabel { get; set; } = "";
        public long PriceCents { get; set; }
        /// <summary>availability.amount - looks like max per order, NOT stock.</summary>
        public long? Available { get; set; }
        public bool IsResale { get; set; }
    }

    /// <summary>One seat from inventory/V2/.../offer/search (Veritix flow).</summary>
    public class Seat
    {
        public string OfferId { get; set; } = "";
        public string Section { get; set; } = "";
        public string Row { get; set; } = "";
        public string Number { get; set; } = "";
        /// <summary>null when Number isn't an integer.</summary>
        public int? SeatNum { get; set; }
        public string PriceLevelId { get; set; } = "";
        public string SectionId { get; set; } = "";
        public string RowId { get; set; } = "";
        public string SeatId { get; set; } = "";
        public string StatusLabel { get; set; } = "";
        public string SeatType { get; set; } = "";
        public string Neighborhood { get; set; } = "";
        public bool IsGa { get; set; }
        public bool IsResale { get; set; }

        public string Key { get { return Section + "-" + Row + "-" + Number; } }
    }

    /// <summary>Primary: contiguous seats of one (offer, section, row, price level).
    /// Resale: one FLASHSEATS offer as listed. Marketplace: one /axsmarketplace/offers listing
    /// (no seat numbers, only a quantity).</summary>
    public class Listing
    {
        public string OfferId { get; set; } = "";
        public string Section { get; set; } = "";
        public string Row { get; set; } = "";
        public string PriceLevelId { get; set; } = "";
        public List<string> SeatKeys { get; set; } = new List<string>();
        public List<int> SeatNums { get; set; } = new List<int>();
        /// <summary>"Consecutive" | "Ungrouped" | "Resale" | "Marketplace".</summary>
        public string SeatingType { get; set; } = "Consecutive";
        /// <summary>Face/list price per ticket, cents.</summary>
        public long PriceCents { get; set; }
        /// <summary>Resale/marketplace: all-in price per ticket incl. fees, dollars.</summary>
        public decimal? TotalPrice { get; set; }
        public decimal? FeePerTicket { get; set; }
        public string SplitRule { get; set; } = "";
        public List<int> SplitQuantities { get; set; } = new List<int>();
        public bool IsResale { get; set; }
        public string Zone { get; set; } = "";
        public int? QuantityOverride { get; set; }
        public string StockType { get; set; } = "";
        public string InHandDate { get; set; } = "";
        public string Notes { get; set; } = "";
        /// <summary>Marketplace: AXS seat labels, e.g. "Restricted/Obstructed View".</summary>
        public List<string> SeatFeatures { get; set; } = new List<string>();
        /// <summary>Marketplace: faceValue as returned (null on most listings).</summary>
        public decimal? FaceValue { get; set; }
        // Marketplace "ladder" (python/axs/docs/AXS_LISTING_RULES.md): the same seats posted as quantity 1..n. Only
        // LABELLED - every listing is kept. Null when the listing is not in a ladder.
        /// <summary>"&lt;section&gt;|&lt;row&gt;|&lt;price in cents&gt;".</summary>
        public string LadderGroup { get; set; }
        public bool? IsLadderMax { get; set; }
        // Purchase-quantity rules exactly as AXS returns them (no crawler logic; null = not sent).
        /// <summary>Primary: price offerPrices[].min.</summary>
        public int? MinQuantity { get; set; }
        /// <summary>Primary: offerPrices[].max. Marketplace: meta.maxTicketCount.</summary>
        public int? MaxQuantity { get; set; }
        /// <summary>Primary: offerPrices[].increment.</summary>
        public int? QuantityIncrement { get; set; }
        /// <summary>Primary: offer/search offers[].allowEmptySingleSeats.</summary>
        public bool? AllowEmptySingleSeats { get; set; }
        /// <summary>Primary: offer/search offers[].requireContiguousSeats.</summary>
        public bool? RequireContiguousSeats { get; set; }

        /// <summary>Copies an offer's raw rules onto this listing (no-op for null).</summary>
        public void ApplyRules(OfferRules r)
        {
            if (r == null) return;
            MinQuantity = r.MinQuantity;
            MaxQuantity = r.MaxQuantity;
            QuantityIncrement = r.QuantityIncrement;
            AllowEmptySingleSeats = r.AllowEmptySingleSeats;
            RequireContiguousSeats = r.RequireContiguousSeats;
        }

        public int Quantity { get { return QuantityOverride.HasValue ? QuantityOverride.Value : SeatKeys.Count; } }

        public string SeatRange
        {
            get
            {
                if (SeatNums.Count == 0) return "";
                if (SeatNums.Count == 1) return SeatNums[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
                return SeatNums[0].ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                       SeatNums[SeatNums.Count - 1].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Resale offer data that only lives on the offer (not the seat items).</summary>
    /// <summary>One offer's purchase-quantity rules, verbatim - python parser.parse_offer_rules().</summary>
    public class OfferRules
    {
        public int? MinQuantity { get; set; }
        public int? MaxQuantity { get; set; }
        public int? QuantityIncrement { get; set; }
        public bool? AllowEmptySingleSeats { get; set; }
        public bool? RequireContiguousSeats { get; set; }
    }

    public class ResaleMeta
    {
        public decimal? TotalPrice { get; set; }
        public decimal? ConnectionFee { get; set; }
        public string SplitRule { get; set; } = "";
        public List<int> SplitQuantities { get; set; } = new List<int>();
        public string OfferType { get; set; } = "";
    }

    /// <summary>Normalized result shared by Cli and Api - python grouping.build_result().</summary>
    public class AxsResult
    {
        public Event Event { get; set; }
        public List<PriceLevel> PriceLevels { get; set; } = new List<PriceLevel>();
        public List<Listing> Listings { get; set; } = new List<Listing>();
        public string Coverage { get; set; } = "";
        public List<string> Notes { get; set; } = new List<string>();
    }
}
