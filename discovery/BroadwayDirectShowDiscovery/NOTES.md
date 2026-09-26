# Ghi chú: Cơ chế pass Cloudflare "verify bot"

## Vấn đề gốc

Cả `broadwaydirect.com` và `tickets.broadwaydirect.com` đều có Cloudflare
Managed Challenge (trang "Just a moment..."):

- Client HTTP thường (requests/axios/fetch trần) → luôn bị chặn 403.
- Chromium chạy `headless: true` → Cloudflare phát hiện và chặn luôn, dù
  dùng patchright.

## Cách vượt qua

1. Dùng **patchright** (bản Playwright đã vá dấu vân tay chống phát hiện
   automation) mở **Chromium thật, `headless: false`** (cửa sổ hiện ra
   thật, không ẩn).
2. Navigate vào trang → Cloudflare tự chạy JS challenge trong vài giây →
   poll `page.title()` mỗi 300ms cho tới khi tiêu đề không còn "Just a
   moment..." nữa (thay vì sleep cứng, tiết kiệm thời gian).
3. Sau khi qua, trình duyệt được cấp cookie `cf_clearance`.

**Phát hiện quan trọng nhất** (đã đo, không đoán): cookie `cf_clearance`
này là **domain-wide** — chỉ cần vượt challenge **1 lần duy nhất** cho mỗi
domain (`broadwaydirect.com` và `tickets.broadwaydirect.com`), sau đó gọi
API cho bất kỳ show/series nào khác cũng đều pass, không cần navigate lại
từng trang.

## Cách kéo data (2 giai đoạn)

### Giai đoạn 1 — tìm danh sách show (trên `broadwaydirect.com`)

- Pass Cloudflare 1 lần
- Fetch `show-sitemap.xml` (danh sách toàn bộ trang `/show/{slug}/`)
- Fetch hàng loạt các trang show đó **song song** (`Promise.all` chạy
  ngay trong page, không phải request riêng từng cái) → lọc ra show nào
  có link `tickets.broadwaydirect.com/.../series/{id}`

### Giai đoạn 2 — kéo toàn bộ suất diễn (trên `tickets.broadwaydirect.com`)

- Pass Cloudflare 1 lần duy nhất (bootstrap bằng 1 trang series bất kỳ)
- Gộp tất cả `(series_id, tháng)` thành 1 danh sách URL, bắn hết cùng lúc
  qua API `getbymonth`, concurrency 25 request/đợt

→ Không có bước nào phải "chờ" Cloudflare quá 2 lần cho cả crawl, bất kể
crawl bao nhiêu show.

## Thời gian thực tế

| Bước                                                           | Thời gian                                  |
| -------------------------------------------------------------- | ------------------------------------------ |
| Pass Cloudflare lần 1 (broadwaydirect.com)                     | ~4-8s                                      |
| Fetch + lọc sitemap (66 trang show, mặc định `--days-back 75`) | ~2-3s                                      |
| Pass Cloudflare lần 2 (tickets.broadwaydirect.com)             | ~4-8s                                      |
| Fetch toàn bộ API (36 show × 6 tháng = 216 request)            | ~9-15s                                     |
| **Tổng cộng**                                                  | **~19-21 giây** (Python 20.9s, Node 19.5s) |

Nếu dùng `--all-shows` (quét cả 538 trang thay vì 66), giai đoạn 1 sẽ lâu
hơn đáng kể (~2-4 phút), nhưng giai đoạn 2 vẫn nhanh như cũ vì không phụ
thuộc số trang đã quét ở bước 1.

## Rate limit / không cần proxy

Test có kiểm soát, từ 1 IP duy nhất:

- Burst 200 request đồng thời, 745 request/2 phút: **0 lần bị chặn**.
- 36 lần pass Cloudflare liên tiếp không nghỉ: **0 lần thất bại**.
