# ERP Suite — ASP.NET Core 8 MVC + SQL Server

Hệ thống quản lý nguồn lực doanh nghiệp (ERP) cơ bản cho doanh nghiệp vừa và nhỏ **sản xuất + thương mại**, hạch toán theo Thông tư 200/2014/TT-BTC (rút gọn). Gồm 10 phân hệ, mọi nghiệp vụ tự sinh bút toán vào sổ cái nên kho, công nợ và báo cáo tài chính luôn khớp nhau.

```
Mua hàng ──► Nhận hàng (Kho: Nợ 15x / Có 331) ──► Hóa đơn mua (Nợ 1331 / Có 331) ──► Phiếu chi (Nợ 331 / Có 112)
Sản xuất ──► Xuất NVL (Nợ 154 / Có 152) ──► Kết chuyển NC, SXC (Nợ 154 / Có 622, 627) ──► Nhập thành phẩm (Nợ 155 / Có 154)
Bán hàng ──► Giao hàng (Kho: Nợ 632 / Có 155, 1561) ──► Hóa đơn bán (Nợ 131 / Có 511, 33311) ──► Phiếu thu (Nợ 112 / Có 131)
Tài sản  ──► Ghi tăng (Nợ 211 / Có 331) ──► Khấu hao hằng tháng (Nợ 642, 641 / Có 2141) ──► Thanh lý (Nợ 2141, 811 / Có 211)
Cuối kỳ  ──► Kết chuyển (5xx–8xx → 911 → 4212) ──► Khóa sổ ──► Bảng CĐ phát sinh, KQKD, CĐKT
```

## 1. Phân hệ & tính năng

| Phân hệ | Tính năng chính |
|---|---|
| **Kế toán tổng hợp** | Hệ thống tài khoản TT200 (thêm TK chi tiết, TK theo dõi công nợ); chứng từ kế toán thủ công (nháp → ghi sổ, kiểm tra cân Nợ/Có, bút toán đảo); xem mọi bút toán tự động; **Bảng cân đối số phát sinh**, **Sổ cái / sổ chi tiết TK** (kèm TK đối ứng), **Báo cáo KQKD (B02-DN)** so sánh cùng kỳ, **Bảng cân đối kế toán (B01-DN)**; kết chuyển cuối kỳ & khóa sổ theo tháng |
| **Kế toán phải thu** | Số dư công nợ theo khách hàng, phân tích tuổi nợ (chưa hạn / 1–30 / 31–60 / 61–90 / >90 ngày), sổ chi tiết công nợ, hạn mức công nợ khi bán |
| **Kế toán phải trả** | Số dư công nợ nhà cung cấp, tuổi nợ phải trả, sổ chi tiết công nợ |
| **Kế toán tài sản** | Sổ TSCĐ theo nhóm, ghi tăng (có / không hạch toán), khấu hao đường thẳng hằng tháng (chạy 1 lần/tháng), lịch sử khấu hao, thanh lý / nhượng bán |
| **Quản lý dòng tiền** | Quỹ tiền mặt & tài khoản ngân hàng, phiếu thu / phiếu chi / chuyển tiền nội bộ (in mẫu 01-TT, 02-TT, số tiền bằng chữ), tự cấn trừ công nợ theo hóa đơn (FIFO theo hạn), chặn chi vượt số dư, sổ quỹ / sổ tiền gửi, báo cáo dòng tiền theo tháng, **dự báo 8 tuần** từ công nợ đến hạn |
| **Quản lý hóa đơn** | Hóa đơn bán ra (tự cấp số theo ký hiệu khi phát hành) / mua vào (chặn trùng số HĐ NCC), hóa đơn dịch vụ trực tiếp, hủy hóa đơn (đảo bút toán, trả lại số lượng cho đơn hàng), in hóa đơn GTGT, **bảng kê thuế GTGT** đầu ra / đầu vào & số thuế phải nộp |
| **Quản lý kho** | Hàng hóa / NVL / thành phẩm / dịch vụ, nhiều kho, tồn kho theo kho + hàng đang về + đã cam kết + khả dụng, phiếu nhập / xuất / chuyển kho / **kiểm kê** (thừa 3381, thiếu 1381), **giá vốn bình quân gia quyền di động**, thẻ kho, cảnh báo tồn tối thiểu |
| **Quản lý mua hàng** | Nhà cung cấp, đơn mua → gửi duyệt (**từ ngưỡng 100 triệu phải Ban Giám đốc duyệt**, dưới ngưỡng tự duyệt) → nhận hàng nhiều đợt → lập hóa đơn mua từ số lượng đã nhận → thanh toán |
| **Quản lý bán hàng** | Khách hàng, đơn bán (giá, chiết khấu, VAT từng dòng) → xác nhận (**kiểm tra hạn mức công nợ**, Ban Giám đốc duyệt vượt) → giao hàng nhiều đợt → xuất hóa đơn → thu tiền |
| **Quản lý sản xuất** | Định mức NVL (BOM, % hao hụt, nhân công & SXC định mức, giá thành định mức, biên lãi gộp), lệnh sản xuất (phát lệnh → xuất NVL → hoàn thành, giá thành thực tế), **hoạch định vật tư (MRP)**: nhu cầu − tồn − đang mua → tạo đơn mua nháp một chạm |

