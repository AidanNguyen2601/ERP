using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  NHÂN SỰ NÂNG CAO: hồ sơ, hợp đồng, quá trình công tác, ca làm việc, chấm công ngày, nghỉ phép,
//  thành phần lương linh hoạt, thưởng, lương sản phẩm
// =====================================================================
public enum LaborContractType
{
    [Display(Name = "Thử việc")] Probation = 1,
    [Display(Name = "Xác định thời hạn")] FixedTerm = 2,
    [Display(Name = "Không xác định thời hạn")] Indefinite = 3,
    [Display(Name = "Thời vụ / dưới 12 tháng")] Seasonal = 4
}

public enum LaborContractStatus
{
    [Display(Name = "Đang hiệu lực")] Active = 1,
    [Display(Name = "Đã hết hạn")] Expired = 2,
    [Display(Name = "Đã chấm dứt")] Terminated = 3
}

public enum HistoryKind
{
    [Display(Name = "Tuyển dụng")] Hired = 1,
    [Display(Name = "Điều chuyển bộ phận")] Transfer = 2,
    [Display(Name = "Bổ nhiệm / thay đổi chức danh")] Promotion = 3,
    [Display(Name = "Điều chỉnh lương")] SalaryChange = 4,
    [Display(Name = "Khen thưởng")] Reward = 5,
    [Display(Name = "Kỷ luật")] Discipline = 6,
    [Display(Name = "Đào tạo")] Training = 7,
    [Display(Name = "Nghỉ việc")] Resigned = 8,
    [Display(Name = "Khác")] Other = 9
}

public enum Gender { [Display(Name = "Nam")] Male = 1, [Display(Name = "Nữ")] Female = 2, [Display(Name = "Khác")] Other = 3 }

public enum LeaveStatus
{
    [Display(Name = "Chờ duyệt")] Pending = 1,
    [Display(Name = "Đã duyệt")] Approved = 2,
    [Display(Name = "Từ chối")] Rejected = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4
}

public enum AttendanceKind
{
    [Display(Name = "Đi làm")] Present = 1,
    [Display(Name = "Vắng không phép")] Absent = 2,
    [Display(Name = "Nghỉ phép")] Leave = 3,
    [Display(Name = "Nghỉ lễ / nghỉ tuần")] Off = 4
}

public enum ComponentKind
{
    [Display(Name = "Khoản thu nhập (cộng)")] Earning = 1,
    [Display(Name = "Khoản khấu trừ (trừ sau thuế)")] Deduction = 2
}

public partial class Employee
{
    [DataType(DataType.Date)][Display(Name = "Ngày sinh")] public DateTime? DateOfBirth { get; set; }
    [Display(Name = "Giới tính")] public Gender? Gender { get; set; }
    [StringLength(30)][Display(Name = "Điện thoại")] public string? Phone { get; set; }
    [StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [StringLength(300)][Display(Name = "Địa chỉ thường trú")] public string? Address { get; set; }
    [StringLength(60)][Display(Name = "Tình trạng hôn nhân")] public string? MaritalStatus { get; set; }
    [StringLength(100)][Display(Name = "Trình độ học vấn")] public string? Education { get; set; }
    [StringLength(200)][Display(Name = "Người liên hệ khẩn cấp")] public string? EmergencyContact { get; set; }
    [StringLength(30)][Display(Name = "Số sổ BHXH")] public string? InsuranceBookNo { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày nghỉ việc")] public DateTime? ResignDate { get; set; }
    [Range(0, 60)][Display(Name = "Số ngày phép năm")] public decimal AnnualLeaveDays { get; set; } = 12;
    [Display(Name = "Ca làm việc mặc định")] public int? DefaultShiftId { get; set; }
}

/// <summary>Hợp đồng lao động</summary>
public class LaborContract
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [Required(ErrorMessage = "Nhập số hợp đồng"), StringLength(40)][Display(Name = "Số hợp đồng")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Loại hợp đồng")] public LaborContractType Type { get; set; } = LaborContractType.FixedTerm;
    [DataType(DataType.Date)][Display(Name = "Ngày ký")] public DateTime SignedDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Có hiệu lực từ")] public DateTime StartDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hết hạn")] public DateTime? EndDate { get; set; }
    [StringLength(100)][Display(Name = "Chức danh")] public string? Position { get; set; }
    [Range(0, 10_000_000_000)][Display(Name = "Mức lương")] public decimal Salary { get; set; }
    [Display(Name = "Trạng thái")] public LaborContractStatus Status { get; set; } = LaborContractStatus.Active;
    [StringLength(300)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [NotMapped] public int? DaysLeft => Status == LaborContractStatus.Active && EndDate.HasValue ? (int)(EndDate.Value.Date - DateTime.Today).TotalDays : null;
}

/// <summary>Quá trình công tác: điều chuyển, bổ nhiệm, điều chỉnh lương, khen thưởng, kỷ luật…</summary>
public class EmployeeHistory
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    public HistoryKind Kind { get; set; } = HistoryKind.Other;
    [StringLength(200)] public string? FromValue { get; set; }
    [StringLength(200)] public string? ToValue { get; set; }
    [StringLength(500)] public string? Note { get; set; }
}

