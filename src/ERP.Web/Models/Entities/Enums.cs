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
    [Display(Name = "Kết chuyển cuối kỳ")] Closing = 10
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
    [Display(Name = "Dịch vụ")] Service = 4
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
    [Display(Name = "Sản xuất")] Production = 4
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
    [Display(Name = "Đã hủy")] Cancelled = 5
}
