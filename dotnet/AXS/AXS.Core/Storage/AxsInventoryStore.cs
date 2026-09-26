using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AXS.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using PaciolanEvenue.Core.Storage;

namespace AXS.Core.Storage
{
    /// <summary>
    /// AXS listings in the REAL .NET Rowing Mongo staging shape - port of python/axs/mongo_inventory.py:
    /// ONE shared collection per datasource (<c>AXS_Inventories_NEW</c>), one document per listing,
    /// keyed by SourceEventId; each save = deleteMany({SourceEventId}) + insertMany
    /// (IntegrationSourceNewListingMongoRepository.SaveFilteredListingsAsync). Same deviation from
    /// BroadwayDirect.Core's raw_events/cleaned_events as PaciolanEvenueInventoryStore, same reason.
    /// </summary>
    public static class AxsDocuments
    {
        public const string CollectionName = "AXS_Inventories_NEW";

        public static string SourceEventId(Event ev) { return ev.EventId.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Broadway's Section_Row_Low_High plus the offer id (two offers can both have an
        /// "Ungrouped" listing in the same row). Missing low/high render as "" - matches Python's
        /// f-string there. Step 4 must add the same fingerprint to ListingIdentity.cs.</summary>
        public static string Fingerprint(Listing l)
        {
            var low = l.SeatNums.Count > 0 ? l.SeatNums[0].ToString(CultureInfo.InvariantCulture) : "";
            var high = l.SeatNums.Count > 0 ? l.SeatNums[l.SeatNums.Count - 1].ToString(CultureInfo.InvariantCulture) : "";
            return l.Section + "_" + l.Row + "_" + low + "_" + high + "_" + l.OfferId;
        }

        public static string ListingId(Event ev, Listing l) { return ListingIdentity.Build(SourceEventId(ev), Fingerprint(l)); }

        private static BsonValue Dec(decimal? v) { return v.HasValue ? (BsonValue)new BsonDecimal128(v.Value) : BsonNull.Value; }
        private static BsonValue IntOrNull(int? v) { return v.HasValue ? (BsonValue)v.Value : BsonNull.Value; }
        private static BsonValue QtyOrNull(int? v) { return v.HasValue && v.Value != 0 ? (BsonValue)v.Value : BsonNull.Value; }
        private static BsonValue StrOrNull(string s) { return string.IsNullOrEmpty(s) ? (BsonValue)BsonNull.Value : new BsonString(s); }

        public static BsonDocument ToDocument(Event ev, Listing l, DateTime nowUtc)
        {
            var price = Math.Round(l.PriceCents / 100m, 2);
            int priceLevelId;
            // resale / marketplace have no price level -> null (not 0)
            var hasPriceLevel = int.TryParse(l.PriceLevelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out priceLevelId);
            var priceClass = l.SeatingType == "Marketplace" ? "Marketplace" : (l.IsResale ? "Resale" : "Primary");
            return new BsonDocument
            {
                { "_id", ListingId(ev, l) },
                { "SourceEventId", SourceEventId(ev) },
                { "Section", l.Section },
                { "Row", l.Row },
                { "LowSeat", l.SeatNums.Count > 0 ? (BsonValue)l.SeatNums[0] : BsonNull.Value },
                { "HighSeat", l.SeatNums.Count > 0 ? (BsonValue)l.SeatNums[l.SeatNums.Count - 1] : BsonNull.Value },
                { "Quantity", l.Quantity },
                // POS vocabulary is Consecutive / Odd/Even; AXS runs are consecutive numbers or seat-less and no
                // odd/even row was seen on 17 real events (python/axs/docs/AXS_LISTING_RULES.md). The listing kind is PriceClass.
                { "Seating", "Consecutive" },
                { "Price", new BsonDecimal128(price) },
                { "PublicNotes", StrOrNull((l.Notes ?? "").Trim()) },   // marketplace seller notes
                { "PrivateNotes", BsonNull.Value },
                { "Splits", StrOrNull(string.Join(",", l.SplitQuantities.Select(q => q.ToString(CultureInfo.InvariantCulture)))) },
                { "BrokerOwned", BsonNull.Value },
                { "LastApiSyncedDateTimeUtc", nowUtc },
                { "PriceLevelId", hasPriceLevel ? (BsonValue)priceLevelId : BsonNull.Value },
                { "Zone", StrOrNull(l.Zone) },
                { "DisplayPrice", new BsonDecimal128(price) },
                { "PriceClass", priceClass },
                { "SeatKeys", StrOrNull(string.Join(",", l.SeatKeys)) }, // marketplace: no seat numbers
                { "OfferId", l.OfferId },
                { "TotalPrice", Dec(l.TotalPrice) },
                { "FeePerTicket", Dec(l.FeePerTicket) },
                { "SplitRule", StrOrNull(l.SplitRule) },
                { "StockType", StrOrNull(l.StockType) },
                { "InHandDate", StrOrNull(l.InHandDate) },
                // purchase-quantity rules, verbatim from AXS (null = AXS didn't send it)
                // a quantity rule of 0 means "not set" -> null, like a missing one
                { "MinQuantity", QtyOrNull(l.MinQuantity) },
                { "MaxQuantity", QtyOrNull(l.MaxQuantity) },
                { "QuantityIncrement", QtyOrNull(l.QuantityIncrement) },
                { "AllowEmptySingleSeats", l.AllowEmptySingleSeats.HasValue ? (BsonValue)l.AllowEmptySingleSeats.Value : BsonNull.Value },
                { "RequireContiguousSeats", l.RequireContiguousSeats.HasValue ? (BsonValue)l.RequireContiguousSeats.Value : BsonNull.Value },
                // marketplace, verbatim / labels only: faceValue and the "ladder" (same seats posted as 1..n)
                { "FaceValue", Dec(l.FaceValue) },
                { "SeatFeatures", StrOrNull(string.Join(",", l.SeatFeatures)) }, // AXS labels, comma-joined
                { "LadderGroup", StrOrNull(l.LadderGroup) },
                { "IsLadderMax", l.IsLadderMax.HasValue ? (BsonValue)l.IsLadderMax.Value : BsonNull.Value },
            };
        }
    }

    public sealed class AxsInventoryStore
    {
        private readonly IMongoDatabase _db;

        public AxsInventoryStore(string uri, string dbName)
        {
            var settings = MongoClientSettings.FromConnectionString(uri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            var client = new MongoClient(settings);
            client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            _db = client.GetDatabase(dbName);
        }

        /// <summary>Rewrites ONE event's slice of the shared collection - 0 listings clears it
        /// (sold out). Throws on Mongo failure; the Api treats persistence as best-effort.</summary>
        public int SaveEventInventory(Event ev, IList<Listing> listings)
        {
            var col = _db.GetCollection<BsonDocument>(AxsDocuments.CollectionName);
            var now = DateTime.UtcNow;
            var docs = listings.Select(l => AxsDocuments.ToDocument(ev, l, now)).ToList();
            col.DeleteMany(new BsonDocument("SourceEventId", AxsDocuments.SourceEventId(ev)));
            if (docs.Count > 0) col.InsertMany(docs);
            return docs.Count;
        }

        public long CountTickets(string sourceEventId, out int listings)
        {
            var docs = _db.GetCollection<BsonDocument>(AxsDocuments.CollectionName)
                .Find(new BsonDocument("SourceEventId", sourceEventId)).ToList();
            listings = docs.Count;
            return docs.Sum(d => (long)d["Quantity"].ToInt32());
        }
    }
}
