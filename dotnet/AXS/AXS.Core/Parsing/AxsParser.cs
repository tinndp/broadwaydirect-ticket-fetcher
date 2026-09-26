using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AXS.Core.Models;

namespace AXS.Core.Parsing
{
    /// <summary>The page/JSON has no discoveryEventData - wrong id, removed event, or a redirect.
    /// Not a block - retrying won't help. python/axs/parser.py NotAnEventPage.</summary>
    public class NotAnEventPageException : Exception
    {
        public NotAnEventPageException(string message) : base(message) { }
    }

    /// <summary>
    /// Pure parsing, no browser/network - 1:1 port of python/axs/parser.py (same field choices,
    /// same fallbacks, same "x or y" truthiness), so the same raw JSON gives the same objects on
    /// both sides (AXS.Tests checks this against vectors produced by the Python code).
    /// </summary>
    public static class AxsParser
    {
        private static readonly Regex EventUrlRe = new Regex(
            @"^https?://(?:www\.)?axs\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?events/(\d+)(?:/([^/?#]+))?",
            RegexOptions.IgnoreCase);

        /// <summary>"https://www.axs.com/events/1457730/tom-jones-21-event-tickets" -> (1457730, "tom-jones-21-event-tickets"), else null.</summary>
        public static Tuple<long, string> ParseEventUrl(string url)
        {
            var m = EventUrlRe.Match(url ?? "");
            if (!m.Success) return null;
            return Tuple.Create(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Success ? m.Groups[2].Value : "");
        }

        // ---- JSON helpers mirroring Python dict.get / truthiness ---------------------------

