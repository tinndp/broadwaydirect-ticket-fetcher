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

    public long PriceLevelId { get; set; }
    public string? PriceLevelCd { get; set; }
    public string? Zone { get; set; }
    public decimal DisplayPrice { get; set; }
    public string? PriceClass { get; set; }
    public string? SeatKeys { get; set; }
}
