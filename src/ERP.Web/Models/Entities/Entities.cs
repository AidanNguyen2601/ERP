using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  NGƯỜI DÙNG & PHÂN QUYỀN
// =====================================================================
public class AppUser
{
    public int Id { get; set; }
    [Required, StringLength(50)][Display(Name = "Tên đăng nhập")] public string Username { get; set; } = string.Empty;
    [Required, StringLength(150)][Display(Name = "Họ và tên")] public string FullName { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [Required, StringLength(100)][Display(Name = "Phòng ban")] public string Department { get; set; } = string.Empty;
    [Required, StringLength(20)][Display(Name = "Vai trò")] public string Role { get; set; } = AppRoles.Sales;
    [Required, StringLength(500)] public string PasswordHash { get; set; } = string.Empty;
    [Display(Name = "Kích hoạt")] public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Director = "Director";
    public const string Accountant = "Accountant";
    public const string Sales = "Sales";
    public const string Purchasing = "Purchasing";
    public const string Warehouse = "Warehouse";
    public const string Production = "Production";
    public const string Quality = "Quality";

    // Nhóm quyền dùng cho [Authorize(Roles = ...)]
    public const string Accounting = Admin + "," + Accountant;
    public const string FinanceView = Admin + "," + Accountant + "," + Director;
    public const string SalesTeam = Admin + "," + Sales;
    public const string PurchaseTeam = Admin + "," + Purchasing;
    public const string WarehouseTeam = Admin + "," + Warehouse;
    public const string ProductionTeam = Admin + "," + Production;
    public const string Approvers = Admin + "," + Director;
    /// <summary>Lập / kết luận phiếu kiểm tra chất lượng</summary>
    public const string QcInspectors = Admin + "," + Quality + "," + Warehouse + "," + Production;
    /// <summary>Khai báo tiêu chí kiểm tra, quyết định xử lý hàng không đạt</summary>
    public const string QcManagers = Admin + "," + Quality;

    public static readonly string[] All = { Admin, Director, Accountant, Sales, Purchasing, Warehouse, Production, Quality };

    public static string DisplayName(string role) => role switch
    {
        Admin => "Quản trị hệ thống",
        Director => "Ban Giám đốc",
        Accountant => "Kế toán",
        Sales => "Kinh doanh",
        Purchasing => "Mua hàng",
        Warehouse => "Thủ kho",
        Production => "Sản xuất",
        Quality => "Quản lý chất lượng (QC)",
        _ => role
    };
}

// =====================================================================
//  KẾ TOÁN TỔNG HỢP
// =====================================================================
/// <summary>Hệ thống tài khoản kế toán (theo Thông tư 200/2014/TT-BTC, rút gọn)</summary>
public class Account
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập số hiệu TK"), StringLength(20)][RegularExpression(@"^\d{3,8}$", ErrorMessage = "Số hiệu TK gồm 3–8 chữ số")]
    [Display(Name = "Số hiệu TK")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên TK"), StringLength(200)][Display(Name = "Tên tài khoản")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Loại")] public AccountType Type { get; set; }
    [Display(Name = "Được hạch toán")] public bool IsPostable { get; set; } = true;
    [Display(Name = "Theo dõi công nợ đối tượng")] public bool TrackPartner { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;

    [NotMapped] public int Level => Code.Length <= 3 ? 1 : Code.Length - 2;
    [NotMapped] public string Display => $"{Code} – {Name}";
}

public class FiscalPeriod
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedAt { get; set; }
    [StringLength(150)] public string? ClosedBy { get; set; }
    [NotMapped] public string Name => $"{Month:00}/{Year}";
}

/// <summary>Chứng từ kế toán (bút toán) – mọi nghiệp vụ đều sinh ra bút toán ở đây</summary>
public class JournalEntry
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số chứng từ")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày hạch toán")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Nhập diễn giải"), StringLength(500)][Display(Name = "Diễn giải")] public string Description { get; set; } = string.Empty;
    [Display(Name = "Nguồn")] public JournalSource Source { get; set; } = JournalSource.Manual;
    [StringLength(30)][Display(Name = "Chứng từ gốc")] public string? SourceRef { get; set; }
    public int? SourceId { get; set; }
    [Display(Name = "Trạng thái")] public DocStatus Status { get; set; } = DocStatus.Draft;
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? PostedAt { get; set; }
    /// <summary>Bút toán đảo của bút toán nào (khi hủy chứng từ)</summary>
    public int? ReversalOfId { get; set; }
    public ICollection<JournalLine> Lines { get; set; } = new List<JournalLine>();

    [NotMapped] public decimal TotalDebit => Lines.Sum(l => l.Debit);
    [NotMapped] public decimal TotalCredit => Lines.Sum(l => l.Credit);
}

