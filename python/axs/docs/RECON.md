# AXS — Recon (2026-09-24)

Sự kiện mẫu: https://www.axs.com/events/1457730/tom-jones-21-event-tickets
(Tom Jones, The HALL at Live!, Hanover MD, 2026-10-04 19:00 EDT)

Công cụ: `curl`, và Python `patchright` với `headless=False` (máy macOS, IP VN, không proxy).
Extension Claude in Chrome không kết nối được nên toàn bộ recon chạy bằng trình duyệt tự động hoá.
Vì vậy kết quả dưới đây là thật cho bot, không phải chỉ cho Chrome của người dùng.

## 1. Kết quả curl (không trình duyệt)

| URL | Kết quả |
|---|---|
| `www.axs.com/events/...` | **403**, `server: cloudflare`, `cf-mitigated: challenge`, title "AXS Access Info" |
| `shop.axs.com`, `tix.axs.com` | 403 challenge |
| `unifiedapicommerce-us.axs.com/veritix/...` | 403 |
| `api.axs.com/` | 404 JSON (không challenge, nhưng không dùng tới) |
| `www.axs.com/robots.txt` | 200. `Crawl-delay: 2`. Chỉ disallow `/users`, `/me`, `/search`, `/contributor`, `/*referrer=*` |
| Sitemap `www.static.discovery-prod.axs.com/uploads/sitemaps/sitemap_event{,2,3,4}.xml` | **200 qua curl, không challenge**. Tổng ~169.6k URL sự kiện, gồm cả sự kiện cũ |

## 2. Anti-bot

Có 3 lớp:

1. **Cloudflare Managed Challenge + Turnstile** trên mọi host `*.axs.com`.
   - Chromium đi kèm patchright (`p.chromium.launch(headless=False)`): **bị kẹt ở ô Turnstile "Verify you are human"**,
     chờ 40 giây không tự qua. Không bấm hộ.
   - **Chrome thật cài trên máy (`p.chromium.launch(headless=False, channel="chrome")`)**: tự qua sau 3–6 giây,
     không cần tương tác. Nhận `cf_clearance` cho `.axs.com`, `.shop.axs.com`, `.tix.axs.com` (TTL 1 năm, gắn IP/fingerprint).
     Lặp lại 6 lần, lần nào cũng qua.
   - Kết luận: **bắt buộc `channel="chrome"`**. Đây là điểm khác với Broadway: ở đó Chromium bundled vẫn qua được.
2. **Queue-it** trên `shop.axs.com`: cookie `Queue-it-axs_____...<e>` (TTL 24h) và `Queue-it-visitorsession`.
   Lúc khảo sát không có hàng đợi, nên bị redirect thẳng. Khi sự kiện hot mở bán thì có thể phải chờ phòng đợi.
3. **Session + rate-limit của API commerce** (`unifiedapicommerce-us.axs.com`):
   - Cookie `axs_ecomm` (httpOnly, **TTL 25 phút**) do app tạo qua `POST /veritix/session/v2/...`.
     Gọi API trước khi có cookie này thì trả **440 "Session Expired"**.
   - Giới hạn tốc độ chặt, xem mục 4.

Không thấy DataDome, PerimeterX, Akamai hay Kasada.

## 3. Luồng và endpoint

```
www.axs.com/events/{eventId}/{slug}        (Next.js; __NEXT_DATA__ chứa toàn bộ event)
  └─ ticketing.url = https://shop.axs.com/?c=axs&e={veritixEventCode}
       └─ 302 → tix.axs.com/{TOKEN}?...&c=axs&e=...      (SPA "FanSight", tải ~30–47s)
            └─ unifiedapicommerce-us.axs.com/veritix/...  (fetch từ trang tix, credentials: include)
```

`{TOKEN}` (vd `G7gZEgAAAAB55nquAAAAAABa%2Fv%2F...`) **giữ nguyên** giữa các lần chạy với cùng một event,
vì nó mã hoá `offerID` (11434726 xuất hiện trong token và trong `e=30367541911434726`).
Chưa kiểm chứng cách tự dựng token. Hiện tại lấy token bằng cách theo redirect của `shop.axs.com`.

