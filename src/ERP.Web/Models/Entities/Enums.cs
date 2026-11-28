using System.ComponentModel.DataAnnotations;

namespace ERP.Web.Models.Entities;

public enum AccountType
{
    [Display(Name = "Tài sản")] Asset = 1,
    [Display(Name = "Nợ phải trả")] Liability = 2,
    [Display(Name = "Vốn chủ sở hữu")] Equity = 3,
    [Display(Name = "Doanh thu / thu nhập")] Revenue = 4,
    [Display(Name = "Chi phí")] Expense = 5,
    [Display(Name = "Xác định KQKD")] Result = 6
}

public enum JournalSource
{
    [Display(Name = "Chứng từ kế toán")] Manual = 1,
    [Display(Name = "Số dư đầu kỳ")] Opening = 2,
    [Display(Name = "Hóa đơn bán")] SalesInvoice = 3,
    [Display(Name = "Hóa đơn mua")] PurchaseInvoice = 4,
    [Display(Name = "Phiếu thu / chi")] Cash = 5,
    [Display(Name = "Kho")] Inventory = 6,
    [Display(Name = "Khấu hao TSCĐ")] Depreciation = 7,
    [Display(Name = "Tài sản cố định")] FixedAsset = 8,
    [Display(Name = "Sản xuất")] Production = 9,
    [Display(Name = "Kết chuyển cuối kỳ")] Closing = 10,
    [Display(Name = "Tiền lương")] Payroll = 11,
    [Display(Name = "Chênh lệch tỷ giá")] Fx = 12,
    [Display(Name = "Phân bổ / định kỳ")] Allocation = 13,
    [Display(Name = "Thuế")] Tax = 14,
    [Display(Name = "Bù trừ công nợ")] Offset = 15
}

public enum DocStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã ghi sổ")] Posted = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public enum ItemType
{
    [Display(Name = "Nguyên vật liệu")] Material = 1,
    [Display(Name = "Thành phẩm")] FinishedGood = 2,
    [Display(Name = "Hàng hóa")] Merchandise = 3,
    [Display(Name = "Dịch vụ")] Service = 4,
    [Display(Name = "Bán thành phẩm")] SemiFinished = 5
}

public enum InvDocType
{
    [Display(Name = "Nhập kho")] Receipt = 1,
    [Display(Name = "Xuất kho")] Issue = 2,
    [Display(Name = "Chuyển kho")] Transfer = 3,
    [Display(Name = "Kiểm kê điều chỉnh")] Adjustment = 4
}

public enum DocOrigin
{
    [Display(Name = "Thủ công")] Manual = 1,
    [Display(Name = "Mua hàng")] Purchase = 2,
    [Display(Name = "Bán hàng")] Sales = 3,
    [Display(Name = "Sản xuất")] Production = 4,
    [Display(Name = "Quản lý chất lượng")] Quality = 5,
    [Display(Name = "Hàng bán bị trả lại")] SalesReturn = 6,
    [Display(Name = "Gia công ngoài")] Outsource = 7,
    [Display(Name = "Bảo dưỡng thiết bị")] Maintenance = 8
}

public enum PoStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt")] PendingApproval = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Nhận một phần")] PartiallyReceived = 4,
    [Display(Name = "Đã nhận đủ")] Received = 5,
    [Display(Name = "Hoàn tất")] Completed = 6,
    [Display(Name = "Đã hủy")] Cancelled = 7
}

public enum SoStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã xác nhận")] Confirmed = 2,
    [Display(Name = "Giao một phần")] PartiallyDelivered = 3,
    [Display(Name = "Đã giao đủ")] Delivered = 4,
    [Display(Name = "Hoàn tất")] Completed = 5,
    [Display(Name = "Đã hủy")] Cancelled = 6
}

public enum InvoiceDirection
{
    [Display(Name = "Hóa đơn bán ra")] Sales = 1,
    [Display(Name = "Hóa đơn mua vào")] Purchase = 2
}

public enum InvoiceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã phát hành / ghi sổ")] Issued = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public enum CashAccountType
{
    [Display(Name = "Tiền mặt")] Cash = 1,
    [Display(Name = "Tiền gửi ngân hàng")] Bank = 2
}

public enum CashVoucherType
{
    [Display(Name = "Phiếu thu")] Receipt = 1,
    [Display(Name = "Phiếu chi")] Payment = 2,
    [Display(Name = "Chuyển tiền nội bộ")] Transfer = 3
}

public enum CashPurpose
{
    [Display(Name = "Thu tiền khách hàng")] CustomerReceipt = 1,
    [Display(Name = "Trả tiền nhà cung cấp")] VendorPayment = 2,
    [Display(Name = "Thu / chi khác")] Other = 3,
    [Display(Name = "Chuyển nội bộ")] Internal = 4
}

public enum AssetStatus
{
    [Display(Name = "Đang sử dụng")] InUse = 1,
    [Display(Name = "Đã khấu hao hết")] FullyDepreciated = 2,
    [Display(Name = "Đã thanh lý")] Disposed = 3
}

public enum ProductionStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã phát lệnh")] Released = 2,
    [Display(Name = "Đang sản xuất")] InProgress = 3,
    [Display(Name = "Hoàn thành")] Completed = 4,
    [Display(Name = "Đã hủy")] Cancelled = 5,
    [Display(Name = "Chờ duyệt")] PendingApproval = 6,
    [Display(Name = "Đã duyệt")] Approved = 7,
    [Display(Name = "Đã đóng lệnh")] Closed = 8
}

/// <summary>Các loại danh mục dùng chung (bảng CatalogItems)</summary>
public enum CatalogType
{
    [Display(Name = "Đơn vị tính")] Unit = 1,
    [Display(Name = "Phòng ban / bộ phận")] Department = 2
}

// ===================================================================== QUẢN LÝ CHẤT LƯỢNG
public enum QcStage
{
    [Display(Name = "Đầu vào (nhận hàng mua)")] Incoming = 1,
    [Display(Name = "Trong quá trình sản xuất")] InProcess = 2,
    [Display(Name = "Thành phẩm (hoàn thành SX)")] Final = 3,
    [Display(Name = "Trước khi giao hàng")] Outgoing = 4
}

public enum QcCriterionType
{
    [Display(Name = "Đạt / Không đạt")] PassFail = 1,
    [Display(Name = "Đo lường (min – max)")] Measure = 2
}

public enum QcResult
{
    [Display(Name = "Chưa kết luận")] Pending = 0,
    [Display(Name = "Đạt")] Pass = 1,
    [Display(Name = "Đạt có điều kiện")] Conditional = 2,
    [Display(Name = "Không đạt")] Fail = 3
}

public enum QcStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã kết luận")] Completed = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public enum NcrDisposition
{
    [Display(Name = "Chưa quyết định")] Pending = 0,
    [Display(Name = "Trả lại nhà cung cấp")] ReturnToVendor = 1,
    [Display(Name = "Sửa chữa / làm lại")] Rework = 2,
    [Display(Name = "Hủy bỏ (xuất hủy)")] Scrap = 3,
    [Display(Name = "Chấp nhận nhân nhượng")] AcceptAsIs = 4
}

public enum NcrStatus
{
    [Display(Name = "Đang xử lý")] Open = 1,
    [Display(Name = "Đã đóng")] Closed = 2
}

/// <summary>Loại hóa đơn điều chỉnh giảm bán hàng</summary>
public enum CreditNoteKind
{
    [Display(Name = "Hàng bán bị trả lại")] Return = 1,
    [Display(Name = "Giảm giá hàng bán")] PriceReduction = 2
}