public class JournalLine
{
    public int Id { get; set; }
    public int JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    [StringLength(300)] public string? Description { get; set; }
}

// =====================================================================
//  ĐỐI TÁC (KHÁCH HÀNG / NHÀ CUNG CẤP)
// =====================================================================
public class Partner
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã đối tượng")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên đối tượng")] public string Name { get; set; } = string.Empty;
    [StringLength(20)][Display(Name = "Mã số thuế")] public string? TaxCode { get; set; }
    [StringLength(300)][Display(Name = "Địa chỉ")] public string? Address { get; set; }
    [StringLength(30)][Display(Name = "Điện thoại")] public string? Phone { get; set; }
    [EmailAddress, StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [StringLength(100)][Display(Name = "Người liên hệ")] public string? ContactPerson { get; set; }
    [Display(Name = "Là khách hàng")] public bool IsCustomer { get; set; }
    [Display(Name = "Là nhà cung cấp")] public bool IsVendor { get; set; }
    [Range(0, 1_000_000_000_000)][Display(Name = "Hạn mức công nợ")] public decimal CreditLimit { get; set; }
    [Range(0, 365)][Display(Name = "Số ngày được nợ")] public int PaymentTermDays { get; set; } = 30;
    [Display(Name = "Đang giao dịch")] public bool IsActive { get; set; } = true;
    [NotMapped] public string Display => $"{Code} – {Name}";
}

// =====================================================================
//  KHO
// =====================================================================
public class Item
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(30)][Display(Name = "Mã hàng")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên hàng")] public string Name { get; set; } = string.Empty;
    [Required, StringLength(20)][Display(Name = "Đơn vị tính")] public string Unit { get; set; } = "Cái";
    [Display(Name = "Loại")] public ItemType Type { get; set; } = ItemType.Merchandise;
    [Range(0, 100_000_000_000)][Display(Name = "Giá bán")] public decimal SalePrice { get; set; }
    [Display(Name = "Giá vốn bình quân")] public decimal AvgCost { get; set; }
    [Range(0, 10)][Display(Name = "Thuế GTGT (%)")] public int VatRate { get; set; } = 10;
    [Range(0, 1_000_000)][Display(Name = "Tồn tối thiểu")] public decimal MinStock { get; set; }
    [Display(Name = "Đang kinh doanh")] public bool IsActive { get; set; } = true;

    [NotMapped] public bool IsStocked => Type != ItemType.Service;
    [NotMapped] public string Display => $"{Code} – {Name}";
}

public class Warehouse
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã kho")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(150)][Display(Name = "Tên kho")] public string Name { get; set; } = string.Empty;
    [StringLength(300)][Display(Name = "Địa chỉ")] public string? Address { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

/// <summary>Tồn kho hiện tại theo mặt hàng × kho</summary>
public class StockLevel
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>Sổ chi tiết vật tư (thẻ kho): mỗi dòng nhập / xuất</summary>
public class StockMove
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    /// <summary>Dương = nhập, âm = xuất</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    public int InventoryDocId { get; set; }
    [StringLength(30)] public string DocNumber { get; set; } = string.Empty;
    [StringLength(300)] public string? Description { get; set; }
}

