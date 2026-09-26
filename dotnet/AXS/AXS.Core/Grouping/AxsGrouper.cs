using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AXS.Core.Models;
using AXS.Core.Parsing;

namespace AXS.Core.Grouping
{
    /// <summary>
    /// Seats -> listings and build_result() - 1:1 port of python/axs/grouping.py.
    /// Primary (Veritix) seats: consecutive seat numbers within one (offer, section, row, price
    /// level) become one listing; non-integer seat numbers -> one "Ungrouped" listing per key.
    /// Resale (FLASHSEATS) offers and Marketplace listings are NOT re-grouped.
    /// Sort order uses ordinal string comparison to match Python's tuple sorting exactly.
    /// </summary>
    public static class AxsGrouper
    {
        private static readonly StringComparer Ord = StringComparer.Ordinal;

        /// <summary>(offer_id, price_level_id) -> PriceLevel, preferring price type "Regular";
        /// resale offers keyed (offer_id, "").</summary>
        private static Dictionary<string, PriceLevel> PriceIndex(IEnumerable<PriceLevel> priceLevels)
        {
            var idx = new Dictionary<string, PriceLevel>(Ord);
            foreach (var pl in priceLevels)
            {
                var key = pl.OfferId + "\u0001" + (pl.IsResale ? "" : pl.PriceLevelId);
                PriceLevel cur;
                if (!idx.TryGetValue(key, out cur) ||
                    (pl.PriceTypeLabel.ToLowerInvariant() == "regular" && cur.PriceTypeLabel.ToLowerInvariant() != "regular"))
                    idx[key] = pl;
            }
            return idx;
        }

        private static List<List<int>> Runs(IEnumerable<int> nums)
        {
            var runs = new List<List<int>>();
            var cur = new List<int>();
            foreach (var n in nums)
            {
                if (cur.Count > 0 && n != cur[cur.Count - 1] + 1)
                {
                    runs.Add(cur);
                    cur = new List<int>();
                }
                cur.Add(n);
            }
            if (cur.Count > 0) runs.Add(cur);
            return runs;
        }

        public static List<Listing> GroupIntoListings(List<Seat> seats, List<PriceLevel> priceLevels, Dictionary<string, ResaleMeta> resaleMeta,
            Dictionary<string, OfferRules> offerRules = null)
        {
            offerRules = offerRules ?? new Dictionary<string, OfferRules>();
            Func<string, OfferRules> rulesOf = id => { OfferRules r; offerRules.TryGetValue(id, out r); return r; };
            var idx = PriceIndex(priceLevels);
            var listings = new List<Listing>();

            var primary = seats.Where(s => !s.IsResale)
                .OrderBy(s => s.OfferId, Ord).ThenBy(s => s.Section, Ord).ThenBy(s => s.Row, Ord).ThenBy(s => s.PriceLevelId, Ord);
            foreach (var grp in primary.GroupBy(s => new { s.OfferId, s.Section, s.Row, s.PriceLevelId }))
            {
                var k = grp.Key;
                PriceLevel pl;
                idx.TryGetValue(k.OfferId + "\u0001" + k.PriceLevelId, out pl);
                var byNum = new Dictionary<int, Seat>();
                foreach (var s in grp.Where(s => s.SeatNum.HasValue).OrderBy(s => s.SeatNum.Value))
                    if (!byNum.ContainsKey(s.SeatNum.Value)) byNum[s.SeatNum.Value] = s;
                foreach (var run in Runs(byNum.Keys.OrderBy(n => n)))
                {
                    var l = NewPrimary(k.OfferId, k.Section, k.Row, k.PriceLevelId, pl);
                    l.SeatKeys = run.Select(n => byNum[n].Key).ToList();
                    l.SeatNums = run;
                    l.ApplyRules(rulesOf(k.OfferId));
                    listings.Add(l);
                }
                var other = grp.Where(s => !s.SeatNum.HasValue).ToList();
                if (other.Count > 0)
                {
                    var l = NewPrimary(k.OfferId, k.Section, k.Row, k.PriceLevelId, pl);
                    l.SeatKeys = other.Select(s => s.Key).ToList();
                    l.SeatingType = "Ungrouped";
                    l.ApplyRules(rulesOf(k.OfferId));
                    listings.Add(l);
                }
            }

            var resale = seats.Where(s => s.IsResale)
                .OrderBy(s => s.OfferId, Ord).ThenBy(s => s.Section, Ord).ThenBy(s => s.Row, Ord);
            foreach (var g in resale.GroupBy(s => s.OfferId))
            {
                var grp = g.ToList();
                ResaleMeta meta;
                resaleMeta.TryGetValue(g.Key, out meta);
                meta = meta ?? new ResaleMeta();
                PriceLevel pl;
                idx.TryGetValue(g.Key + "\u0001", out pl);
                var nums = grp.Where(s => s.SeatNum.HasValue).Select(s => s.SeatNum.Value).OrderBy(n => n).ToList();
                var rows = grp.Select(s => s.Row).Distinct(Ord).OrderBy(r => r, Ord).ToList();
                var rl = new Listing
                {
                    OfferId = g.Key,
                    Section = grp[0].Section,
                    Row = rows.Count == 1 ? grp[0].Row : string.Join("/", rows),
                    SeatKeys = grp.OrderBy(s => s.Row, Ord).ThenBy(s => s.SeatNum ?? 0).ThenBy(s => s.Number, Ord).Select(s => s.Key).ToList(),
                    SeatNums = nums.Count == grp.Count ? nums : new List<int>(),
                    SeatingType = "Resale",
                    PriceCents = pl != null ? pl.PriceCents : 0,
                    TotalPrice = meta.TotalPrice,
                    FeePerTicket = meta.ConnectionFee,
                    SplitRule = meta.SplitRule ?? "",
                    SplitQuantities = new List<int>(meta.SplitQuantities),
                    IsResale = true,
                    Zone = grp[0].Neighborhood,
                };
                rl.ApplyRules(rulesOf(g.Key));
                listings.Add(rl);
            }
            return listings;
        }

