using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  Chiều sâu nghiệp vụ: tuyển dụng, đánh giá KPI, đào tạo, cấp phát tài sản, cổng nhân viên,
//  chiến dịch marketing, phiếu hỗ trợ / bảo hành
// =====================================================================

// ------------------------------------------------------------------ tuyển dụng
public enum JobStatus { [Display(Name = "Đang tuyển")] Open = 1, [Display(Name = "Tạm dừng")] OnHold = 2, [Display(Name = "Đã đóng")] Closed = 3 }
public enum CandidateStage
{
    [Display(Name = "Ứng tuyển")] Applied = 1, [Display(Name = "Sàng lọc hồ sơ")] Screening = 2, [Display(Name = "Phỏng vấn")] Interview = 3,
    [Display(Name = "Đề nghị nhận việc")] Offer = 4, [Display(Name = "Đã tuyển")] Hired = 5, [Display(Name = "Không đạt")] Rejected = 6
}

public class JobOpening
{
    public int Id { get; set; }
    [StringLength(20)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập vị trí tuyển dụng"), StringLength(150)][Display(Name = "Vị trí")] public string Title { get; set; } = string.Empty;
    [StringLength(100)][Display(Name = "Phòng ban")] public string? Department { get; set; }
    [Range(1, 1000)][Display(Name = "Số lượng cần tuyển")] public int Quantity { get; set; } = 1;
    [Display(Name = "Lương từ")] public decimal SalaryFrom { get; set; }
    [Display(Name = "Lương đến")] public decimal SalaryTo { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hạn nhận hồ sơ")] public DateTime Deadline { get; set; } = DateTime.Today.AddDays(30);
    [StringLength(2000)][Display(Name = "Mô tả công việc / yêu cầu")] public string? Description { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<Candidate> Candidates { get; set; } = new List<Candidate>();
}

public class Candidate
{
    public int Id { get; set; }
    public int JobOpeningId { get; set; }
    public JobOpening? JobOpening { get; set; }
    [Required(ErrorMessage = "Nhập họ tên"), StringLength(150)][Display(Name = "Họ tên")] public string FullName { get; set; } = string.Empty;
    [StringLength(30)][Display(Name = "Điện thoại")] public string? Phone { get; set; }
    [StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [StringLength(60)][Display(Name = "Nguồn")] public string? Source { get; set; }
    public CandidateStage Stage { get; set; } = CandidateStage.Applied;
    [Range(0, 100)][Display(Name = "Điểm đánh giá")] public int Score { get; set; }
    [Display(Name = "Lương mong muốn")] public decimal ExpectedSalary { get; set; }
    [Display(Name = "Lịch phỏng vấn")] public DateTime? InterviewAt { get; set; }
    [StringLength(1000)][Display(Name = "Nhận xét")] public string? Note { get; set; }
    public int? EmployeeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// ------------------------------------------------------------------ đánh giá KPI
public enum AppraisalStatus { [Display(Name = "Đang chấm")] Draft = 1, [Display(Name = "Đã gửi duyệt")] Submitted = 2, [Display(Name = "Đã chốt")] Approved = 3 }

public class AppraisalCycle
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên kỳ đánh giá"), StringLength(100)][Display(Name = "Kỳ đánh giá")] public string Name { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime FromDate { get; set; } = new(DateTime.Today.Year, 1, 1);
    [DataType(DataType.Date)] public DateTime ToDate { get; set; } = new(DateTime.Today.Year, 12, 31);
    public bool IsClosed { get; set; }
    /// <summary>Thưởng hiệu suất theo xếp loại (% lương cơ bản): A, B, C, D</summary>
    [StringLength(60)] public string BonusPct { get; set; } = "A:100;B:50;C:0;D:0";
    public ICollection<AppraisalCriterion> Criteria { get; set; } = new List<AppraisalCriterion>();
}

public class AppraisalCriterion
{
    public int Id { get; set; }
    public int AppraisalCycleId { get; set; }
    public AppraisalCycle? Cycle { get; set; }
    [Required, StringLength(150)][Display(Name = "Tiêu chí / KPI")] public string Name { get; set; } = string.Empty;
    [StringLength(200)][Display(Name = "Chỉ tiêu")] public string? Target { get; set; }
    [Range(1, 100)][Display(Name = "Trọng số (%)")] public int Weight { get; set; } = 20;
}

public class Appraisal
{
    public int Id { get; set; }
    public int AppraisalCycleId { get; set; }
    public AppraisalCycle? Cycle { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    [StringLength(150)] public string? Reviewer { get; set; }
    public AppraisalStatus Status { get; set; } = AppraisalStatus.Draft;
    /// <summary>Điểm tổng (0–100) = Σ điểm × trọng số</summary>
    public decimal Total { get; set; }
    [StringLength(2)] public string? Rating { get; set; }
    [StringLength(1000)] public string? SelfReview { get; set; }
    [StringLength(1000)] public string? ManagerComment { get; set; }
    public ICollection<AppraisalScore> Scores { get; set; } = new List<AppraisalScore>();
    public static string RatingOf(decimal total) => total >= 90 ? "A" : total >= 75 ? "B" : total >= 50 ? "C" : "D";
}

public class AppraisalScore
{
    public int Id { get; set; }
    public int AppraisalId { get; set; }
    public Appraisal? Appraisal { get; set; }
    public int AppraisalCriterionId { get; set; }
    public AppraisalCriterion? Criterion { get; set; }
    [Range(0, 100)] public int Score { get; set; }
    [StringLength(300)] public string? Comment { get; set; }
}

// ------------------------------------------------------------------ đào tạo
public enum TrainingStatus { [Display(Name = "Đã đăng ký")] Registered = 1, [Display(Name = "Hoàn thành")] Completed = 2, [Display(Name = "Vắng / không đạt")] Failed = 3 }

public class TrainingCourse
{
    public int Id { get; set; }
    [StringLength(20)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên khóa học"), StringLength(200)][Display(Name = "Khóa đào tạo")] public string Title { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Đơn vị đào tạo")] public string? Provider { get; set; }
    [DataType(DataType.Date)][Display(Name = "Từ ngày")] public DateTime FromDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Đến ngày")] public DateTime ToDate { get; set; } = DateTime.Today;
    [Range(0, 10_000)][Display(Name = "Số giờ")] public decimal Hours { get; set; } = 8;
    [Range(0, 10_000_000_000)][Display(Name = "Chi phí")] public decimal Cost { get; set; }
    [Display(Name = "Bắt buộc (an toàn lao động, nghiệp vụ...)")] public bool Mandatory { get; set; }
    [Range(0, 120)][Display(Name = "Hiệu lực chứng chỉ (tháng, 0 = không hết hạn)")] public int ValidMonths { get; set; }
    public ICollection<TrainingAttendee> Attendees { get; set; } = new List<TrainingAttendee>();
}

public class TrainingAttendee
{
    public int Id { get; set; }
    public int TrainingCourseId { get; set; }
    public TrainingCourse? Course { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public TrainingStatus Status { get; set; } = TrainingStatus.Registered;
    [Range(0, 100)] public int? Score { get; set; }
    [StringLength(60)] public string? CertificateNo { get; set; }
    [NotMapped] public DateTime? ExpiresOn => Status == TrainingStatus.Completed && Course?.ValidMonths > 0 ? Course.ToDate.AddMonths(Course.ValidMonths) : null;
}

// ------------------------------------------------------------------ cấp phát tài sản / công cụ cho nhân viên
public class EmployeeAssetIssue
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int? ToolItemId { get; set; }
    public ToolItem? ToolItem { get; set; }
    public int? FixedAssetId { get; set; }
    public FixedAsset? FixedAsset { get; set; }
    [Required(ErrorMessage = "Nhập tên tài sản"), StringLength(200)][Display(Name = "Tài sản / thiết bị")] public string Description { get; set; } = string.Empty;
    [StringLength(60)][Display(Name = "Số serial")] public string? SerialNo { get; set; }
    [Range(0.001, 100_000)][Display(Name = "Số lượng")] public decimal Quantity { get; set; } = 1;
    [DataType(DataType.Date)][Display(Name = "Ngày giao")] public DateTime IssuedDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày thu hồi")] public DateTime? ReturnedDate { get; set; }
    [StringLength(200)][Display(Name = "Tình trạng khi thu hồi")] public string? ReturnCondition { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public partial class AppUser
{
    /// <summary>Nhân viên gắn với tài khoản (cổng nhân viên tự phục vụ)</summary>
    public int? EmployeeId { get; set; }
}

// ------------------------------------------------------------------ CRM: chiến dịch, phiếu hỗ trợ / bảo hành
public enum CampaignChannel
{
    [Display(Name = "Email")] Email = 1, [Display(Name = "Mạng xã hội / quảng cáo")] Social = 2, [Display(Name = "Hội chợ / sự kiện")] Event = 3,
    [Display(Name = "Gọi điện / telesales")] Phone = 4, [Display(Name = "Khuyến mại")] Promotion = 5, [Display(Name = "Khác")] Other = 9
}

public class Campaign
{
    public int Id { get; set; }
    [StringLength(20)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên chiến dịch"), StringLength(200)][Display(Name = "Chiến dịch")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Kênh")] public CampaignChannel Channel { get; set; } = CampaignChannel.Social;
    [DataType(DataType.Date)][Display(Name = "Từ ngày")] public DateTime FromDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Đến ngày")] public DateTime ToDate { get; set; } = DateTime.Today.AddDays(30);
    [Range(0, 100_000_000_000)][Display(Name = "Ngân sách")] public decimal Budget { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí thực tế")] public decimal ActualCost { get; set; }
    [Range(0, 1_000_000)][Display(Name = "Mục tiêu số khách hàng tiềm năng")] public int TargetLeads { get; set; }
    [StringLength(1000)][Display(Name = "Mô tả")] public string? Note { get; set; }
}

public enum TicketCategory { [Display(Name = "Hỏi đáp")] Question = 1, [Display(Name = "Báo lỗi / khiếu nại")] Complaint = 2, [Display(Name = "Bảo hành")] Warranty = 3, [Display(Name = "Lắp đặt / bảo trì")] Installation = 4, [Display(Name = "Khác")] Other = 9 }
public enum TicketPriority { [Display(Name = "Thấp")] Low = 1, [Display(Name = "Bình thường")] Normal = 2, [Display(Name = "Cao")] High = 3, [Display(Name = "Khẩn cấp")] Urgent = 4 }
public enum TicketStatus { [Display(Name = "Mới")] New = 1, [Display(Name = "Đang xử lý")] InProgress = 2, [Display(Name = "Chờ khách phản hồi")] Waiting = 3, [Display(Name = "Đã giải quyết")] Resolved = 4, [Display(Name = "Đã đóng")] Closed = 5 }

public class ServiceTicket
{
    public int Id { get; set; }
    [StringLength(20)] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn khách hàng")][Display(Name = "Khách hàng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Required(ErrorMessage = "Nhập tiêu đề"), StringLength(200)][Display(Name = "Tiêu đề")] public string Subject { get; set; } = string.Empty;
    [StringLength(2000)][Display(Name = "Mô tả")] public string? Description { get; set; }
    [Display(Name = "Loại yêu cầu")] public TicketCategory Category { get; set; } = TicketCategory.Question;
    [Display(Name = "Mức ưu tiên")] public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.New;
    [Display(Name = "Người xử lý")] public int? AssigneeId { get; set; }
    public AppUser? Assignee { get; set; }
    [Display(Name = "Sản phẩm")] public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(60)][Display(Name = "Số serial / lô")] public string? SerialNo { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    /// <summary>Hạn xử lý theo SLA của mức ưu tiên</summary>
    public DateTime DueAt { get; set; }
    public DateTime? FirstResponseAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    [StringLength(2000)][Display(Name = "Kết quả xử lý")] public string? Resolution { get; set; }
    [StringLength(30)][Display(Name = "Kênh tiếp nhận")] public string? Channel { get; set; } = "Điện thoại";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [NotMapped] public bool IsOpen => Status is not (TicketStatus.Resolved or TicketStatus.Closed);
    [NotMapped] public bool IsLate => IsOpen ? DateTime.Now > DueAt : ResolvedAt > DueAt;

    /// <summary>SLA giờ xử lý theo mức ưu tiên</summary>
    public static int SlaHours(TicketPriority p) => p switch { TicketPriority.Urgent => 4, TicketPriority.High => 8, TicketPriority.Normal => 24, _ => 72 };
}