/// <summary>Phiếu nhập / xuất / chuyển kho / kiểm kê</summary>
public class InventoryDoc
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số phiếu")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Loại phiếu")] public InvDocType Type { get; set; } = InvDocType.Receipt;
    [DataType(DataType.Date)][Display(Name = "Ngày")] public DateTime Date { get; set; } = DateTime.Today;
    [Display(Name = "Kho")] public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [Display(Name = "Kho nhận")] public int? ToWarehouseId { get; set; }
    public Warehouse? ToWarehouse { get; set; }
    [Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Display(Name = "TK đối ứng")] public int? CounterAccountId { get; set; }
    public Account? CounterAccount { get; set; }
    [StringLength(500)][Display(Name = "Lý do / diễn giải")] public string? Reason { get; set; }
    [Display(Name = "Nguồn")] public DocOrigin Origin { get; set; } = DocOrigin.Manual;
    [StringLength(30)][Display(Name = "Chứng từ gốc")] public string? OriginRef { get; set; }
    public int? OriginId { get; set; }
    [Display(Name = "Trạng thái")] public DocStatus Status { get; set; } = DocStatus.Draft;
    public int? JournalEntryId { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<InventoryDocLine> Lines { get; set; } = new List<InventoryDocLine>();
    [NotMapped] public decimal TotalAmount => Lines.Sum(l => l.Amount);
}

