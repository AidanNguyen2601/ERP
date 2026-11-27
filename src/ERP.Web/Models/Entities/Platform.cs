using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  ĐA CÔNG TY / ĐA CHI NHÁNH
// =====================================================================
/// <summary>Chứng từ / danh mục thuộc một chi nhánh (dùng cho phân quyền dữ liệu, báo cáo theo đơn vị, hợp nhất)</summary>
public interface IBranchOwned
{
    int? BranchId { get; set; }
}

/// <summary>Pháp nhân (công ty) trong tập đoàn</summary>
public class Company
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã công ty")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên công ty")] public string Name { get; set; } = string.Empty;
    [StringLength(20)][Display(Name = "Mã số thuế")] public string? TaxCode { get; set; }
    [StringLength(300)][Display(Name = "Địa chỉ")] public string? Address { get; set; }
    [Display(Name = "Là công ty mẹ")] public bool IsParent { get; set; }
    [Display(Name = "Tỷ lệ sở hữu của công ty mẹ (%)")][Range(0, 100)] public decimal OwnershipPct { get; set; } = 100;
    [DataType(DataType.Date)][Display(Name = "Ngày công ty mẹ nắm quyền kiểm soát")] public DateTime? AcquisitionDate { get; set; }
    [Display(Name = "LNST chưa phân phối của công ty con tại ngày mua")] public decimal RetainedAtAcquisition { get; set; }
    [Display(Name = "Đang hoạt động")] public bool IsActive { get; set; } = true;
    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
}

/// <summary>Chi nhánh / đơn vị hạch toán thuộc một công ty</summary>
public class Branch
{
    public int Id { get; set; }
    [Display(Name = "Công ty")] public int CompanyId { get; set; }
    public Company? Company { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã chi nhánh")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên chi nhánh")] public string Name { get; set; } = string.Empty;
    [StringLength(300)][Display(Name = "Địa chỉ")] public string? Address { get; set; }
    [Display(Name = "Đang hoạt động")] public bool IsActive { get; set; } = true;
    [NotMapped] public string Display => $"{Code} – {Name}";
}

// =====================================================================
//  NHẬT KÝ THAO TÁC
// =====================================================================
public class AuditLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public int? UserId { get; set; }
    [StringLength(150)] public string? UserName { get; set; }
    /// <summary>Tạo, Sửa, Xóa, Đổi trạng thái, Duyệt, Từ chối, Đăng nhập...</summary>
    [StringLength(40)] public string Action { get; set; } = string.Empty;
    [StringLength(80)] public string EntityType { get; set; } = string.Empty;
    [StringLength(80)] public string EntityName { get; set; } = string.Empty;
    [StringLength(40)] public string? EntityId { get; set; }
    [StringLength(100)] public string? DocNumber { get; set; }
    public string? Changes { get; set; }
    [StringLength(60)] public string? Ip { get; set; }
}

// =====================================================================
//  LUỒNG PHÊ DUYỆT
// =====================================================================
public enum ApprovalDocType
{
    [Display(Name = "Đơn mua hàng")] PurchaseOrder = 1,
    [Display(Name = "Đơn bán hàng")] SalesOrder = 2,
    [Display(Name = "Chứng từ kế toán thủ công")] JournalEntry = 3,
    [Display(Name = "Bảng lương")] Payroll = 4,
    [Display(Name = "Đề nghị mua hàng")] PurchaseRequisition = 5,
    [Display(Name = "Lệnh sản xuất")] ProductionOrder = 6,
}

public enum ApprovalStatus
{
    [Display(Name = "Chờ duyệt")] Pending = 1,
    [Display(Name = "Đã duyệt")] Approved = 2,
    [Display(Name = "Từ chối")] Rejected = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4,
}

/// <summary>Quy tắc duyệt: chứng từ loại X có giá trị từ MinAmount cần cấp Level do vai trò / người dùng duyệt</summary>
public class ApprovalRule
{
    public int Id { get; set; }
    [Display(Name = "Loại chứng từ")] public ApprovalDocType DocType { get; set; }
    [Required(ErrorMessage = "Nhập tên"), StringLength(150)][Display(Name = "Tên bước duyệt")] public string Name { get; set; } = string.Empty;
    [Range(1, 9)][Display(Name = "Cấp duyệt")] public int Level { get; set; } = 1;
    [Range(0, 1_000_000_000_000)][Display(Name = "Áp dụng từ giá trị")] public decimal MinAmount { get; set; }
    [Display(Name = "Chi nhánh (trống = mọi chi nhánh)")] public int? BranchId { get; set; }
    public Branch? Branch { get; set; }
    [StringLength(20)][Display(Name = "Vai trò duyệt")] public string? ApproverRole { get; set; }
    [Display(Name = "Hoặc người duyệt cụ thể")] public int? ApproverUserId { get; set; }
    public AppUser? ApproverUser { get; set; }
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
}

