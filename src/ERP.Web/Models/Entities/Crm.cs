using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  CRM: khách hàng tiềm năng, cơ hội bán hàng, lịch sử tương tác
// =====================================================================
public enum LeadSource
{
    [Display(Name = "Website / biểu mẫu")] Website = 1,
    [Display(Name = "Khách hàng giới thiệu")] Referral = 2,
    [Display(Name = "Triển lãm / sự kiện")] Event = 3,
    [Display(Name = "Gọi điện / thị trường")] Phone = 4,
    [Display(Name = "Mạng xã hội / quảng cáo")] Social = 5,
    [Display(Name = "Đối tác / đại lý")] Partner = 6,
    [Display(Name = "Khác")] Other = 9
}

public enum LeadStage
{
    [Display(Name = "Mới")] New = 1,
    [Display(Name = "Đã liên hệ")] Contacted = 2,
    [Display(Name = "Đạt yêu cầu")] Qualified = 3,
    [Display(Name = "Đã chuyển thành khách hàng")] Converted = 4,
    [Display(Name = "Không phù hợp")] Lost = 5
}

public enum OppStage
{
    [Display(Name = "Tiếp cận")] Prospecting = 1,
    [Display(Name = "Xác định nhu cầu")] Qualified = 2,
    [Display(Name = "Báo giá")] Proposal = 3,
    [Display(Name = "Đàm phán")] Negotiation = 4,
    [Display(Name = "Thắng")] Won = 5,
    [Display(Name = "Thua")] Lost = 6
}

public enum ActivityKind
{
    [Display(Name = "Cuộc gọi")] Call = 1,
    [Display(Name = "Gặp mặt")] Meeting = 2,
    [Display(Name = "Email")] Email = 3,
    [Display(Name = "Chào hàng / báo giá")] Offer = 4,
    [Display(Name = "Công việc cần làm")] Task = 5,
    [Display(Name = "Ghi chú")] Note = 6
}

public class Lead : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên công ty / khách hàng"), StringLength(200)][Display(Name = "Công ty / khách hàng")] public string Name { get; set; } = string.Empty;
    [StringLength(100)][Display(Name = "Người liên hệ")] public string? ContactName { get; set; }
    [StringLength(100)][Display(Name = "Chức vụ")] public string? ContactTitle { get; set; }
    [StringLength(30)][Display(Name = "Điện thoại")] public string? Phone { get; set; }
    [EmailAddress, StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [StringLength(300)][Display(Name = "Địa chỉ")] public string? Address { get; set; }
    [Display(Name = "Nguồn khách hàng")] public LeadSource Source { get; set; } = LeadSource.Other;
    [Display(Name = "Giai đoạn")] public LeadStage Stage { get; set; } = LeadStage.New;
    [Display(Name = "Người phụ trách")] public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Giá trị dự kiến")] public decimal EstimatedValue { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú / nhu cầu")] public string? Note { get; set; }
    /// <summary>Khách hàng đã tạo khi chuyển đổi (hoặc khách hàng hiện có)</summary>
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [StringLength(300)] public string? LostReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastContactAt { get; set; }
    [Display(Name = "Chiến dịch")] public int? CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
}

public class Opportunity : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [StringLength(20)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên cơ hội"), StringLength(200)][Display(Name = "Tên cơ hội")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Khách hàng tiềm năng")] public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    [Display(Name = "Khách hàng")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Display(Name = "Giai đoạn")] public OppStage Stage { get; set; } = OppStage.Prospecting;
    [Range(0, 100_000_000_000)][Display(Name = "Giá trị dự kiến")] public decimal Amount { get; set; }
    [Range(0, 100)][Display(Name = "Xác suất thắng (%)")] public int Probability { get; set; } = 10;
    [DataType(DataType.Date)][Display(Name = "Dự kiến chốt")] public DateTime ExpectedClose { get; set; } = DateTime.Today.AddDays(30);
    [Display(Name = "Người phụ trách")] public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public int? QuoteId { get; set; }
    public SalesQuote? Quote { get; set; }
    public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    [StringLength(300)][Display(Name = "Lý do thua")] public string? LostReason { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ClosedAt { get; set; }
    [NotMapped] public decimal Weighted => Math.Round(Amount * Probability / 100m, 0);
    [NotMapped] public bool IsOpen => Stage is not (OppStage.Won or OppStage.Lost);

    public static int DefaultProbability(OppStage s) => s switch { OppStage.Prospecting => 10, OppStage.Qualified => 30, OppStage.Proposal => 50, OppStage.Negotiation => 75, OppStage.Won => 100, _ => 0 };
}

/// <summary>Tương tác với khách hàng: cuộc gọi, gặp mặt, email, chào hàng, công việc, ghi chú</summary>
public class CrmActivity : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [Display(Name = "Loại")] public ActivityKind Kind { get; set; } = ActivityKind.Call;
    [Required(ErrorMessage = "Nhập nội dung"), StringLength(300)][Display(Name = "Nội dung")] public string Subject { get; set; } = string.Empty;
    /// <summary>Thời điểm diễn ra (hoặc hạn cần làm với công việc)</summary>
    [Display(Name = "Thời điểm")] public DateTime Date { get; set; } = DateTime.Now;
    [Display(Name = "Hoàn thành")] public bool Done { get; set; } = true;
    [StringLength(1000)][Display(Name = "Kết quả / ghi chú")] public string? Outcome { get; set; }
    [Range(0, 1440)][Display(Name = "Thời lượng (phút)")] public int DurationMinutes { get; set; }
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [NotMapped] public bool IsOverdue => Kind == ActivityKind.Task && !Done && Date < DateTime.Now;
}

/// <summary>Tham so cho khung lich su tuong tac dung chung</summary>
public record ActivityPanel(List<CrmActivity> Items, bool CanEdit, int? LeadId, int? PartnerId, int? OppId, string ReturnUrl);