public class InventoryDocLine
{
    public int Id { get; set; }
    public int InventoryDocId { get; set; }
    public InventoryDoc? InventoryDoc { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    /// <summary>Số lượng nhập / xuất (kiểm kê: chênh lệch, có thể âm)</summary>
    public decimal Quantity { get; set; }
    /// <summary>Kiểm kê: số lượng thực tế đếm được</summary>
    public decimal? CountedQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
}

// =====================================================================
//  MUA HÀNG
// =====================================================================
public class PurchaseOrder
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số đơn mua")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày đặt")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Chọn nhà cung cấp")][Display(Name = "Nhà cung cấp")] public int? VendorId { get; set; }
    public Partner? Vendor { get; set; }
    [Required(ErrorMessage = "Chọn kho nhận")][Display(Name = "Kho nhận hàng")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày giao dự kiến")] public DateTime ExpectedDate { get; set; } = DateTime.Today.AddDays(7);
    [Display(Name = "Trạng thái")] public PoStatus Status { get; set; } = PoStatus.Draft;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal Total { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public int? ApprovedById { get; set; }
    public AppUser? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
}

public class PurchaseOrderLine
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int VatRate { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal InvoicedQty { get; set; }
    /// <summary>Số lượng đã trả lại nhà cung cấp khi CHƯA lập hóa đơn (giảm số lượng còn phải lập hóa đơn; phần đã có hóa đơn được cấn trừ công nợ)</summary>
    public decimal ReturnedQty { get; set; }
    /// <summary>Số lượng còn có thể lập hóa đơn mua = đã nhận − đã trả chưa lập hóa đơn − đã lập hóa đơn</summary>
    [NotMapped] public decimal BillableQty => ReceivedQty - ReturnedQty - InvoicedQty;
    [NotMapped] public decimal Amount => Math.Round(Quantity * UnitPrice, 0);
}

// =====================================================================
//  BÁN HÀNG
// =====================================================================
public class SalesOrder
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số đơn bán")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày đơn")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Chọn khách hàng")][Display(Name = "Khách hàng")] public int? CustomerId { get; set; }
    public Partner? Customer { get; set; }
    [Required(ErrorMessage = "Chọn kho xuất")][Display(Name = "Kho xuất hàng")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày giao")] public DateTime DeliveryDate { get; set; } = DateTime.Today.AddDays(3);
    [Display(Name = "Trạng thái")] public SoStatus Status { get; set; } = SoStatus.Draft;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal Total { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    /// <summary>Người duyệt vượt hạn mức công nợ (nếu có)</summary>
    public int? CreditApprovedById { get; set; }
    public AppUser? CreditApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<SalesOrderLine> Lines { get; set; } = new List<SalesOrderLine>();
}

public class SalesOrderLine
{
    public int Id { get; set; }
    public int SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public int VatRate { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    [NotMapped] public decimal Amount => Math.Round(Quantity * UnitPrice * (100 - DiscountPct) / 100, 0);
    [NotMapped] public decimal NetPrice => UnitPrice * (100 - DiscountPct) / 100;
}

// =====================================================================
//  HÓA ĐƠN (bán ra / mua vào)
// =====================================================================
public class Invoice
{
    public int Id { get; set; }
    [Display(Name = "Loại")] public InvoiceDirection Direction { get; set; }
    [Required, StringLength(30)][Display(Name = "Số chứng từ")] public string Number { get; set; } = string.Empty;
    [StringLength(20)][Display(Name = "Ký hiệu HĐ")] public string? Series { get; set; }
    [StringLength(20)][Display(Name = "Số hóa đơn")] public string? InvoiceNo { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày hóa đơn")] public DateTime Date { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hạn thanh toán")] public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);
    [Required(ErrorMessage = "Chọn đối tượng")][Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public int? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    [Display(Name = "Trạng thái")] public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal Total { get; set; }
    public decimal PaidAmount { get; set; }
    public int? JournalEntryId { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();

    [NotMapped] public decimal Outstanding => Status == InvoiceStatus.Issued ? Total - PaidAmount : 0;
    [NotMapped] public bool IsOverdue => Outstanding > 0 && DueDate < DateTime.Today;
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(20)] public string? Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int VatRate { get; set; }
    public decimal Amount { get; set; }
    public decimal VatAmount { get; set; }
    /// <summary>TK doanh thu (bán) hoặc TK chi phí (mua dịch vụ). Bỏ trống: hệ thống tự xác định theo loại hàng</summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    /// <summary>Dòng đơn hàng gốc (SO line / PO line)</summary>
    public int? SourceLineId { get; set; }
    /// <summary>Mua hàng: giá trị hàng đã ghi Có 331 khi nhập kho → hóa đơn chỉ ghi phần thuế</summary>
    public bool GoodsPosted { get; set; }
}

// =====================================================================
//  TIỀN (QUỸ & NGÂN HÀNG)
// =====================================================================
public class CashAccount
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(150)][Display(Name = "Tên quỹ / tài khoản")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Loại")] public CashAccountType Type { get; set; }
    [Display(Name = "TK kế toán")] public int AccountId { get; set; }
    public Account? Account { get; set; }
    [StringLength(150)][Display(Name = "Ngân hàng")] public string? BankName { get; set; }
    [StringLength(50)][Display(Name = "Số tài khoản")] public string? BankAccountNo { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

public class CashVoucher
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số phiếu")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Loại phiếu")] public CashVoucherType Type { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày")] public DateTime Date { get; set; } = DateTime.Today;
    [Display(Name = "Quỹ / tài khoản")] public int CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }
    [Display(Name = "Chuyển đến")] public int? ToCashAccountId { get; set; }
    public CashAccount? ToCashAccount { get; set; }
    [Display(Name = "Nội dung")] public CashPurpose Purpose { get; set; }
    [Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Display(Name = "TK đối ứng")] public int? CounterAccountId { get; set; }
    public Account? CounterAccount { get; set; }
    [Display(Name = "Số tiền")] public decimal Amount { get; set; }
    [Required(ErrorMessage = "Nhập diễn giải"), StringLength(500)][Display(Name = "Diễn giải")] public string Description { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Người nộp / nhận")] public string? PayerPayee { get; set; }
    [Display(Name = "Trạng thái")] public DocStatus Status { get; set; } = DocStatus.Posted;
    public int? JournalEntryId { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

/// <summary>Phân bổ tiền thu / chi vào từng hóa đơn (cấn trừ công nợ)</summary>
public class PaymentAllocation
{
    public int Id { get; set; }
    public int CashVoucherId { get; set; }
    public CashVoucher? CashVoucher { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
}

// =====================================================================
//  TÀI SẢN CỐ ĐỊNH
// =====================================================================
public class AssetCategory
{
    public int Id { get; set; }
    [Required, StringLength(20)][Display(Name = "Mã nhóm")] public string Code { get; set; } = string.Empty;
    [Required, StringLength(150)][Display(Name = "Tên nhóm")] public string Name { get; set; } = string.Empty;
    [Display(Name = "TK nguyên giá")] public int AssetAccountId { get; set; }
    public Account? AssetAccount { get; set; }
    [Display(Name = "TK hao mòn")] public int DepreciationAccountId { get; set; }
    public Account? DepreciationAccount { get; set; }
    [Display(Name = "TK chi phí khấu hao")] public int ExpenseAccountId { get; set; }
    public Account? ExpenseAccount { get; set; }
    [Range(1, 600)][Display(Name = "Thời gian KH mặc định (tháng)")] public int DefaultLifeMonths { get; set; } = 60;
}

public class FixedAsset
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(30)][Display(Name = "Mã tài sản")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên tài sản")] public string Name { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn nhóm")][Display(Name = "Nhóm tài sản")] public int? CategoryId { get; set; }
    public AssetCategory? Category { get; set; }
    [StringLength(100)][Display(Name = "Bộ phận sử dụng")] public string? Department { get; set; }
    [StringLength(100)][Display(Name = "Số serial / biển số")] public string? SerialNo { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày mua")] public DateTime AcquisitionDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày bắt đầu khấu hao")] public DateTime DepreciationStartDate { get; set; } = DateTime.Today;
    [Range(1, 1_000_000_000_000)][Display(Name = "Nguyên giá")] public decimal OriginalCost { get; set; }
    [Range(1, 600)][Display(Name = "Thời gian khấu hao (tháng)")] public int UsefulLifeMonths { get; set; } = 60;
    [Display(Name = "Hao mòn lũy kế")] public decimal AccumulatedDepreciation { get; set; }
    [Display(Name = "Trạng thái")] public AssetStatus Status { get; set; } = AssetStatus.InUse;
    [DataType(DataType.Date)][Display(Name = "Ngày thanh lý")] public DateTime? DisposalDate { get; set; }
    [Display(Name = "Giá trị thu hồi")] public decimal? DisposalValue { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }

    [NotMapped] public decimal NetBookValue => OriginalCost - AccumulatedDepreciation;
    [NotMapped] public decimal MonthlyDepreciation => Math.Round(OriginalCost / Math.Max(1, UsefulLifeMonths), 0);
}