        internal static JsonElement Get(JsonElement el, string name)
        {
            JsonElement v;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out v)) return v;
            return default(JsonElement); // ValueKind.Undefined
        }

        internal static bool Has(JsonElement el, string name)
        {
            JsonElement v;
            return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out v);
        }

        internal static bool Truthy(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString().Length > 0;
                case JsonValueKind.Number: return el.GetDouble() != 0;
                case JsonValueKind.True: return true;
                case JsonValueKind.Array: return el.GetArrayLength() > 0;
                case JsonValueKind.Object: return el.EnumerateObject().Any();
                default: return false;
            }
        }

        /// <summary>Python str(x) for a JSON scalar; null for null/missing.</summary>
        internal static string Str(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number: return el.GetRawText();
                case JsonValueKind.True: return "True";
                case JsonValueKind.False: return "False";
                default: return null;
            }
        }

        /// <summary>Python `str(a or b or ... or "")` - first truthy value as string, else "".</summary>
        internal static string Or(params JsonElement[] els)
        {
            foreach (var e in els)
                if (Truthy(e)) return Str(e) ?? "";
            return "";
        }

        internal static long? Long(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number) return (long)el.GetDouble();
            long v;
            if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        internal static decimal? Dec(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
            return null;
        }

        private static JsonElement Obj(JsonElement el) { return el.ValueKind == JsonValueKind.Object ? el : default(JsonElement); }

        private static IEnumerable<JsonElement> Arr(JsonElement el)
        {
            return el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : Enumerable.Empty<JsonElement>();
        }

        /// <summary>Python int(str(s)); null when not an integer.</summary>
        internal static int? ToInt(string s)
        {
            int v;
            if (s != null && int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        /// <summary>AXS's *UTC fields are real UTC but have no "Z" (verified across DST in RECON.md).</summary>
        internal static string Utc(JsonElement el)
        {
            var s = Str(el);
            if (string.IsNullOrEmpty(s)) return null;
            return s.EndsWith("Z") || (s.Length > 10 && s.Substring(10).Contains("+")) ? s : s + "Z";
        }

        // ---- event page ---------------------------------------------------------------------

        /// <summary>Full __NEXT_DATA__ ({props:{pageProps}}) or a /_next/data/ response ({pageProps}).</summary>
        public static JsonElement PageProps(JsonElement nextData)
        {
            if (Has(nextData, "props")) return Obj(Get(Get(nextData, "props"), "pageProps"));
            return Obj(Get(nextData, "pageProps"));
        }

        public static Event ParseEvent(JsonElement nextData)
        {
            var pp = PageProps(nextData);
            var d = Get(pp, "discoveryEventData");
            if (!Truthy(d) || !Truthy(Get(d, "eventId")))
                throw new NotAnEventPageException("no discoveryEventData (redirect=" + (Str(Get(pp, "__N_REDIRECT")) ?? "None") + ")");
            var venue = Obj(Get(d, "venue"));
            var title = Obj(Get(d, "title"));
            var ticketing = Obj(Get(d, "ticketing"));
            // The ticket link only exists in the HTML page's __NEXT_DATA__; /_next/data/ strips it.
            var t2 = Obj(Get(pp, "ticketingEventData"));
            var ticketUrl = Or(Get(ticketing, "url"), Get(ticketing, "ticketURL"), Get(t2, "url"), Get(t2, "ticketURL"));
            var assoc = Obj(Get(d, "associations"));

            return new Event
            {
                EventId = Long(Get(d, "eventId")).Value,
                Slug = Or(Get(d, "urlSlug")),
                Name = Or(Get(title, "eventTitleText"), Get(title, "headlinersText")),
                VenueId = Long(Get(venue, "venueId")),
                VenueName = Or(Get(venue, "title")),
                VenueCity = Or(Get(venue, "city")),
                VenueState = Or(Get(venue, "state")),
                PerformerIds = Arr(Get(assoc, "performerIds")).Select(p => Str(p) ?? "None").ToList(),
                EventDtLocal = Str(Get(d, "eventDatetime")),
                EventDtUtc = Utc(Get(d, "eventDatetimeUTC")),
                EventTz = Or(Get(d, "eventDatetimeTz"), Get(d, "eventDatetimeZone"), Get(venue, "timeZone")),
                DoorDtUtc = Utc(Get(d, "doorDatetimeUTC")),
                OnsaleDtUtc = Utc(Get(d, "onsaleDatetimeUTC")),
                StatusId = Has(ticketing, "statusId") ? Long(Get(ticketing, "statusId")) : Long(Get(t2, "statusId")),
                Status = Or(Get(ticketing, "status"), Get(t2, "status")),
                TicketUrl = ticketUrl.Length > 0 ? ticketUrl : null,
                PublishStatus = Long(Get(d, "publishStatus")),
            };
        }

        // ---- Veritix commerce API -----------------------------------------------------------

        /// <summary>AXS Official Resale (FLASHSEATS) offers have 16-digit ids starting 9000000...</summary>
        public static bool IsResaleOffer(string offerId)
        {
            return (offerId ?? "").StartsWith("9000000", StringComparison.Ordinal);
        }

        /// <summary>inventory/v4/{token}/price -> one PriceLevel per (offer, price level, price type).</summary>
        public static List<PriceLevel> ParsePrice(JsonElement priceJson)
        {
            var result = new List<PriceLevel>();
            foreach (var offer in Arr(Get(priceJson, "offerPrices")))
            {
                var offerId = Or(Get(offer, "offerID"));
                var resale = IsResaleOffer(offerId);
                foreach (var zp in Arr(Get(offer, "zonePrices")))
                {
                    var typeLabels = new Dictionary<string, string>();
                    foreach (var pt in Arr(Get(zp, "priceTypes")))
                        typeLabels[Str(Get(pt, "priceTypeID")) ?? "None"] = Or(Get(pt, "label"));
                    foreach (var pl in Arr(Get(zp, "priceLevels")))
                    {
                        foreach (var pr in Arr(Get(pl, "prices")))
                        {
                            string label;
                            typeLabels.TryGetValue(Str(Get(pr, "priceTypeID")) ?? "None", out label);
                            result.Add(new PriceLevel
                            {
                                OfferId = offerId,
                                OfferName = Or(Get(offer, "offerName")),
                                PriceLevelId = Or(Get(pl, "priceLevelID")),
                                Label = Or(Get(pl, "label")),
                                PriceTypeId = Or(Get(pr, "priceTypeID")),
                                PriceTypeLabel = label ?? "",
                                PriceCents = Truthy(Get(pr, "base")) ? (long)Get(pr, "base").GetDouble() : 0,
                                Available = Long(Get(Obj(Get(pl, "availability")), "amount")),
                                IsResale = resale,
                            });
                        }
                    }
                }
            }
            return result;
        }

        internal static int? IntOrNull(JsonElement el)
        {
            return el.ValueKind == JsonValueKind.Number ? (int?)(int)el.GetDouble() : null;
        }

        internal static bool? BoolOrNull(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        /// <summary>offer id -> purchase-quantity rules copied verbatim (null when AXS doesn't send the
        /// field): min/max/increment from price offerPrices[], allowEmptySingleSeats /
        /// requireContiguousSeats from offer/search offers[]. Port of python parse_offer_rules.</summary>
        public static Dictionary<string, OfferRules> ParseOfferRules(JsonElement priceJson, JsonElement offerSearchJson)
        {
            var rules = new Dictionary<string, OfferRules>();
            Func<string, OfferRules> at = id =>
            {
                OfferRules r;
                if (!rules.TryGetValue(id, out r)) rules[id] = r = new OfferRules();
                return r;
            };
            foreach (var offer in Arr(Get(priceJson, "offerPrices")))
            {
                var r = at(Or(Get(offer, "offerID")));
                r.MinQuantity = IntOrNull(Get(offer, "min"));
                r.MaxQuantity = IntOrNull(Get(offer, "max"));
                r.QuantityIncrement = IntOrNull(Get(offer, "increment"));
            }
            foreach (var offer in Arr(Get(offerSearchJson, "offers")))
            {
                var r = at(Or(Get(offer, "offerID")));
                r.AllowEmptySingleSeats = BoolOrNull(Get(offer, "allowEmptySingleSeats"));
                r.RequireContiguousSeats = BoolOrNull(Get(offer, "requireContiguousSeats"));
            }
            return rules;
        }

        /// <summary>inventory/V2/{token}/offer/search -> (seats, resale offer meta by offer id).</summary>
        public static Tuple<List<Seat>, Dictionary<string, ResaleMeta>> ParseSeats(JsonElement offerSearchJson)
        {
            var seats = new List<Seat>();
            var meta = new Dictionary<string, ResaleMeta>();
            foreach (var offer in Arr(Get(offerSearchJson, "offers")))
            {
                var offerId = Or(Get(offer, "offerID"));
                var resale = IsResaleOffer(offerId) || Str(Get(offer, "offerType")) == "FLASHSEATS";
                if (resale)
                {
                    meta[offerId] = new ResaleMeta
                    {
                        TotalPrice = Dec(Get(offer, "totalPrice")),
                        ConnectionFee = Dec(Get(offer, "connectionFee")),
                        SplitRule = Or(Get(offer, "purchasableQuantityRule")),
                        SplitQuantities = Arr(Get(offer, "purchasableQuantityList")).Select(q => (int)q.GetDouble()).ToList(),
                        OfferType = Or(Get(offer, "offerType")),
                    };
                }
                foreach (var it in Arr(Get(offer, "items")))
                {
                    var number = Or(Get(it, "number"));
                    seats.Add(new Seat
                    {
                        OfferId = offerId,
                        Section = Or(Get(it, "sectionLabel")),
                        Row = Or(Get(it, "rowLabel")),
                        Number = number,
                        SeatNum = ToInt(number),
                        PriceLevelId = Or(Get(it, "priceLevelID")),
                        SectionId = Or(Get(it, "sectionID")),
                        RowId = Or(Get(it, "rowID")),
                        SeatId = Or(Get(it, "id")),
                        StatusLabel = Or(Get(it, "statusCodeLabel"), Get(it, "priceCodeLabel")),
                        SeatType = Or(Get(it, "seatType")),
                        Neighborhood = Or(Get(it, "neighborhoodPrintDescription"), Get(it, "neighborhoodLabel")),
                        IsGa = Truthy(Get(it, "isGASection")),
                        IsResale = resale,
                    });
                }
            }
            return Tuple.Create(seats, meta);
        }

        // ---- Marketplace --------------------------------------------------------------------

        /// <summary>/axsmarketplace/offers -> (listings, meta). Prices are DOLLARS here - converted
        /// to cents (Python round() = banker's rounding, hence ToEven).</summary>
        public static Tuple<List<Listing>, JsonElement> ParseMarketplaceOffers(JsonElement offersJson)
        {
            var meta = Obj(Get(offersJson, "meta"));
            var result = new List<Listing>();
            foreach (var l in Arr(Get(offersJson, "listings")))
            {
                var sec = Obj(Get(l, "section"));
                var pb = Obj(Get(l, "priceBreakdown"));
                var price = Truthy(Get(l, "price")) ? Get(l, "price").GetDouble() : 0.0;
                result.Add(new Listing
                {
                    OfferId = Or(Get(l, "id")),
                    Section = Or(Get(sec, "name")),
                    Row = Or(Get(l, "row")),
                    SeatingType = "Marketplace",
                    PriceCents = (long)Math.Round(price * 100, MidpointRounding.ToEven),
                    TotalPrice = Has(l, "allInPrice") ? Dec(Get(l, "allInPrice")) : Dec(Get(pb, "total")),
                    FeePerTicket = Dec(Get(pb, "serviceFee")),
                    SplitQuantities = Arr(Get(l, "splits")).Select(q => (int)q.GetDouble()).ToList(),
                    IsResale = true,
                    Zone = Or(Get(sec, "longSectionName"), Get(sec, "name")),
                    QuantityOverride = Truthy(Get(l, "quantity")) ? (int)Get(l, "quantity").GetDouble() : 0,
                    StockType = Or(Get(l, "stockType")),
                    InHandDate = Or(Get(l, "inHandDate")),
                    Notes = Or(Get(l, "notes")),
                    SeatFeatures = Arr(Get(l, "seatFeatures")).Where(f => f.ValueKind == JsonValueKind.String && f.GetString() != "").Select(f => f.GetString()).ToList(),
                    MaxQuantity = IntOrNull(Get(meta, "maxTicketCount")),
                    FaceValue = Dec(Get(l, "faceValue")),
                });
            }
            return Tuple.Create(result, meta);
        }

        /// <summary>inventory/V2/{token}/sections is a dict sectionLabel -> section.</summary>
        public static int SectionCount(JsonElement sectionsJson)
        {
            return sectionsJson.ValueKind == JsonValueKind.Object ? sectionsJson.EnumerateObject().Count() : 0;
        }
    }
}
