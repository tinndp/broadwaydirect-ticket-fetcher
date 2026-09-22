using MongoDB.Driver;
using PaciolanEvenue.Core.Models;

namespace PaciolanEvenue.Core.Storage;

/// <summary>
/// Persists PaciolanEvenue listings matching the REAL .NET Rowing bot's shape - ONE SHARED
/// collection for the whole datasource (<c>PaciolanEvenue_Inventories_NEW</c>, no event id in the
/// name), keyed by the SourceEventId field inside each document, one document per listing.
///
/// Deliberately DIFFERENT from this repo's other two stores:
///   - BroadwayDirect.Core.Storage.MongoStore's shared raw_events/cleaned_events shape (that shape
///     is this repo's own prototype convention, not what the real Rowing pipeline reads).
///   - StubHub.Core.Storage.StubHubInventoryStore's per-event collection name
///     (StubHub_Inventories_NEW_{eventId}) - unverified here whether that matches StubHub's actual
///     Rowing bot (which lives on a different branch/architecture); PaciolanEvenue's real Rowing
///     bot (ETECH.Application.MarkAutomation, Rowing/PaciolanEvenue/) was directly confirmed
///     (2026-09-22) via SettingFactory.GetIntegrationNewInventoryCollectionName + every reference
///     to it in that codebase to use a SHARED collection, not a per-event one - a name that looked
///     per-event during an earlier live-Mongo inspection turned out to be stale/leftover data, not
///     the real convention. This store follows that confirmed shape - see the Python
///     paciolanevenue package's mongo_inventory.py for the same correction.
///
/// This is the ONLY place PaciolanEvenue listing data lands in this dotnet/ demo - a failure here
/// should be treated as fatal by callers (PaciolanEvenue.Api), not swallowed.
/// </summary>
public sealed class PaciolanEvenueInventoryStore
{
    public const string CollectionName = "PaciolanEvenue_Inventories_NEW";

    private readonly IMongoDatabase _db;

    public PaciolanEvenueInventoryStore(string uri = "mongodb://localhost:27017", string dbName = "broadwaydirect")
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        var client = new MongoClient(settings);
        client.GetDatabase("admin").RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        _db = client.GetDatabase(dbName);
    }

    /// <summary>Replaces the snapshot of one source event: deleteMany({SourceEventId: eventId})
    /// then insertMany - matches IntegrationSourceNewListingMongoRepository.SaveFilteredListingsAsync
    /// exactly, NOT a delete of the whole collection (the collection is shared across every event
    /// of this datasource). Throws on any write failure.</summary>
    public void SaveEventInventory(string sourceEventId, IReadOnlyList<PaciolanEvenueListing> listings)
    {
        var col = _db.GetCollection<PaciolanEvenueListing>(CollectionName);
        col.Indexes.CreateOne(new CreateIndexModel<PaciolanEvenueListing>(
            Builders<PaciolanEvenueListing>.IndexKeys.Ascending(l => l.SourceEventId)));

        col.DeleteMany(Builders<PaciolanEvenueListing>.Filter.Eq(l => l.SourceEventId, sourceEventId));
        if (listings.Count > 0)
            col.InsertMany(listings);
    }

    public void Close() { /* MongoClient in the .NET driver needs no manual disposal */ }
}