### 3a. Dữ liệu event (không cần qua shop)

| Method | URL | Ghi chú |
|---|---|---|
| GET | `https://www.axs.com/events/{id}/{slug}` → `__NEXT_DATA__` | ~80KB, `props.pageProps.discoveryEventData` |
| GET | `https://www.axs.com/_next/data/{buildId}/en/events/{id}/{slug}.json?eventId={id}&slug={slug}` | ~52KB JSON, gọi bằng `fetch()` trong trang www. `buildId` lấy từ `__NEXT_DATA__.buildId` và **đổi mỗi lần AXS deploy** |

Các field chính của `discoveryEventData`: `eventId`, `eventDatetime` (local, không có offset), `eventDatetimeUTC`
(không có `Z`), `eventDatetimeISO` (có offset), `eventDatetimeTz`, `doorDatetimeUTC`, `onsaleDatetimeUTC`,
`venue{venueId,title,timeZone,mappings[].veritixVenueId}`, `associations.headliners[].performerId`,
`ticketing{statusId,status,url,onsaleUrlLookupId}`, `publishStatus`.

**Chú ý:** `ticketingEventData.url` và `discoveryEventData.ticketing.url` bị `null` ở cả 25/25 event mới thử qua `_next/data`,
trong khi event mẫu 1457730 lại có URL. Có thể link vé chỉ render ở server (`__NEXT_DATA__` của trang HTML),
cũng có thể do các event này chưa gắn Veritix. **Cần kiểm tra thêm** trước khi chọn `_next/data` hay trang HTML.

### 3b. Giá và ghế (phải có session trên tix.axs.com)

Base: `https://unifiedapicommerce-us.axs.com/veritix`

| # | Method | Path | Nội dung |
|---|---|---|---|
| 1 | GET | `/pre-flow/v2/{TOKEN}/phase?reservation=false&...` | 65KB: `offerID`, `contextID`, `onSaleStatus{onSaleNow,onSaleDate,offSaleDate}`, `es5Flow`, `mmcMapID`, `queueIt` |
| 2 | POST | `/session/v2/{TOKEN}?reservation=false&...` body `{"locale":"en-US",...}` | tạo `axs_ecomm`, `sessionExpireAt` (epoch ms) |
| 3 | GET | `/start-flow/v1/{TOKEN}?...` | 1.2MB (config và map) |
| 4 | GET | `/inventory/v4/{TOKEN}/price?flow=pick_a_seat_2d&excludeResaleTaxes=false&includeDynamicPrice=true&includeSoldOuts=false&locale=en-US` | **bảng giá** theo offer, xem bên dưới |
| 5 | GET | `/inventory/V2/{TOKEN}/sections?flow=pick_a_seat_2d&q=0000...` | dict `sectionLabel → {sectionID, availability.isSoldOut, prices[{offerID,priceLevelID,priceTypeID}], neighborhoodDescription, connectionFee}` |
| 6 | POST | `/inventory/V2/{TOKEN}/offer/search?flow=pick_a_seat_2d&q=0000...` body `{"category":"SEAT","eventID":"3463","locale":"en-US","priceLevels":["5864","5865"],"sectionID":null,"sectionLabel":null}` | **từng ghế**: `offers[].items[]{id, sectionID, sectionLabel, rowID, rowLabel, number, statusCodeLabel, seatType, priceLevelID, offerID}` |

Không thấy header bắt buộc nào ngoài cookie (không có `authorization` hay `x-*`).
`flow` lấy từ `es5Flow` viết thường. Event GA hoặc best-available có thể dùng flow khác (chưa gặp).

