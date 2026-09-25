using MongoDB.Bson.Serialization.Attributes;

namespace PaciolanEvenue.Core.Models;

/// <summary>
/// One Mongo document, matching the REAL .NET Rowing bot's shape
/// (ETECH.Application.Library.SK4DataSource.Core.Models.Templates.PaciolanEvenueSourceInventory /
/// IntegrationTemplateSourceInventory) - PascalCase fields, one document per listing. See
/// PaciolanEvenueInventoryStore's own doc comment for the collection-naming decision.
/// </summary>
[BsonIgnoreExtraElements]
public class PaciolanEvenueListing
{
    [BsonId]
    public string Id { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public string? Level { get; set; }
    public string? Section { get; set; }
    public string? Row { get; set; }
    public int? LowSeat { get; set; }
    public int? HighSeat { get; set; }
    public int Quantity { get; set; }
    public string? Seating { get; set; }
    public decimal Price { get; set; }
    public string? PublicNotes { get; set; }
    public string? PrivateNotes { get; set; }
    public string? Splits { get; set; }
    public bool? BrokerOwned { get; set; }
    public DateTime? LastApiSyncedDateTimeUtc { get; set; }

    /// <summary>eVenue PRICELEVELCD as a number (same field name as Broadway/AXS); null if not numeric.</summary>
    public long? PriceLevelId { get; set; }
    public string? Zone { get; set; }
    public decimal DisplayPrice { get; set; }
    public string? PriceClass { get; set; }
    public string? SeatKeys { get; set; }

    // Purchase-quantity rules from the event page SSR (null = not sent, or 0 = eVenue's "not set").
    // Event level: MINQTY / MAXQTY / MULTIPLEQTY.
    public int? MinQuantity { get; set; }
    public int? MaxQuantity { get; set; }
    public int? QuantityIncrement { get; set; }
    // PLPT_MINQTY / PLPT_MAXQTY / PLPT_MULTIPLE of the same PL_PT_PRICES row the Price comes from.
    public int? PriceLevelMinQuantity { get; set; }
    public int? PriceLevelMaxQuantity { get; set; }
    public int? PriceLevelQuantityIncrement { get; set; }

    /// <summary>Special seats only (HOLDCODES type): "accessible" = wheelchair / ADA / companion, "limited" =
    /// obstructed view. Null = regular seats.</summary>
    public string? SeatStatusType { get; set; }

    /// <summary>Part of the Id fingerprint (ListingIdentity) - stored so Rowing can rebuild the same Id.</summary>
    public string? SeatTag { get; set; }
}