        private static Listing NewPrimary(string offerId, string section, string row, string priceLevelId, PriceLevel pl)
        {
            return new Listing
            {
                OfferId = offerId,
                Section = section,
                Row = row,
                PriceLevelId = priceLevelId,
                PriceCents = pl != null ? pl.PriceCents : 0,
                Zone = pl != null ? pl.Label : "",
            };
        }

        /// <summary>Marketplace "ladders": a seller posting the SAME seats as quantity 1, 2, .., n (Sound of
        /// Music 1623554: 11 ladders = all 44 listings). Labels them, keeps every listing. A group = same
        /// section (whitespace collapsed), row, price, face value, all-in price, stock type, in-hand date and
        /// notes; a ladder only if 2+ listings, quantities exactly 1..n once each, and each listing's splits
        /// exactly [1..quantity]. Port of python grouping.mark_ladders.</summary>
        public static List<Listing> MarkLadders(List<Listing> listings)
        {
            var groups = new Dictionary<string, List<Listing>>();
            var order = new List<string>();
            foreach (var l in listings)
            {
                var section = System.Text.RegularExpressions.Regex.Replace((l.Section ?? "").Trim(), @"\s+", " ");
                var key = string.Join("\u0001", section, l.Row, l.PriceCents.ToString(CultureInfo.InvariantCulture),
                    l.FaceValue.HasValue ? l.FaceValue.Value.ToString("0.############################", CultureInfo.InvariantCulture) : "null",
                    l.TotalPrice.HasValue ? l.TotalPrice.Value.ToString("0.############################", CultureInfo.InvariantCulture) : "null",
                    l.StockType, l.InHandDate, l.Notes);
                List<Listing> g;
                if (!groups.TryGetValue(key, out g)) { groups[key] = g = new List<Listing>(); order.Add(key); }
                g.Add(l);
            }
            foreach (var key in order)
            {
                var grp = groups[key];
                var qs = grp.Select(l => l.Quantity).OrderBy(q => q).ToList();
                if (grp.Count < 2 || !qs.SequenceEqual(Enumerable.Range(1, grp.Count))) continue;
                if (grp.Any(l => !l.SplitQuantities.SequenceEqual(Enumerable.Range(1, l.Quantity)))) continue;
                var first = grp[0];
                var label = System.Text.RegularExpressions.Regex.Replace((first.Section ?? "").Trim(), @"\s+", " ") + "|" + first.Row + "|" +
                            first.PriceCents.ToString(CultureInfo.InvariantCulture);
                foreach (var l in grp)
                {
                    l.LadderGroup = label;
                    l.IsLadderMax = l.Quantity == grp.Count;
                }
            }
            return listings;
        }