public class ApprovalRequest
{
    public int Id { get; set; }
    public ApprovalDocType DocType { get; set; }
    public int DocId { get; set; }
    [StringLength(40)] public string DocNumber { get; set; } = string.Empty;
    [StringLength(300)] public string? Summary { get; set; }
    public decimal Amount { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public int CurrentLevel { get; set; }
    public int? RequestedById { get; set; }
    public AppUser? RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public int? BranchId { get; set; }
    public ICollection<ApprovalStep> Steps { get; set; } = new List<ApprovalStep>();
}

public class ApprovalStep
{
    public int Id { get; set; }
    public int ApprovalRequestId { get; set; }
    public ApprovalRequest? ApprovalRequest { get; set; }
    public int Level { get; set; }
    [StringLength(150)] public string Name { get; set; } = string.Empty;
    [StringLength(20)] public string? ApproverRole { get; set; }
    public int? ApproverUserId { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public int? ActedById { get; set; }
    [StringLength(150)] public string? ActedByName { get; set; }
    public DateTime? ActedAt { get; set; }
    [StringLength(500)] public string? Comment { get; set; }
}

// =====================================================================
//  QUY TẮC ĐẶT MÃ, CÀI ĐẶT HỆ THỐNG
// =====================================================================
public class CodeRule
{
    public int Id { get; set; }
    /// <summary>customer, vendor, item.Material, item.FinishedGood, item.Merchandise, item.Service, employee, contract.Sale, contract.Purchase, tool</summary>
    [Required, StringLength(40)] public string Target { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tiền tố"), StringLength(15)][Display(Name = "Tiền tố")] public string Prefix { get; set; } = string.Empty;
    [Range(2, 10)][Display(Name = "Số chữ số")] public int Digits { get; set; } = 3;
    [Display(Name = "Bắt buộc theo quy tắc")] public bool Enforce { get; set; }
}

public class SystemSetting
{
    [Key, StringLength(80)] public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  NHÂN VIÊN, CHẤM CÔNG, LƯƠNG
// =====================================================================
public class Employee : IBranchOwned
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã nhân viên")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập họ tên"), StringLength(150)][Display(Name = "Họ và tên")] public string FullName { get; set; } = string.Empty;
    [StringLength(100)][Display(Name = "Phòng ban / bộ phận")] public string? Department { get; set; }
    [StringLength(100)][Display(Name = "Chức danh")] public string? Position { get; set; }
    [Display(Name = "Chi nhánh")] public int? BranchId { get; set; }
    public Branch? Branch { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày vào làm")] public DateTime StartDate { get; set; } = DateTime.Today;
    [Range(0, 10_000_000_000)][Display(Name = "Lương cơ bản / tháng")] public decimal BaseSalary { get; set; }
    [Range(0, 10_000_000_000)][Display(Name = "Phụ cấp chịu thuế / tháng")] public decimal Allowance { get; set; }
    [Range(0, 10_000_000_000)][Display(Name = "Phụ cấp không chịu thuế (ăn trưa...)")] public decimal NonTaxAllowance { get; set; }
    [Range(0, 10_000_000_000)][Display(Name = "Lương đóng bảo hiểm")] public decimal InsuranceSalary { get; set; }
    [Range(0, 20)][Display(Name = "Số người phụ thuộc")] public int Dependents { get; set; }
    [StringLength(20)][Display(Name = "Mã số thuế cá nhân")] public string? PersonalTaxCode { get; set; }
    [StringLength(30)][Display(Name = "Số CCCD")] public string? IdNumber { get; set; }
    [StringLength(30)][Display(Name = "Số tài khoản")] public string? BankAccountNo { get; set; }
    [StringLength(100)][Display(Name = "Ngân hàng")] public string? BankName { get; set; }
    [Required, StringLength(10)][Display(Name = "TK chi phí lương")] public string ExpenseAccount { get; set; } = "6421";
    [Display(Name = "Đang làm việc")] public bool IsActive { get; set; } = true;
    [NotMapped] public string Display => $"{Code} – {FullName}";
}

public class Attendance
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal WorkDays { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal LeaveDays { get; set; }
    [StringLength(40)] public string? Source { get; set; }
}

public class PayrollRun : IBranchOwned
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public int? BranchId { get; set; }
    public Branch? Branch { get; set; }
    public decimal StandardDays { get; set; } = 26;
    public DocStatus Status { get; set; } = DocStatus.Draft;
    public int? JournalEntryId { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<PayrollLine> Lines { get; set; } = new List<PayrollLine>();
    [NotMapped] public decimal TotalGross => Lines.Sum(l => l.Gross);
    [NotMapped] public decimal TotalNet => Lines.Sum(l => l.Net);
    [NotMapped] public decimal TotalCost => Lines.Sum(l => l.Gross + l.ErTotal);
}

public class PayrollLine
{
    public int Id { get; set; }
    public int PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [StringLength(100)] public string? Department { get; set; }
    [StringLength(10)] public string ExpenseAccount { get; set; } = "6421";
    public decimal BaseSalary { get; set; }
    public decimal WorkDays { get; set; }
    public decimal SalaryByDays { get; set; }
    public decimal Overtime { get; set; }
    public decimal Allowance { get; set; }
    public decimal NonTaxAllowance { get; set; }
    public decimal Gross { get; set; }
    public decimal InsuranceBase { get; set; }
    public decimal EmpBhxh { get; set; }
    public decimal EmpBhyt { get; set; }
    public decimal EmpBhtn { get; set; }
    public decimal FamilyDeduction { get; set; }
    public decimal TaxableIncome { get; set; }
    public decimal Pit { get; set; }
    public decimal Net { get; set; }
    public decimal ErBhxh { get; set; }
    public decimal ErBhyt { get; set; }
    public decimal ErBhtn { get; set; }
    public decimal ErKpcd { get; set; }
    [NotMapped] public decimal EmpTotal => EmpBhxh + EmpBhyt + EmpBhtn;
    [NotMapped] public decimal ErTotal => ErBhxh + ErBhyt + ErBhtn + ErKpcd;
}

// =====================================================================
//  ĐA TIỀN TỆ
// =====================================================================
public class Currency
{
    public int Id { get; set; }
    [Required, StringLength(3)][Display(Name = "Mã tiền tệ")] public string Code { get; set; } = string.Empty;
    [Required, StringLength(60)][Display(Name = "Tên")] public string Name { get; set; } = string.Empty;
    [Range(0, 4)][Display(Name = "Số chữ số thập phân")] public int Decimals { get; set; } = 2;
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

public class ExchangeRate
{
    public int Id { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    /// <summary>Số VND cho 1 đơn vị ngoại tệ</summary>
    public decimal Rate { get; set; }
}

// =====================================================================
//  HỢP ĐỒNG
// =====================================================================
public enum ContractType
{
    [Display(Name = "Hợp đồng bán")] Sale = 1,
    [Display(Name = "Hợp đồng mua")] Purchase = 2
}
public enum ContractStatus
{
    [Display(Name = "Đang hiệu lực")] Active = 1,
    [Display(Name = "Đã thanh lý")] Completed = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public class Contract : IBranchOwned
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập số hợp đồng"), StringLength(40)][Display(Name = "Số hợp đồng")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Loại")] public ContractType Type { get; set; } = ContractType.Sale;
    [Required(ErrorMessage = "Chọn đối tượng")][Display(Name = "Đối tượng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Required(ErrorMessage = "Nhập tên"), StringLength(300)][Display(Name = "Nội dung hợp đồng")] public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày ký")] public DateTime SignDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày hết hạn")] public DateTime? EndDate { get; set; }
    [Range(0, 1_000_000_000_000)][Display(Name = "Giá trị hợp đồng (gồm thuế)")] public decimal Value { get; set; }
    [Range(0, 365)][Display(Name = "Hạn thanh toán (ngày)")] public int PaymentTermDays { get; set; } = 30;
    [StringLength(500)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [Display(Name = "Trạng thái")] public ContractStatus Status { get; set; } = ContractStatus.Active;
    public int? BranchId { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  BÙ TRỪ CÔNG NỢ
// =====================================================================
public class DebtOffset : IBranchOwned
{
    public int Id { get; set; }
    [StringLength(30)] public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public decimal Amount { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    public DocStatus Status { get; set; } = DocStatus.Posted;
    public int? JournalEntryId { get; set; }
    public int? BranchId { get; set; }
    public int? CreatedById { get; set; }
    public ICollection<DebtOffsetLine> Lines { get; set; } = new List<DebtOffsetLine>();
}

public class DebtOffsetLine
{
    public int Id { get; set; }
    public int DebtOffsetId { get; set; }
    public DebtOffset? DebtOffset { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
}

// =====================================================================
//  SAO KÊ NGÂN HÀNG
// =====================================================================
public class BankStatement : IBranchOwned
{
    public int Id { get; set; }
    public int CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }
    [StringLength(60)] public string Name { get; set; } = string.Empty;
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    [StringLength(40)] public string? Source { get; set; }
    public int? BranchId { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public ICollection<BankStatementLine> Lines { get; set; } = new List<BankStatementLine>();
}

public class BankStatementLine
{
    public int Id { get; set; }
    public int BankStatementId { get; set; }
    public BankStatement? BankStatement { get; set; }
    public DateTime Date { get; set; }
    [StringLength(500)] public string Description { get; set; } = string.Empty;
    [StringLength(60)] public string? Reference { get; set; }
    /// <summary>Dương = tiền vào, âm = tiền ra</summary>
    public decimal Amount { get; set; }
    public int? CashVoucherId { get; set; }
    public CashVoucher? CashVoucher { get; set; }
    /// <summary>auto / manual / created</summary>
    [StringLength(20)] public string? MatchType { get; set; }
}

// =====================================================================
//  TÀI SẢN: ĐIỀU CHUYỂN, CÔNG CỤ DỤNG CỤ
// =====================================================================
public class AssetTransfer
{
    public int Id { get; set; }
    public int FixedAssetId { get; set; }
    public FixedAsset? FixedAsset { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    [StringLength(100)] public string? FromDepartment { get; set; }
    [StringLength(100)] public string? ToDepartment { get; set; }
    public int? FromBranchId { get; set; }
    public int? ToBranchId { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    [StringLength(150)] public string? CreatedBy { get; set; }
}

public enum ToolStatus
{
    [Display(Name = "Đang phân bổ")] InUse = 1,
    [Display(Name = "Đã phân bổ hết")] FullyAllocated = 2,
    [Display(Name = "Đã báo hỏng / thanh lý")] Disposed = 3
}

public class ToolItem : IBranchOwned
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(30)][Display(Name = "Mã CCDC")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên công cụ dụng cụ")] public string Name { get; set; } = string.Empty;
    [StringLength(100)][Display(Name = "Bộ phận sử dụng")] public string? Department { get; set; }
    [Range(0.001, 1_000_000)][Display(Name = "Số lượng")] public decimal Quantity { get; set; } = 1;
    [Range(1, 1_000_000_000_000)][Display(Name = "Tổng giá trị")] public decimal Cost { get; set; }
    [Range(1, 36)][Display(Name = "Số kỳ phân bổ (tháng)")] public int Months { get; set; } = 12;
    [DataType(DataType.Date)][Display(Name = "Ngày bắt đầu phân bổ")] public DateTime StartDate { get; set; } = DateTime.Today;
    [Display(Name = "TK chi phí phân bổ")] public int ExpenseAccountId { get; set; }
    public Account? ExpenseAccount { get; set; }
    public decimal Allocated { get; set; }
    public ToolStatus Status { get; set; } = ToolStatus.InUse;
    public int? BranchId { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [NotMapped] public decimal Remaining => Cost - Allocated;
    [NotMapped] public decimal Monthly => Math.Round(Cost / Math.Max(1, Months), 0);
}

public class ToolAllocationRun
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Total { get; set; }
    public int? JournalEntryId { get; set; }
    [StringLength(150)] public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  BÚT TOÁN ĐỊNH KỲ, PHÂN BỔ
// =====================================================================
public class RecurringJournal
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên"), StringLength(150)][Display(Name = "Tên bút toán định kỳ")] public string Name { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập diễn giải"), StringLength(300)][Display(Name = "Diễn giải")] public string Description { get; set; } = string.Empty;
    [Range(1, 12)][Display(Name = "Chu kỳ (tháng)")] public int IntervalMonths { get; set; } = 1;
    [Range(1, 28)][Display(Name = "Ngày hạch toán trong tháng")] public int DayOfMonth { get; set; } = 28;
    [DataType(DataType.Date)][Display(Name = "Kỳ phát sinh tiếp theo")] public DateTime NextDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Kết thúc sau ngày")] public DateTime? EndDate { get; set; }
    [Display(Name = "Tự ghi sổ")] public bool AutoPost { get; set; } = true;
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
    [Display(Name = "Phân bổ theo tỷ lệ")] public bool IsAllocation { get; set; }
    /// <summary>Phân bổ: TK nguồn được ghi Có toàn bộ số dư Nợ phát sinh trong kỳ (VD 627, 242)</summary>
    [Display(Name = "TK nguồn phân bổ")] public int? SourceAccountId { get; set; }
    public Account? SourceAccount { get; set; }
    public int? BranchId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public ICollection<RecurringJournalLine> Lines { get; set; } = new List<RecurringJournalLine>();
}

public class RecurringJournalLine
{
    public int Id { get; set; }
    public int RecurringJournalId { get; set; }
    public RecurringJournal? RecurringJournal { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int? PartnerId { get; set; }
    [StringLength(100)] public string? Department { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    /// <summary>Phân bổ: tỷ lệ % của số cần phân bổ</summary>
    public decimal Percent { get; set; }
}

// =====================================================================
//  NGÂN SÁCH
// =====================================================================
public enum BudgetControl
{
    [Display(Name = "Chỉ cảnh báo")] Warn = 1,
    [Display(Name = "Chặn ghi sổ khi vượt")] Block = 2
}

public class Budget : IBranchOwned
{
    public int Id { get; set; }
    [Range(2000, 2100)][Display(Name = "Năm")] public int Year { get; set; } = DateTime.Today.Year;
    [Required(ErrorMessage = "Nhập tên"), StringLength(150)][Display(Name = "Tên ngân sách")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Chi nhánh (trống = toàn công ty)")] public int? BranchId { get; set; }
    public Branch? Branch { get; set; }
    [Display(Name = "Kiểm soát")] public BudgetControl Control { get; set; } = BudgetControl.Warn;
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
    public ICollection<BudgetLine> Lines { get; set; } = new List<BudgetLine>();
}

public class BudgetLine
{
    public int Id { get; set; }
    public int BudgetId { get; set; }
    public Budget? Budget { get; set; }
    /// <summary>Khoản mục: đầu số tài khoản (VD 641, 6427, 627)</summary>
    [Required, StringLength(10)] public string AccountPrefix { get; set; } = string.Empty;
    [StringLength(100)] public string? Department { get; set; }
    [StringLength(150)] public string? Note { get; set; }
    public decimal M01 { get; set; } public decimal M02 { get; set; } public decimal M03 { get; set; } public decimal M04 { get; set; }
    public decimal M05 { get; set; } public decimal M06 { get; set; } public decimal M07 { get; set; } public decimal M08 { get; set; }
    public decimal M09 { get; set; } public decimal M10 { get; set; } public decimal M11 { get; set; } public decimal M12 { get; set; }
    public decimal Month(int m) => m switch { 1 => M01, 2 => M02, 3 => M03, 4 => M04, 5 => M05, 6 => M06, 7 => M07, 8 => M08, 9 => M09, 10 => M10, 11 => M11, _ => M12 };
    public void SetMonth(int m, decimal v) { switch (m) { case 1: M01 = v; break; case 2: M02 = v; break; case 3: M03 = v; break; case 4: M04 = v; break; case 5: M05 = v; break; case 6: M06 = v; break; case 7: M07 = v; break; case 8: M08 = v; break; case 9: M09 = v; break; case 10: M10 = v; break; case 11: M11 = v; break; default: M12 = v; break; } }
    [NotMapped] public decimal Year => Enumerable.Range(1, 12).Sum(Month);
}

// =====================================================================
//  THUẾ: TỜ KHAI GTGT, TNDN
// =====================================================================
public class VatDeclaration : IBranchOwned
{
    public int Id { get; set; }
    public int Year { get; set; }
    /// <summary>Kỳ: tháng (1–12) hoặc quý (13–16 = Q1–Q4)</summary>
    public int Period { get; set; }
    public int? BranchId { get; set; }
    public decimal C22 { get; set; } public decimal C23 { get; set; } public decimal C24 { get; set; } public decimal C25 { get; set; }
    public decimal C26 { get; set; } public decimal C29 { get; set; } public decimal C30 { get; set; } public decimal C31 { get; set; }
    public decimal C32 { get; set; } public decimal C33 { get; set; } public decimal C37 { get; set; } public decimal C38 { get; set; }
    public decimal C40a { get; set; } public decimal C41 { get; set; } public decimal C43 { get; set; }
    public int? JournalEntryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [StringLength(150)] public string? CreatedBy { get; set; }
}

public class CitDeclaration : IBranchOwned
{
    public int Id { get; set; }
    public int Year { get; set; }
    /// <summary>1–4 = tạm tính quý, 0 = quyết toán năm</summary>
    public int Quarter { get; set; }
    public int? BranchId { get; set; }
    public decimal AccountingProfit { get; set; }
    public decimal NonDeductible { get; set; }
    public decimal ExemptIncome { get; set; }
    public decimal LossCarried { get; set; }
    public decimal TaxableIncome { get; set; }
    public decimal Rate { get; set; }
    public decimal Tax { get; set; }
    public decimal PaidEarlier { get; set; }
    public decimal Payable { get; set; }
    public int? JournalEntryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [StringLength(150)] public string? CreatedBy { get; set; }
}

// =====================================================================
//  CHUẨN MỰC KẾ TOÁN: ÁNH XẠ TÀI KHOẢN
// =====================================================================
public class AccountMapping
{
    public int Id { get; set; }
    /// <summary>TT133, TT99, IFRS...</summary>
    [Required, StringLength(20)] public string Standard { get; set; } = string.Empty;
    /// <summary>Đầu số TK theo hệ thống đang dùng (TT99)</summary>
    [Required, StringLength(10)] public string FromPrefix { get; set; } = string.Empty;
    [Required, StringLength(10)] public string ToCode { get; set; } = string.Empty;
    [Required, StringLength(200)] public string ToName { get; set; } = string.Empty;
    /// <summary>-1: đổi dấu khi chuyển (VD 521 → giảm 511)</summary>
    public int Sign { get; set; } = 1;
}

// =====================================================================
//  TÍCH HỢP
// =====================================================================
public class ApiClient
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)][Display(Name = "Tên ứng dụng / đối tác")] public string Name { get; set; } = string.Empty;
    [StringLength(12)] public string KeyPrefix { get; set; } = string.Empty;
    [StringLength(100)] public string KeyHash { get; set; } = string.Empty;
    [StringLength(300)][Display(Name = "Quyền (scope)")] public string Scopes { get; set; } = string.Empty;
    [Display(Name = "Đang hoạt động")] public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastUsedAt { get; set; }
    public int CallCount { get; set; }
}

public enum EInvoiceStatus
{
    [Display(Name = "Chưa gửi")] None = 0,
    [Display(Name = "Đã cấp mã CQT")] Issued = 1,
    [Display(Name = "Đã thông báo sai sót / hủy")] Cancelled = 2,
    [Display(Name = "Có hóa đơn điều chỉnh")] Adjusted = 3,
    [Display(Name = "Lỗi gửi")] Error = 9
}

/// <summary>Danh sách hóa đơn tải từ cổng thuế (hoadondientu.gdt.gov.vn) để đối chiếu</summary>
public class TaxPortalInvoice
{
    public int Id { get; set; }
    public InvoiceDirection Direction { get; set; }
    [StringLength(20)] public string? Series { get; set; }
    [StringLength(20)] public string InvoiceNo { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    [StringLength(20)] public string? PartnerTaxCode { get; set; }
    [StringLength(200)] public string? PartnerName { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  MỞ RỘNG THỰC THỂ HIỆN CÓ
// =====================================================================
public partial class AppUser
{
    [Display(Name = "Chi nhánh làm việc")] public int? BranchId { get; set; }
    public Branch? Branch { get; set; }
    [Display(Name = "Được xem mọi chi nhánh")] public bool AllBranches { get; set; } = true;
    [StringLength(5)] public string? Language { get; set; }
}

public partial class Partner
{
    [Display(Name = "Là công ty trong tập đoàn")] public int? GroupCompanyId { get; set; }
    public Company? GroupCompany { get; set; }
    [StringLength(30)][Display(Name = "Số tài khoản ngân hàng")] public string? BankAccountNo { get; set; }
    [StringLength(150)][Display(Name = "Ngân hàng")] public string? BankName { get; set; }
}

public partial class JournalEntry : IBranchOwned { public int? BranchId { get; set; } public Branch? Branch { get; set; } }
public partial class JournalLine { [StringLength(100)] public string? Department { get; set; } }
public partial class PurchaseOrder : IBranchOwned { public int? BranchId { get; set; } public int? ContractId { get; set; } public Contract? Contract { get; set; } }
public partial class SalesOrder : IBranchOwned { public int? BranchId { get; set; } public int? ContractId { get; set; } public Contract? Contract { get; set; } }
public partial class InventoryDoc : IBranchOwned { public int? BranchId { get; set; } }
public partial class ProductionOrder : IBranchOwned { public int? BranchId { get; set; } public decimal AllocatedOverhead { get; set; } }
public partial class FixedAsset : IBranchOwned { public int? BranchId { get; set; } }
public partial class Warehouse : IBranchOwned { public int? BranchId { get; set; } }
public partial class SalesCreditNote : IBranchOwned { public int? BranchId { get; set; } [StringLength(60)] public string? EInvoiceLookupCode { get; set; } }
public partial class CashAccount : IBranchOwned { public int? BranchId { get; set; } [StringLength(3)] public string CurrencyCode { get; set; } = "VND"; }
public partial class CashVoucher : IBranchOwned
{
    public int? BranchId { get; set; }
    [StringLength(3)] public string CurrencyCode { get; set; } = "VND";
    public decimal ExchangeRate { get; set; } = 1;
    public decimal FcAmount { get; set; }
    /// <summary>Lãi (+) / lỗ (−) tỷ giá đã thực hiện khi cấn trừ hóa đơn ngoại tệ</summary>
    public decimal FxDifference { get; set; }
    [NotMapped] public bool IsForeign => CurrencyCode != "VND";
}

public partial class Invoice : IBranchOwned
{
    public int? BranchId { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    [StringLength(3)] public string CurrencyCode { get; set; } = "VND";
    public decimal ExchangeRate { get; set; } = 1;
    public decimal FcSubtotal { get; set; }
    public decimal FcVatAmount { get; set; }
    public decimal FcTotal { get; set; }
    public decimal FcPaid { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày dự kiến thanh toán")] public DateTime? PlannedPayDate { get; set; }
    public EInvoiceStatus EInvoiceStatus { get; set; }
    [StringLength(60)] public string? EInvoiceLookupCode { get; set; }
    [StringLength(60)] public string? EInvoiceTaxCode { get; set; }
    [StringLength(500)] public string? EInvoiceMessage { get; set; }
    public string? EInvoiceXml { get; set; }
    public DateTime? EInvoiceSentAt { get; set; }
    [NotMapped] public bool IsForeign => CurrencyCode != "VND";
    [NotMapped] public decimal FcOutstanding => IsForeign && Status == InvoiceStatus.Issued ? FcTotal - FcPaid : 0;
}

public partial class InvoiceLine
{
    public decimal FcUnitPrice { get; set; }
    public decimal FcAmount { get; set; }
}
public partial class PaymentAllocation { public decimal FcAmount { get; set; } }

/// <summary>Phiếu cân nhận từ thiết bị cân điện tử qua API (dùng đối chiếu khi lập phiếu nhập / xuất kho)</summary>
public class ScaleTicket
{
    public int Id { get; set; }
    [StringLength(40)][Display(Name = "Số phiếu cân")] public string TicketNo { get; set; } = string.Empty;
    [Display(Name = "Thời điểm cân")] public DateTime WeighedAt { get; set; } = DateTime.Now;
    [StringLength(20)][Display(Name = "Biển số xe")] public string? VehicleNo { get; set; }
    [StringLength(30)][Display(Name = "Mã hàng")] public string? ItemCode { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(30)][Display(Name = "Mã đối tác")] public string? PartnerCode { get; set; }
    [StringLength(10)][Display(Name = "Chiều")] public string Direction { get; set; } = "IN";
    [Display(Name = "Cân tổng (kg)")] public decimal GrossKg { get; set; }
    [Display(Name = "Bì (kg)")] public decimal TareKg { get; set; }
    [Display(Name = "Khối lượng hàng (kg)")] public decimal NetKg { get; set; }
    [StringLength(30)][Display(Name = "Chứng từ liên quan")] public string? Reference { get; set; }
    [StringLength(80)][Display(Name = "Thiết bị")] public string? Device { get; set; }
    [StringLength(300)] public string? Note { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
}