Mẫu `price` đã rút gọn:
```json
{"offerPrices":[
  {"offerID":"11434726","offerName":"Tom Jones (21+ Event)","offerType":"Single","min":1,"max":8,
   "zonePrices":[{"eventID":"3463","priceLevels":[
     {"label":"P5","priceLevelID":"5864","availability":{"amount":8},
      "prices":[{"base":7500,"priceTypeID":"45478"}]}],
     "priceTypes":[{"priceTypeID":"45478","label":"Regular","pricingMode":"Dynamic"}]}]},
  {"offerID":"9000000178233956","offerName":null,
   "zonePrices":[{"priceLevels":[{"priceLevelID":null,"availability":{"amount":2},"prices":[{"base":47430}]}]}]}
 ],
 "fees":[{"id":"9915002","name":"Service Fees","applicationMethod":"PerItem","components":[{"calculationMethod":"Percentage","rate":0.31}]}],
 "currency":"USD"}
```
- Giá tính bằng **cent** (`7500` = $75). Trang hiển thị "Price Range: $75 - $596".
- Offer chính (primary) có ID 8 chữ số (`1143xxxx`). **Vé bán lại** có `offerID` bắt đầu bằng `9000000…` và không có tên hay priceLevel.
  Event mẫu có 7 offer chính và 17 offer bán lại.
- `offer/search` cho event mẫu: 20 offer, **756 ghế**, trạng thái Open 528, Aisle 189, ADA 4, null 35.

## 4. Bảng kiểm tra

| Hạng mục | Kết quả |
|---|---|
| ID | Event: `eventId` (1457730, ổn định, nằm trong URL). Veritix: `e=30367541911434726`, `offerID` 11434726, `eventID` nội bộ `"3463"`, `contextID` 303675419. Ghế: `items[].id` + `sectionID/rowID/number`. Listing bán lại: `offerID 9000000…`. Chưa kiểm tra ID ghế có ổn định qua nhiều ngày không |
| Thời gian | **`eventDatetimeUTC` đúng UTC nhưng thiếu `Z`**, phải thêm `Z` trước khi parse. `eventDatetime` là giờ local. `eventDatetimeISO` có offset. Đã kiểm tra DST với 25 event: 2027-01-23 NY −05 → UTC +5h, 2027-04-10 NY −04 → +4h, 2026-11-29 Chicago −06, 2026-10-25 LA −07, 2027-02-19 LA −08, đều khớp. Phoenix (không có DST) −07 cũng khớp. `onSaleStatus.onSaleDate` có dạng `"2026-05-29 14:00:00 +0000"` |
| Phân trang | Event data: 1 response. Ghế: `offer/search` trả tất cả trong 1 response (756 ghế, 271KB) khi `sectionID:null`. Chưa thử với venue lớn (arena 15–20k ghế), có thể bị cắt |
| Đủ dữ liệu | Không có field `total`. Có thể đối chiếu bằng tổng `priceLevels[].availability.amount` ở `price` với số ghế ở `offer/search` |
| Phạm vi | Sitemap có ~170k event gồm cả sự kiện cũ, **không có ngày**, phải lọc bằng `_next/data`. Hết giờ bán thì `shop.axs.com` redirect qua `afterevent.aspx` (kể cả event mẫu vẫn đang bán, `rt=AfterEvent` chỉ là tên bước). Chưa thử event đã huỷ hoặc hết vé |
| Tốc độ | Trên 1 IP và 1 session: `sections` chịu được 5 và 10 request đồng thời (200). **25 request đồng thời thì 25/25 bị 429**, sau đó mọi request đều 429 bằng trang HTML Cloudflare. Gọi lại `price` ngay sau app thì 429. Gọi lại `offer/search` thì **403 bằng HTML Cloudflare (WAF)**, dù body giống hệt. Một lần chạy khác không tải được `sections` trong 90 giây, sau vài phút thì hết. `robots.txt` đặt Crawl-delay 2s |
| Phiên | `cf_clearance` TTL 1 năm nhưng gắn IP/fingerprint. `__cf_bm` 30 phút. `axs_ecomm` 25 phút. Queue-it 24h. Đổi IP phải warm lại (qua CF, rồi qua shop để tạo session) |