        /// <summary>
        /// event + captured inventory JSON -> {event, price_levels, listings, coverage, notes}.
        /// `inventory` maps capture name ("price", "sections", "offer_search", "mp_offers", ...) to
        /// the raw JSON text the ticket app received. Pure - no network.
        /// </summary>
        public static AxsResult BuildResult(Event ev, IDictionary<string, string> inventory, IEnumerable<string> notes)
        {
            var res = new AxsResult { Event = ev, Notes = (notes ?? Enumerable.Empty<string>()).ToList() };
            string mpOffers;
            if (inventory.TryGetValue("mp_offers", out mpOffers) && mpOffers != null)
            {
                using (var doc = JsonDocument.Parse(mpOffers))
                {
                    var parsed = AxsParser.ParseMarketplaceOffers(doc.RootElement);
                    MarkLadders(parsed.Item1);
                    var listings = parsed.Item1;
                    var meta = parsed.Item2;
                    var tickets = listings.Sum(l => l.Quantity);
                    var mpId = AxsParser.Long(AxsParser.Get(AxsParser.Get(meta, "event"), "id"));
                    if (mpId.HasValue) ev.MarketplaceEventId = mpId;
                    var lc = AxsParser.Long(AxsParser.Get(meta, "listingCount"));
                    var tc = AxsParser.Long(AxsParser.Get(meta, "ticketCount"));
                    var match = lc == listings.Count && tc == tickets ? "yes" : "NO";
                    res.Listings = listings;
                    res.Coverage = "flow=marketplace listings=" + listings.Count + "/" + (lc.HasValue ? lc.ToString() : "None") +
                                   " tickets=" + tickets + "/" + (tc.HasValue ? tc.ToString() : "None") +
                                   " sections_with_tickets=" + listings.Select(l => l.Section).Distinct(Ord).Count() +
                                   " match=" + match;
                    if (listings.Count == 0) res.Notes.Add("marketplace has no listings right now");
                    return res;
                }
            }

            var priceLevels = ParseOrEmpty(inventory, "price", AxsParser.ParsePrice, new List<PriceLevel>());
            var seatsAndMeta = ParseOrEmpty(inventory, "offer_search", AxsParser.ParseSeats,
                Tuple.Create(new List<Seat>(), new Dictionary<string, ResaleMeta>()));
            var sectionCount = ParseOrEmpty(inventory, "sections", AxsParser.SectionCount, 0);
            var seats = seatsAndMeta.Item1;
            var offerRules = ParseOfferRules(inventory);
            var all = GroupIntoListings(seats, priceLevels, seatsAndMeta.Item2, offerRules);
            var resaleCount = seats.Count(s => s.IsResale);
            res.PriceLevels = priceLevels;
            res.Listings = all;
            res.Coverage = "flow=veritix sections=" + sectionCount + " price_levels=" + priceLevels.Count +
                           " seats=" + seats.Count + " (primary=" + (seats.Count - resaleCount) + " resale=" + resaleCount + ")" +
                           " listings=" + all.Count + " seats_in_listings=" + all.Sum(l => l.Quantity);
            return res;
        }

        private static Dictionary<string, OfferRules> ParseOfferRules(IDictionary<string, string> inventory)
        {
            string price, search;
            inventory.TryGetValue("price", out price);
            inventory.TryGetValue("offer_search", out search);
            using (var p = JsonDocument.Parse(price ?? "{}"))
            using (var s = JsonDocument.Parse(search ?? "{}"))
                return AxsParser.ParseOfferRules(p.RootElement, s.RootElement);
        }

        private static T ParseOrEmpty<T>(IDictionary<string, string> inventory, string key, Func<JsonElement, T> parse, T empty)
        {
            string json;
            if (!inventory.TryGetValue(key, out json) || json == null) return empty;
            using (var doc = JsonDocument.Parse(json))
                return parse(doc.RootElement);
        }
    }
}