public class DepreciationRun
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Total { get; set; }
    public int? JournalEntryId { get; set; }
    [StringLength(150)] public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<DepreciationLine> Lines { get; set; } = new List<DepreciationLine>();
}

public class DepreciationLine
{
    public int Id { get; set; }
    public int DepreciationRunId { get; set; }
    public DepreciationRun? DepreciationRun { get; set; }
    public int FixedAssetId { get; set; }
    public FixedAsset? FixedAsset { get; set; }
    public decimal Amount { get; set; }
}

// =====================================================================
//  SẢN XUẤT
// =====================================================================
/// <summary>Định mức nguyên vật liệu (Bill of Materials)</summary>
public class Bom
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(30)][Display(Name = "Mã định mức")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn thành phẩm")][Display(Name = "Thành phẩm")] public int? FinishedItemId { get; set; }
    public Item? FinishedItem { get; set; }
    [Range(0.001, 1_000_000)][Display(Name = "Sản lượng định mức")] public decimal OutputQuantity { get; set; } = 1;
    [Range(0, 100_000_000_000)][Display(Name = "Nhân công định mức / đơn vị")] public decimal LaborCostPerUnit { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí SXC định mức / đơn vị")] public decimal OverheadPerUnit { get; set; }
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public ICollection<BomLine> Lines { get; set; } = new List<BomLine>();
}

public class BomLine
{
    public int Id { get; set; }
    public int BomId { get; set; }
    public Bom? Bom { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal ScrapPct { get; set; }
}

public class ProductionOrder
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số lệnh SX")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn định mức")][Display(Name = "Định mức (BOM)")] public int? BomId { get; set; }
    public Bom? Bom { get; set; }
    [Display(Name = "Thành phẩm")] public int ItemId { get; set; }
    public Item? Item { get; set; }
    [Range(0.001, 1_000_000)][Display(Name = "Số lượng sản xuất")] public decimal Quantity { get; set; } = 1;
    [Required(ErrorMessage = "Chọn kho NVL")][Display(Name = "Kho nguyên vật liệu")] public int? MaterialWarehouseId { get; set; }
    public Warehouse? MaterialWarehouse { get; set; }
    [Required(ErrorMessage = "Chọn kho thành phẩm")][Display(Name = "Kho thành phẩm")] public int? FinishedWarehouseId { get; set; }
    public Warehouse? FinishedWarehouse { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày bắt đầu")] public DateTime StartDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày hoàn thành dự kiến")] public DateTime DueDate { get; set; } = DateTime.Today.AddDays(7);
    [Display(Name = "Trạng thái")] public ProductionStatus Status { get; set; } = ProductionStatus.Draft;
    [Display(Name = "Đơn bán liên quan")] public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public decimal MaterialCost { get; set; }
    public decimal LaborCost { get; set; }
    public decimal OverheadCost { get; set; }
    public decimal CompletedQty { get; set; }
    public decimal UnitCost { get; set; }
    public DateTime? CompletedDate { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<ProductionOrderLine> Lines { get; set; } = new List<ProductionOrderLine>();
    [NotMapped] public decimal TotalCost => MaterialCost + LaborCost + OverheadCost;
}