**Kiểm soát dùng chung:** mọi thao tác ghi dữ liệu chạy trong một transaction (lỗi giữa chừng → rollback toàn bộ); lỗi nghiệp vụ hiển thị thành thông báo (không văng trang lỗi); kỳ đã khóa sổ chặn mọi chứng từ có ngày trong kỳ; tài khoản công nợ bắt buộc chọn đối tượng; chỉ TK chi tiết được hạch toán.

## 2. Phân quyền

| Vai trò | Quyền |
|---|---|
| Quản trị hệ thống | Toàn quyền, người dùng, danh mục kho |
| Ban Giám đốc | Xem toàn bộ (kể cả tài chính), duyệt đơn mua vượt ngưỡng, duyệt bán vượt hạn mức công nợ, mở khóa sổ |
| Kế toán | Kế toán tổng hợp, phải thu, phải trả, tiền, hóa đơn, TSCĐ, khóa sổ; lập hóa đơn từ đơn mua / bán; kiểm kê |
| Kinh doanh | Khách hàng, đơn bán, giao hàng |
| Mua hàng | Nhà cung cấp, đơn mua, nhận hàng, tạo đơn mua từ MRP |
| Thủ kho | Phiếu nhập / xuất / chuyển / kiểm kê, nhận hàng mua, giao hàng bán, xuất NVL sản xuất |
| Sản xuất | Định mức BOM, lệnh sản xuất, MRP |

Các vai trò không có quyền tài chính sẽ không thấy menu & số liệu kế toán.

## 3. Cấu trúc (MVC)

```
ERP.sln
src/ERP.Web/
├── Controllers/   Home, Accounts, Journals, Reports, Debts, Cash, Assets, Invoices,
│                  Items, Inventory, Partners, PurchaseOrders, SalesOrders, Boms, Production, Users, Account
├── Services/      AccountingService (ghi sổ, số dư, kiểm tra kỳ khóa)   InventoryService (tồn kho, giá vốn BQ, bút toán kho)
│                  TradeServices (Invoice/Purchase/Sales)   FinanceServices (Cash/Asset/Production/Period)
│                  ReportService (CĐPS, sổ cái, KQKD, CĐKT, tuổi nợ)   Core (số chứng từ, cấu hình, lỗi nghiệp vụ)
├── Models/Entities/  Account, FiscalPeriod, JournalEntry/Line, Partner, Item, Warehouse, StockLevel, StockMove,
│                     InventoryDoc/Line, PurchaseOrder/Line, SalesOrder/Line, Invoice/Line, CashAccount, CashVoucher,
│                     PaymentAllocation, AssetCategory, FixedAsset, DepreciationRun/Line, Bom/Line, ProductionOrder/Line
├── Data/          ErpDbContext, DbSeeder (dữ liệu mẫu chạy qua service thật)
├── Infrastructure/  định dạng tiền/ngày, đọc số bằng chữ, BusinessExceptionFilter, TransactionFilter
├── Views/         Razor + Bootstrap 5; _ItemLines dùng chung cho mọi chứng từ có dòng hàng
└── wwwroot/js/site.js   bộ soạn dòng chứng từ (thêm/xóa dòng, chọn hàng tự điền giá, tính tiền, cân Nợ/Có)
Dockerfile, docker-compose.yml (PA A), docker-compose.shared-db.yml (PA B), deploy-erp-shared-db.ps1, run-erp-B.cmd
```

Số chứng từ tự sinh theo tháng: `BT2611-0001` (bút toán), `PKT` (chứng từ KT), `PT`/`PC`/`CT` (thu/chi/chuyển), `PN`/`PX`/`CK`/`KK` (kho), `DMH` (đơn mua), `DBH` (đơn bán), `HDB`/`HDM` (hóa đơn), `LSX` (lệnh SX).

## 4. Chạy

**Visual Studio / dotnet:** sửa `ConnectionStrings:ErpConnection` trong `src/ERP.Web/appsettings.json` → `cd src/ERP.Web && dotnet run`. Lần đầu tự tạo database `ERP_DB` và dữ liệu mẫu.