/// <summary>Ca làm việc</summary>
public class Shift
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã ca")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)][Display(Name = "Tên ca")] public string Name { get; set; } = string.Empty;
    /// <summary>Giờ vào / ra (phút từ 00:00)</summary>
    [Range(0, 1439)] public int StartMinute { get; set; } = 8 * 60;
    [Range(0, 1439)] public int EndMinute { get; set; } = 17 * 60;
    [Range(0, 480)][Display(Name = "Nghỉ giữa ca (phút)")] public int BreakMinutes { get; set; } = 60;
    [Range(0, 120)][Display(Name = "Cho phép đi trễ (phút)")] public int GraceMinutes { get; set; } = 5;
    [Display(Name = "Ca đêm (qua ngày)")] public bool IsNight { get; set; }
    [Display(Name = "Hệ số làm thêm giờ")] public decimal OtRate { get; set; } = 1.5m;
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
    [NotMapped] public decimal PaidHours => Math.Round((((EndMinute - StartMinute) + (EndMinute <= StartMinute ? 1440 : 0)) - BreakMinutes) / 60m, 2);
    [NotMapped] public string Time => $"{StartMinute / 60:00}:{StartMinute % 60:00} – {EndMinute / 60:00}:{EndMinute % 60:00}";
}

/// <summary>Lượt quẹt thô từ máy chấm công (đẩy qua API hoặc nhập file)</summary>
public class TimeClockPunch
{
    public int Id { get; set; }
    [Required, StringLength(30)] public string EmployeeCode { get; set; } = string.Empty;
    public DateTime PunchTime { get; set; }
    [StringLength(80)] public string? Device { get; set; }
    public bool Processed { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
}

/// <summary>Chấm công theo ngày</summary>
public class AttendanceDay
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; }
    public int? ShiftId { get; set; }
    public Shift? Shift { get; set; }
    public AttendanceKind Kind { get; set; } = AttendanceKind.Present;
    public DateTime? CheckIn { get; set; }
    public DateTime? CheckOut { get; set; }
    public decimal WorkedHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyLeaveMinutes { get; set; }
    [StringLength(40)] public string? Source { get; set; }
    [StringLength(200)] public string? Note { get; set; }
}

public class LeaveType
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)] public string Name { get; set; } = string.Empty;
    /// <summary>Nghỉ hưởng lương: tính vào ngày công</summary>
    public bool IsPaid { get; set; } = true;
    /// <summary>Trừ vào số ngày phép năm</summary>
    public bool DeductsAnnualBalance { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LeaveRequest
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int LeaveTypeId { get; set; }
    public LeaveType? LeaveType { get; set; }
    [DataType(DataType.Date)] public DateTime FromDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)] public DateTime ToDate { get; set; } = DateTime.Today;
    public decimal Days { get; set; } = 1;
    [StringLength(300)] public string? Reason { get; set; }
    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;
    [StringLength(150)] public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(300)] public string? DecisionNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Thành phần lương cấu hình được: phụ cấp / thưởng / khấu trừ, có thể tính theo công thức</summary>
public class SalaryComponent
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)][Display(Name = "Tên khoản")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Loại")] public ComponentKind Kind { get; set; } = ComponentKind.Earning;
    [Display(Name = "Chịu thuế TNCN")] public bool Taxable { get; set; } = true;
    /// <summary>Công thức: BaseSalary, WorkDays, StdDays, OvertimeHours, Dependents, Years, InsuranceSalary, SalaryByDays; hàm MIN, MAX, ROUND, IF. Để trống = số tiền cố định theo từng nhân viên</summary>
    [StringLength(300)][Display(Name = "Công thức")] public string? Formula { get; set; }
    [StringLength(300)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

public class EmployeeSalaryComponent
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int SalaryComponentId { get; set; }
    public SalaryComponent? Component { get; set; }
    /// <summary>Số tiền cố định (khi khoản không có công thức) hoặc hệ số nhân cho công thức (trống = 1)</summary>
    public decimal Amount { get; set; }
}

/// <summary>Thưởng / khoản điều chỉnh theo tháng</summary>
public class PayrollBonus
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    [Required, StringLength(200)] public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool Taxable { get; set; } = true;
}

/// <summary>Lương sản phẩm: số lượng sản phẩm × đơn giá</summary>
public class PieceRateEntry
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(200)] public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    [NotMapped] public decimal Amount => Math.Round(Quantity * Rate, 0);
}

public partial class PayrollLine
{
    /// <summary>Phụ cấp / khoản thu nhập theo thành phần lương, thưởng, lương sản phẩm (chịu thuế / không chịu thuế)</summary>
    public decimal ExtraTaxable { get; set; }
    public decimal ExtraNonTax { get; set; }
    public decimal Bonus { get; set; }
    public decimal PieceRate { get; set; }
    /// <summary>Khấu trừ khác sau thuế (tạm ứng, đoàn phí…)</summary>
    public decimal OtherDeduction { get; set; }
    [StringLength(600)] public string? ExtraNote { get; set; }
}