public class ProductionOrderLine
{
    public int Id { get; set; }
    public int ProductionOrderId { get; set; }
    public ProductionOrder? ProductionOrder { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal RequiredQty { get; set; }
    public decimal IssuedQty { get; set; }
    public decimal Cost { get; set; }
}

// =====================================================================
//  DANH MỤC DÙNG CHUNG & TÙY CHỌN NGƯỜI DÙNG
// =====================================================================
/// <summary>Danh mục dùng chung: đơn vị tính, phòng ban, ngân hàng... (chứng từ lưu theo tên để giữ lịch sử)</summary>
public class CatalogItem
{
    public int Id { get; set; }
    [Display(Name = "Loại danh mục")] public CatalogType Type { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)][Display(Name = "Tên")] public string Name { get; set; } = string.Empty;
    [Range(0, 9999)][Display(Name = "Thứ tự")] public int SortOrder { get; set; }
    [StringLength(300)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

/// <summary>Tùy chọn theo người dùng (giao diện, yêu thích, bố cục bảng) – Value là JSON</summary>
public class UserPreference
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(200)] public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  CẤU HÌNH BÚT TOÁN THEO GIAO DỊCH
// =====================================================================
/// <summary>Tài khoản được chọn cho một dòng định khoản của giao dịch tự động (Key khai báo trong PostingCatalog)</summary>
public class PostingRule
{
    public int Id { get; set; }
    [Required, StringLength(60)] public string Key { get; set; } = string.Empty;
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    [StringLength(150)] public string? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  QUẢN LÝ CHẤT LƯỢNG
// =====================================================================
/// <summary>Kế hoạch kiểm tra: bộ tiêu chí áp dụng cho một công đoạn, một mặt hàng hoặc một loại hàng</summary>
public class QcPlan
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã kế hoạch")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên kế hoạch kiểm tra")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Công đoạn kiểm tra")] public QcStage Stage { get; set; } = QcStage.Incoming;
    [Display(Name = "Áp dụng cho mặt hàng")] public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Display(Name = "Hoặc loại hàng")] public ItemType? ItemType { get; set; }
    [Range(0, 100)][Display(Name = "Tỷ lệ lấy mẫu (%)")] public decimal SamplePct { get; set; } = 10;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
    public ICollection<QcPlanLine> Lines { get; set; } = new List<QcPlanLine>();
}

public class QcPlanLine
{
    public int Id { get; set; }
    public int QcPlanId { get; set; }
    public QcPlan? QcPlan { get; set; }
    public int Seq { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(200)] public string? Method { get; set; }
    [StringLength(200)] public string? Standard { get; set; }
    public QcCriterionType Type { get; set; } = QcCriterionType.PassFail;
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    [StringLength(20)] public string? Unit { get; set; }
    /// <summary>Tiêu chí trọng yếu: không đạt thì cả lô không đạt</summary>
    public bool IsCritical { get; set; }
}