**Docker – dùng chung SQL Server (giống HRM, CRM, E-Procurement, Quản lý dự án):** double-click `run-erp-B.cmd`. Script tự tìm SQL Server đang chạy (bỏ qua các container SQL riêng của các app kia), tạo login riêng `erp_app` (chỉ quyền tạo database), chọn port trống từ **8130**, build và chạy `erp-web`. Log ghi vào `deploy.log`.

**Docker – SQL Server riêng:** `copy .env.example .env` → `docker compose up -d --build` → `http://localhost:8130` (SQL: `localhost,1438`).

**Cấu hình nghiệp vụ** (`appsettings.json` → `Erp`): tên / MST / địa chỉ công ty in trên chứng từ, ký hiệu hóa đơn bán (`1C26TAA`), ngưỡng duyệt đơn mua (`100000000`).

### Dữ liệu mẫu

Công ty sản xuất & kinh doanh nội thất văn phòng, số dư đầu kỳ từ ngày 01 của 3 tháng trước, phát sinh liên tục đến hôm nay: 3 kho, 6 NVL, 3 thành phẩm (có BOM), 2 hàng hóa, 2 dịch vụ, 5 khách hàng, 5 nhà cung cấp, 5 TSCĐ, 7 đơn mua, 11 đơn bán, 7 lệnh sản xuất, lương, thuê nhà, lãi vay, khấu hao, 2 tháng đầu đã kết chuyển & khóa sổ.

| Tài khoản (mật khẩu `Abc@123`) | Họ tên | Vai trò |
|---|---|---|
| admin | Quản trị hệ thống | Quản trị |
| director | Trần Quốc Bảo | Ban Giám đốc |
| ketoan | Nguyễn Thị Mai | Kế toán |
| kinhdoanh | Lê Minh Tuấn | Kinh doanh |
| muahang | Phạm Thu Trang | Mua hàng |
| thukho | Võ Văn Nam | Thủ kho |
| sanxuat | Đặng Hữu Phước | Sản xuất |

**Kịch bản demo (15 phút):**
1. `kinhdoanh` → Đơn bán → mở đơn nháp của *Minh Phát* → Xác nhận → bị chặn do vượt hạn mức công nợ. `director` mở lại → Xác nhận (duyệt vượt).
2. `thukho` → Giao hàng → phiếu xuất kho, tồn kho giảm, giá vốn ghi Nợ 632.
3. `ketoan` → mở đơn → Xuất hóa đơn → hóa đơn được cấp số → Thu tiền (phiếu thu tự cấn trừ hóa đơn).
4. `muahang` → đơn mua chờ duyệt (> 100 triệu) → không có nút duyệt; `director` duyệt. Tạo đơn nhỏ → tự duyệt → `thukho` nhận hàng một phần → `ketoan` lập & ghi sổ hóa đơn mua → thanh toán.
5. `sanxuat` → Lệnh sản xuất đã phát lệnh → Xuất NVL → Hoàn thành (nhập kho thành phẩm, giá thành thực tế). Hoạch định vật tư: thấy *Bản lề* thiếu → `muahang` tạo đơn mua từ MRP.
6. `ketoan` → Trích khấu hao tháng → Bảng cân đối số phát sinh, KQKD, CĐKT (đều cân) → Kết chuyển & khóa sổ tháng trước → thử lập phiếu chi lùi ngày vào kỳ đã khóa → bị chặn.

## 5. Ghi chú kỹ thuật

- `EnsureCreated()` cho chạy thử; môi trường thật nên dùng EF Migrations (`dotnet ef migrations add InitialCreate`, đổi sang `MigrateAsync()`).
- Tắt dữ liệu mẫu: `Database__SeedOnStartup=false`.
- Mọi cột tiền / số lượng `decimal(18,2)`; tiền VND làm tròn đến đồng khi ghi sổ. Khóa ngoại `ON DELETE NO ACTION` — chứng từ không xóa sau khi ghi sổ, chỉ hủy (bút toán đảo).
- Mỗi quỹ / tài khoản ngân hàng gắn một TK chi tiết riêng (1111, 11211, 11212…) để số dư lấy trực tiếp từ sổ cái.
- Tên tách biệt với các app khác: project `erp`, container `erp-web` / `erp-sqlserver`, DB `ERP_DB`, login `erp_app`, port 8130 / 1438.

## 6. Hướng mở rộng

Đa tiền tệ & chênh lệch tỷ giá; kết nối hóa đơn điện tử (nhà cung cấp HĐĐT) và chữ ký số; báo cáo lưu chuyển tiền tệ B03-DN; giá thành theo phân bổ 627 cuối kỳ; đơn giá FIFO / đích danh; lô, hạn dùng, serial; định mức nhiều cấp & routing / công đoạn; ngân sách và kế hoạch dòng tiền; tích hợp HRM (bảng lương → 334, 622, 641, 642) và CRM (cơ hội → báo giá → đơn bán); phân quyền theo chi nhánh / kho.
