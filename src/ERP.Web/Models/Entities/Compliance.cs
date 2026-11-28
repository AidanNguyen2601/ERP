using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  Tuân thủ pháp lý: tạm ứng lương, chế độ BHXH, tài khoản ngân hàng nhân viên, hóa đơn thay thế
// =====================================================================
public enum BenefitKind
{
    [Display(Name = "Không")] None = 0,
    [Display(Name = "Ốm đau (BHXH chi trả)")] Sickness = 1,
    [Display(Name = "Thai sản (BHXH chi trả)")] Maternity = 2
}

public enum AdvanceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã chi")] Paid = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public partial class PayrollLine
{
    /// <summary>Tạm ứng lương đã chi trong kỳ (đã ghi Nợ 334 khi chi) – trừ vào thực lĩnh</summary>
    public decimal Advance { get; set; }
}

/// <summary>Tạm ứng lương: chi tiền (Nợ 334 / Có 111, 112), trừ vào bảng lương của kỳ được chọn</summary>
public class SalaryAdvance
{
    public int Id { get; set; }
    [StringLength(20)] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn nhân viên")][Display(Name = "Nhân viên")] public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày chi")] public DateTime Date { get; set; } = DateTime.Today;
    [Range(1, 10_000_000_000, ErrorMessage = "Số tiền phải lớn hơn 0")][Display(Name = "Số tiền")] public decimal Amount { get; set; }
    [Range(2000, 2100)][Display(Name = "Trừ vào lương năm")] public int Year { get; set; } = DateTime.Today.Year;
    [Range(1, 12)][Display(Name = "Tháng")] public int Month { get; set; } = DateTime.Today.Month;
    [Display(Name = "Chi từ quỹ / tài khoản")] public int? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }
    public AdvanceStatus Status { get; set; } = AdvanceStatus.Draft;
    public int? JournalEntryId { get; set; }
    [StringLength(300)][Display(Name = "Lý do")] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public partial class Invoice
{
    /// <summary>Hóa đơn thay thế cho hóa đơn này (theo NĐ 123/2020, NĐ 70/2025): hóa đơn gốc bị hủy khi hóa đơn thay thế được phát hành</summary>
    public int? ReplacesInvoiceId { get; set; }
    [ForeignKey(nameof(ReplacesInvoiceId))] public Invoice? ReplacesInvoice { get; set; }
    /// <summary>XML thông báo hóa đơn điện tử có sai sót (mẫu 04/SS-HĐĐT) lập khi hủy</summary>
    public string? ErrorNoticeXml { get; set; }
}
