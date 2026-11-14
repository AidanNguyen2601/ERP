using System.ComponentModel.DataAnnotations;
using ERP.Web.Models.Entities;

namespace ERP.Web.ViewModels;

// ===================================================================== Tài khoản người dùng
public class LoginViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập tên đăng nhập")][Display(Name = "Tên đăng nhập")] public string Username { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu")][DataType(DataType.Password)][Display(Name = "Mật khẩu")] public string Password { get; set; } = string.Empty;
    [Display(Name = "Ghi nhớ đăng nhập")] public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu hiện tại")][DataType(DataType.Password)][Display(Name = "Mật khẩu hiện tại")] public string CurrentPassword { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới")][StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự")][DataType(DataType.Password)][Display(Name = "Mật khẩu mới")]
    public string NewPassword { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu mới")][Compare(nameof(NewPassword), ErrorMessage = "Xác nhận mật khẩu không khớp")][DataType(DataType.Password)][Display(Name = "Xác nhận mật khẩu mới")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class UserFormViewModel
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên đăng nhập"), StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9_.]+$", ErrorMessage = "Chỉ dùng chữ không dấu, số, dấu chấm, gạch dưới")]
    [Display(Name = "Tên đăng nhập")] public string Username { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập họ tên"), StringLength(150)][Display(Name = "Họ và tên")] public string FullName { get; set; } = string.Empty;
    [EmailAddress, StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [Required(ErrorMessage = "Nhập phòng ban"), StringLength(100)][Display(Name = "Phòng ban")] public string Department { get; set; } = string.Empty;
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự")][DataType(DataType.Password)][Display(Name = "Mật khẩu")] public string? Password { get; set; }
    [Required][Display(Name = "Vai trò")] public string Role { get; set; } = AppRoles.Sales;
    [Display(Name = "Kích hoạt")] public bool IsActive { get; set; } = true;
}

// ===================================================================== Dòng chứng từ dùng chung
public class LineInput
{
    public int Id { get; set; }
    public int? ItemId { get; set; }
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public int VatRate { get; set; }
    public decimal? CountedQty { get; set; }
    public decimal ScrapPct { get; set; }
    public int? AccountId { get; set; }
    public int? PartnerId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

/// <summary>Cấu hình bảng dòng hàng (_ItemLines)</summary>
public class LineEditorOptions
{
    public string PriceSource { get; set; } = "sale";   // sale | cost
    public bool ShowPrice { get; set; } = true;
    public bool ShowDiscount { get; set; }
    public bool ShowVat { get; set; } = true;
    public bool ShowCounted { get; set; }
    public bool ShowScrap { get; set; }
    public bool ShowAccount { get; set; }
    public bool AllowFreeText { get; set; }
    public string QtyLabel { get; set; } = "Số lượng";
    public string PriceLabel { get; set; } = "Đơn giá";
    public List<Item> Items { get; set; } = new();
    public List<Account> Accounts { get; set; } = new();
    public List<LineInput> Lines { get; set; } = new();
}

public class OrderForm
{
    public int Id { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Chọn đối tượng")][Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    [Required(ErrorMessage = "Chọn kho")][Display(Name = "Kho")] public int? WarehouseId { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày giao")] public DateTime DeliveryDate { get; set; } = DateTime.Today.AddDays(5);
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public string? Number { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class InvoiceForm
{
    public int Id { get; set; }
    public InvoiceDirection Direction { get; set; } = InvoiceDirection.Sales;
    [DataType(DataType.Date)][Display(Name = "Ngày hóa đơn")] public DateTime Date { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hạn thanh toán")] public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);
    [Required(ErrorMessage = "Chọn đối tượng")][Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    [StringLength(20)][Display(Name = "Ký hiệu")] public string? Series { get; set; }
    [StringLength(20)][Display(Name = "Số hóa đơn")] public string? InvoiceNo { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int? SalesOrderId { get; set; }
    public int? PurchaseOrderId { get; set; }
    public string? Number { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class InventoryDocForm
{
    public int Id { get; set; }
    public InvDocType Type { get; set; } = InvDocType.Receipt;
    [DataType(DataType.Date)][Display(Name = "Ngày")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Chọn kho")][Display(Name = "Kho")] public int? WarehouseId { get; set; }
    [Display(Name = "Kho nhận")] public int? ToWarehouseId { get; set; }
    [Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    [Display(Name = "TK đối ứng")] public int? CounterAccountId { get; set; }
    [StringLength(500)][Display(Name = "Lý do / diễn giải")] public string? Reason { get; set; }
    public string? Number { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class JournalForm
{
    public int Id { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày hạch toán")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Nhập diễn giải"), StringLength(500)][Display(Name = "Diễn giải")] public string Description { get; set; } = string.Empty;
    public string? Number { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class BomForm
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(30)][Display(Name = "Mã định mức")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn thành phẩm")][Display(Name = "Thành phẩm")] public int? FinishedItemId { get; set; }
    [Range(0.001, 1_000_000)][Display(Name = "Sản lượng định mức")] public decimal OutputQuantity { get; set; } = 1;
    [Range(0, 100_000_000_000)][Display(Name = "Nhân công định mức / đơn vị")] public decimal LaborCostPerUnit { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí SXC định mức / đơn vị")] public decimal OverheadPerUnit { get; set; }
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class CashVoucherForm
{
    public CashVoucherType Type { get; set; } = CashVoucherType.Receipt;
    [DataType(DataType.Date)][Display(Name = "Ngày")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Chọn quỹ / tài khoản")][Display(Name = "Quỹ / tài khoản tiền")] public int? CashAccountId { get; set; }
    [Display(Name = "Chuyển đến")] public int? ToCashAccountId { get; set; }
    [Display(Name = "Nội dung")] public CashPurpose Purpose { get; set; } = CashPurpose.CustomerReceipt;
    [Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    [Display(Name = "TK đối ứng")] public int? CounterAccountId { get; set; }
    [Display(Name = "Thanh toán cho hóa đơn")] public int? InvoiceId { get; set; }
    [Range(1, 1_000_000_000_000, ErrorMessage = "Số tiền phải lớn hơn 0")][Display(Name = "Số tiền")] public decimal Amount { get; set; }
    [Required(ErrorMessage = "Nhập diễn giải"), StringLength(500)][Display(Name = "Diễn giải")] public string Description { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Người nộp / nhận")] public string? PayerPayee { get; set; }
}

// ===================================================================== Báo cáo
public class TrialBalanceRow
{
    public Account Account { get; set; } = null!;
    public bool IsGroup { get; set; }
    public decimal OpenDebit { get; set; }
    public decimal OpenCredit { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal CloseDebit { get; set; }
    public decimal CloseCredit { get; set; }
}

public class LedgerRow
{
    public DateTime Date { get; set; }
    public string Number { get; set; } = "";
    public int EntryId { get; set; }
    public string Description { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string? CounterAccounts { get; set; }
    public string? PartnerName { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
}

public class ReportLine
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? Previous { get; set; }
    public bool Bold { get; set; }
    public int Indent { get; set; }
}

public class AgingRow
{
    public Partner Partner { get; set; } = null!;
    public decimal NotDue { get; set; }
    public decimal D1_30 { get; set; }
    public decimal D31_60 { get; set; }
    public decimal D61_90 { get; set; }
    public decimal Over90 { get; set; }
    public decimal Total => NotDue + D1_30 + D31_60 + D61_90 + Over90;
}

public class DashboardViewModel
{
    public decimal RevenueMonth { get; set; }
    public decimal GrossProfitMonth { get; set; }
    public decimal CashBalance { get; set; }
    public decimal Receivable { get; set; }
    public decimal ReceivableOverdue { get; set; }
    public decimal Payable { get; set; }
    public decimal InventoryValue { get; set; }
    public decimal AssetNbv { get; set; }
    public List<(string Month, decimal Revenue, decimal Cost)> Monthly { get; set; } = new();
    public List<(Item Item, decimal OnHand)> LowStock { get; set; } = new();
    public int PoPendingApproval { get; set; }
    public int SoToDeliver { get; set; }
    public int ProductionOpen { get; set; }
    public int DraftInvoices { get; set; }
    public List<Invoice> OverdueInvoices { get; set; } = new();
}