## 5. Rủi ro

1. **Rate-limit và WAF trên API commerce rất chặt.** Replay `offer/search` bị 403 ngay lần đầu. Có thể CF giới hạn theo
   endpoint+IP (app vừa gọi 1 lần trước đó), cũng có thể cần một header hay chuỗi request mà mình chưa thấy.
   **Đây là rủi ro lớn nhất và phải giải quyết ở giai đoạn prototype.** Hướng an toàn: không tự gọi lại,
   mà **để app tự gọi rồi bắt response** (`page.on("response")`), mỗi event một lần tải trang tix.
2. Mỗi event tốn ~30–47 giây để app tải xong (redirect, CF, Queue-it, phase, session, start-flow 1.2MB, price, offer/search).
   Với hàng nghìn event, cần nhiều trình duyệt hoặc proxy song song.
3. Queue-it khi mở bán hot: có thể phải chờ vài phút đến vài giờ.
4. `channel="chrome"` yêu cầu Chrome cài trên máy chạy bot. Chromium bundled bị Turnstile tương tác.
5. `buildId` của `_next/data` đổi sau mỗi lần deploy, nên phải đọc lại từ `__NEXT_DATA__`.
6. `ticketing.url` bị `null` trong `_next/data` ở một số event (xem 3a).

## 6. Đề xuất kiến trúc

Theo `crawler-workflow`, giai đoạn 2 viết prototype bằng Python và patchright ở `python/axs/`:

- `launch(headless=False, channel="chrome")`, mỗi proxy session một context (`{SESSIONID}`).
- **Discovery:** tải sitemap bằng `requests` (không challenge), lấy danh sách `eventId/slug`. Rồi trong một trang `www.axs.com`
  đã qua CF, gọi `fetch('/_next/data/{buildId}/...')` để lấy ngày UTC, trạng thái và `ticketing.url`, lọc giữ event sắp diễn ra.
  Tôn trọng Crawl-delay: tuần tự hoặc ≤2 request đồng thời.
- **Giá và ghế:** mở `ticketing.url` → tix, **bắt response** `price`, `sections`, `offer/search` do app tự gọi,
  không replay. Nếu cần gọi lại (refresh) thì cách nhau ≥30s, khớp `availFreq = 30` trong `phase`.
- Khi gặp 429, 403 HTML hoặc 440: đóng context, đổi proxy session, warm lại (theo mẫu retry của `paciolanevenue/client.py`).
- Chuẩn hoá: `utc = eventDatetimeUTC + "Z"`, giá chia 100, `is_resale = offerID.startswith("9")`.

## 7. Quyết định của user (2026-09-24)

| Câu hỏi | Trả lời |
|---|---|
| Phạm vi | **Toàn bộ AXS**. Discovery lấy từ sitemap (~170k URL), lọc event sắp diễn ra qua `_next/data` |
| Dữ liệu | **Lấy tất cả**: metadata event, bảng giá (`price`), section (`sections`), từng ghế (`offer/search`), vé bán lại, phí, kèm raw JSON |
| Proxy | **Tuỳ chọn**: truyền `--proxy` thì dùng (`{SESSIONID}` đổi mỗi lần retry), không truyền thì chạy IP máy |
| Nền tảng | **Python demo** → **.NET 8 trên macOS** (Microsoft.Playwright, mẫu `PaciolanEvenue.Fetch.Playwright`) → **.NET trên Windows** (Rowing, `DataSourceType` mới = 9) |
| Quyền crawl | Được phép |

Việc tiếp theo là giai đoạn 2 (prototype Python ở `python/axs/`). Cần kiểm chứng sớm:
(a) cách bắt response ổn định cho nhiều event liên tiếp trong cùng một context;
(b) `ticketing.url` bị `null` trong `_next/data`;
(c) venue lớn có bị cắt ở `offer/search` không;
(d) ở bước 4a, `Microsoft.Playwright` với `Channel="chrome"` có qua được Turnstile không.
