# Ghi chú: discovery AXS (đo thật ngày 2026-09-26)

## Vì sao không đi theo sitemap như Broadway

- AXS có sitemap công khai: `www.static.discovery-prod.axs.com/uploads/sitemaps/sitemap_{event,event2,event3,event4,venue,performer,series}.xml`. Tải bằng curl được, không có Cloudflare.
- Riêng sitemap event đã có khoảng 170 nghìn URL, gồm cả event đã qua. Sitemap không có ngày.
- Muốn biết event nào sắp diễn ra thì phải mở từng event (`_next/data`). Tốc độ đo được khoảng 0,9 event/giây với 2 luồng, tức khoảng 50 giờ cho một lượt.
- Venue page (`/venues/{id}/{slug}`, 9.501 venue trong `/venues`) có sẵn danh sách event sắp diễn ra kèm `totalEvents`. Đây là phương án dự phòng, nhưng cần ít nhất 9.501 request.

## API tìm kiếm của AXS (đang dùng)

Trang search và category của axs.com tự gọi API này:

```
GET https://unifiedapisearch.discovery-prod.axs.com/v1/Discovery/Events
    ?page=N&results=100&sortOrder=Date&sortDirection=Asc
    &filterCriteria=Date&includeAdditionalDateFilter=false
    &dateStart=2026-09-26T00:00:00&dateEnd=2026-09-27T00:00:00
```

| Đo được | Kết quả |
|---|---|
| Gọi bằng curl | 403 (trang chặn của AXS) |
| `fetch()` trong trang www.axs.com | 200. API cho phép CORS từ www.axs.com, giống cách site tự gọi |
| `results` | 1..100. Gửi 500 thì nhận 400 "must be between 1 and 100" |
| Giới hạn 10.000 | `total` dừng đúng ở 10000. Trang vượt quá 100 × 100 trả 500 (giới hạn cửa sổ kết quả của Elasticsearch) |
| Không có `filterCriteria=Date` | ngày bị bỏ qua, `total` luôn 10000 |
| Không gửi tham số geo | trả mọi site AXS (US, CA, GB, SE, JP, AU…) |
| Số event mỗi ngày | 340–2.436 (thấp nhất 28/9, cao nhất 10/10) |
| Mỗi trang 100 event | khoảng 750 KB JSON (có mô tả HTML, bản dịch, ảnh) |

Vì mỗi ngày có ít hơn 10.000 event, crawler đi từng ngày. Ngày nào vẫn chạm trần thì tự chia đôi.

Một kết quả có đủ những gì cần: `eventId`, `eventURL`, `eventDatetimeLocal`, `eventDatetimeUtc` (không có `Z`, crawler tự thêm), `eventTimezone`, `venue{venueId,venueTitle,address{city,stateProvince,countryCode}}`, `performerIds`, `axsTicketed`, `axsMarketplaceEvent`, `ticketingStatusText`.

## Cloudflare và phiên trình duyệt

- Chromium kèm theo patchright bị kẹt ở Turnstile, nên phải dùng Chrome thật (`channel: "chrome"`). Giống fetcher Python.
- Sau khi mở `www.axs.com/venues` và qua challenge, crawler chuyển sang `www.axs.com/robots.txt` rồi mới gọi API. Trang `/venues` thỉnh thoảng tự tải lại, làm `page.evaluate` báo "Execution context was destroyed" (gặp ở cả lần chạy 3 ngày và lần chạy concurrency 10). `robots.txt` không có script nên không bao giờ tự tải lại.
- Ngày 2026-09-26, IP máy bị Cloudflare chặn ở www.axs.com (sau các lượt test tồn kho). Qua proxy wiredproxies thì qua được, thường ở session thứ 1 hoặc 2.
- Nếu API từ chối (0/403/429) ở mọi lần retry, crawler mở session proxy mới và làm lại đúng ngày đó, tối đa 5 session.

## Số đo thật

| Lần chạy | Kết quả |
|---|---|
| 3 ngày (26–28/9), proxy, concurrency 4 | 3.510/3.510 hit, 3.481 event sắp diễn ra, 37 request, 81s (tính cả warm-up) |
| 3 ngày (10–12/11), proxy, concurrency 10 | 2.753/2.753 hit, 29 request, 267s (chạy song song với lượt 365 ngày trên cùng proxy) |
| 365 ngày, proxy, concurrency 4 (bản cũ, còn fetch từ `/venues`) | chạy 125 ngày (26/9/2026–28/1/2027), 110.670 event duy nhất, rồi dừng vì Chrome bị đóng ("Target page, context or browser has been closed"). Không có CSV vì file chỉ ghi khi chạy xong. Đã sửa: trình duyệt bị đóng thì mở session mới và làm lại đúng ngày đó |

Trong 3 ngày đầu:
- Theo quốc gia: US 3.254, CA 157, GB 44, SE 11, JP 6.
- Theo cách bán: 321 bán vé chính trên AXS (Veritix), 3.119 chỉ có Marketplace, 41 AXS chỉ liệt kê chứ không bán.

## Kiểm tra với fetcher bước 2

URL `https://www.axs.com/events/1156220/blue-man-group-tickets` lấy từ CSV được đưa vào `python3 -m axs.cli event --url …`. Event page parse ra đúng id, tên, `2026-09-28T00:00:00Z` và Luxor Hotel & Casino, khớp dòng CSV. Bước lấy vé bị chặn do chất lượng proxy (lỗi đã biết), không liên quan tới discovery.