/// <summary>Phiếu kiểm tra chất lượng (KCS) cho một lô hàng</summary>
public class QcInspection
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số phiếu")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày kiểm tra")] public DateTime Date { get; set; } = DateTime.Today;
    [Display(Name = "Công đoạn")] public QcStage Stage { get; set; } = QcStage.Incoming;
    [Required(ErrorMessage = "Chọn mặt hàng")][Display(Name = "Mặt hàng")] public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Display(Name = "Kho")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [Display(Name = "Nhà cung cấp / khách hàng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Display(Name = "Nguồn")] public DocOrigin Origin { get; set; } = DocOrigin.Manual;
    [StringLength(30)][Display(Name = "Chứng từ gốc")] public string? OriginRef { get; set; }
    public int? OriginId { get; set; }
    [Display(Name = "Kế hoạch kiểm tra")] public int? QcPlanId { get; set; }
    public QcPlan? QcPlan { get; set; }
    [Range(0.001, 100_000_000, ErrorMessage = "Số lượng lô phải > 0")][Display(Name = "Số lượng lô")] public decimal LotQty { get; set; }
    [Range(0, 100_000_000)][Display(Name = "Số lượng mẫu")] public decimal SampleQty { get; set; }
    [Range(0, 100_000_000)][Display(Name = "SL đạt")] public decimal AcceptedQty { get; set; }
    [Range(0, 100_000_000)][Display(Name = "SL không đạt")] public decimal RejectedQty { get; set; }
    [Display(Name = "Kết luận")] public QcResult Result { get; set; } = QcResult.Pending;
    [Display(Name = "Trạng thái")] public QcStatus Status { get; set; } = QcStatus.Draft;
    [StringLength(1000)][Display(Name = "Nhận xét")] public string? Note { get; set; }
    public int? InspectorId { get; set; }
    public AppUser? Inspector { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public ICollection<QcInspectionLine> Lines { get; set; } = new List<QcInspectionLine>();

    [NotMapped] public decimal DefectRate => LotQty > 0 ? Math.Round(RejectedQty * 100 / LotQty, 2) : 0;
}

public class QcInspectionLine
{
    public int Id { get; set; }
    public int QcInspectionId { get; set; }
    public QcInspection? QcInspection { get; set; }
    public int Seq { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(200)] public string? Method { get; set; }
    [StringLength(200)] public string? Standard { get; set; }
    public QcCriterionType Type { get; set; } = QcCriterionType.PassFail;
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    [StringLength(20)] public string? Unit { get; set; }
    public bool IsCritical { get; set; }
    /// <summary>Giá trị đo (tiêu chí đo lường)</summary>
    public decimal? MeasuredValue { get; set; }
    /// <summary>null = chưa đánh giá</summary>
    public bool? Passed { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

/// <summary>Báo cáo không phù hợp (NCR): xử lý số lượng hàng không đạt</summary>
public class QcNonconformance
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số NCR")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày lập")] public DateTime Date { get; set; } = DateTime.Today;
    [Display(Name = "Phiếu kiểm tra")] public int? InspectionId { get; set; }
    public QcInspection? Inspection { get; set; }
    [Required(ErrorMessage = "Chọn mặt hàng")][Display(Name = "Mặt hàng")] public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Display(Name = "Kho đang giữ hàng")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [Display(Name = "Nhà cung cấp / khách hàng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Range(0.001, 100_000_000, ErrorMessage = "Số lượng phải > 0")][Display(Name = "Số lượng không phù hợp")] public decimal Quantity { get; set; }
    [Required(ErrorMessage = "Mô tả lỗi"), StringLength(1000)][Display(Name = "Mô tả lỗi")] public string Description { get; set; } = string.Empty;
    [StringLength(1000)][Display(Name = "Nguyên nhân gốc")] public string? RootCause { get; set; }
    [Display(Name = "Hướng xử lý")] public NcrDisposition Disposition { get; set; } = NcrDisposition.Pending;
    [StringLength(1000)][Display(Name = "Hành động khắc phục / phòng ngừa")] public string? CorrectiveAction { get; set; }
    [StringLength(150)][Display(Name = "Người chịu trách nhiệm")] public string? Responsible { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hạn xử lý")] public DateTime? DueDate { get; set; }
    [Display(Name = "Trạng thái")] public NcrStatus Status { get; set; } = NcrStatus.Open;
    /// <summary>Phiếu xuất kho khi hủy bỏ / trả lại nhà cung cấp</summary>
    public int? InventoryDocId { get; set; }
    public InventoryDoc? InventoryDoc { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ClosedAt { get; set; }
    [StringLength(150)] public string? ClosedBy { get; set; }

    [NotMapped] public bool IsOverdue => Status == NcrStatus.Open && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
}

// =====================================================================
//  TRẢ HÀNG NHÀ CUNG CẤP (từ xử lý NCR chất lượng)
// =====================================================================
/// <summary>
/// Trả hàng nhà cung cấp theo từng dòng đơn mua: phần chưa lập hóa đơn giảm số lượng phải lập hóa đơn (và giảm hàng nhập chưa có hóa đơn),
/// phần đã có hóa đơn được cấn trừ vào công nợ hóa đơn kèm hoàn thuế GTGT đầu vào.
/// </summary>
public class PurchaseReturn
{
    public int Id { get; set; }
    public int NcrId { get; set; }
    public QcNonconformance? Ncr { get; set; }
    public int InventoryDocId { get; set; }
    public InventoryDoc? InventoryDoc { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Quantity { get; set; }
    /// <summary>Phần chưa lập hóa đơn</summary>
    public decimal QtyUnbilled { get; set; }
    /// <summary>Phần đã lập hóa đơn</summary>
    public decimal QtyBilled { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Giá trị trả lại theo giá mua (chưa thuế) = phần chưa lập hóa đơn + phần đã lập hóa đơn</summary>
    public decimal Amount { get; set; }
    /// <summary>Phần giá trị của số lượng đã lập hóa đơn (chưa thuế)</summary>
    public decimal BilledAmount { get; set; }
    /// <summary>Thuế GTGT đầu vào được hoàn (chỉ phần đã lập hóa đơn)</summary>
    public decimal VatAmount { get; set; }
    /// <summary>Phần giảm trừ chưa cấn trừ được vào hóa đơn nào (hóa đơn đã thanh toán hết) – nhà cung cấp còn nợ lại</summary>
    public decimal Unapplied { get; set; }
    public int? AdjustJournalEntryId { get; set; }
    public ICollection<InvoiceOffset> Offsets { get; set; } = new List<InvoiceOffset>();
}

/// <summary>Cấn trừ công nợ một hóa đơn mua bằng giá trị hàng trả lại (đã cộng vào Invoice.PaidAmount)</summary>
public class InvoiceOffset
{
    public int Id { get; set; }
    public int PurchaseReturnId { get; set; }
    public PurchaseReturn? PurchaseReturn { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
}

// =====================================================================
//  HÓA ĐƠN ĐIỀU CHỈNH GIẢM BÁN HÀNG (hàng bán bị trả lại / giảm giá hàng bán)
// =====================================================================
/// <summary>
/// Điều chỉnh giảm một hóa đơn bán đã phát hành:
///  – Hàng bán bị trả lại: nhập lại kho theo giá vốn lúc bán (Nợ 15x / Có 632), giảm trừ doanh thu (Nợ 5212 + Nợ 33311 / Có 131)
///  – Giảm giá hàng bán: Nợ 5213 + Nợ 33311 / Có 131, không nhập kho
/// Giá trị điều chỉnh được cấn trừ vào công nợ hóa đơn; phần hóa đơn đã thanh toán hết ghi nhận là khách còn được ghi có (Unapplied).
/// </summary>
public class SalesCreditNote
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số chứng từ")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày")] public DateTime Date { get; set; } = DateTime.Today;
    [Display(Name = "Loại điều chỉnh")] public CreditNoteKind Kind { get; set; } = CreditNoteKind.Return;
    public int CustomerId { get; set; }
    public Partner? Customer { get; set; }
    [Display(Name = "Hóa đơn gốc")] public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    [Display(Name = "Kho nhận hàng trả lại")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [Required(ErrorMessage = "Nhập lý do"), StringLength(500)][Display(Name = "Lý do")] public string Reason { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal Total { get; set; }
    public DocStatus Status { get; set; } = DocStatus.Posted;
    public int? JournalEntryId { get; set; }
    public int? InventoryDocId { get; set; }
    public InventoryDoc? InventoryDoc { get; set; }
    /// <summary>Phần chưa cấn trừ được vào hóa đơn (hóa đơn đã thanh toán hết): khách còn được ghi có</summary>
    public decimal Unapplied { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<SalesCreditLine> Lines { get; set; } = new List<SalesCreditLine>();
    public ICollection<SalesCreditOffset> Offsets { get; set; } = new List<SalesCreditOffset>();
}

public class SalesCreditLine
{
    public int Id { get; set; }
    public int SalesCreditNoteId { get; set; }
    public SalesCreditNote? SalesCreditNote { get; set; }
    public int InvoiceLineId { get; set; }
    public InvoiceLine? InvoiceLine { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(300)] public string Description { get; set; } = string.Empty;
    /// <summary>Số lượng trả lại (0 với giảm giá)</summary>
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public int VatRate { get; set; }
    public decimal VatAmount { get; set; }
    /// <summary>Giá vốn đơn vị khi nhập lại kho (giá vốn lúc bán)</summary>
    public decimal UnitCost { get; set; }
}

public class SalesCreditOffset
{
    public int Id { get; set; }
    public int SalesCreditNoteId { get; set; }
    public SalesCreditNote? SalesCreditNote { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
}
