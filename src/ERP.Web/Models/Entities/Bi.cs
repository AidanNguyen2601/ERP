using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  BI: lịch gửi báo cáo qua email, hộp thư đi, quy tắc cảnh báo tự động
// =====================================================================
public enum ScheduleFrequency
{
    [Display(Name = "Hằng ngày")] Daily = 1,
    [Display(Name = "Hằng tuần (thứ Hai)")] Weekly = 2,
    [Display(Name = "Hằng tháng (ngày 1)")] Monthly = 3
}

public enum MailStatus
{
    [Display(Name = "Chờ gửi")] Pending = 1,
    [Display(Name = "Đã gửi")] Sent = 2,
    [Display(Name = "Lỗi")] Failed = 3,
    [Display(Name = "Chưa cấu hình SMTP")] NotConfigured = 4
}

/// <summary>Lịch tự động chạy một báo cáo đã lưu và gửi tệp Excel/CSV qua email</summary>
public class ReportSchedule
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Chọn báo cáo")] public int SavedReportId { get; set; }
    public SavedReport? SavedReport { get; set; }
    [Display(Name = "Tần suất")] public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    [Range(0, 23)][Display(Name = "Giờ gửi")] public int Hour { get; set; } = 7;
    [Required(ErrorMessage = "Nhập email người nhận"), StringLength(500)][Display(Name = "Người nhận (cách nhau bằng dấu phẩy)")] public string Recipients { get; set; } = string.Empty;
    [StringLength(10)][Display(Name = "Định dạng")] public string Format { get; set; } = "xlsx";
    public bool IsActive { get; set; } = true;
    public int OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public DateTime NextRunAt { get; set; } = DateTime.Now;
    public DateTime? LastRunAt { get; set; }
    [StringLength(300)] public string? LastResult { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime NextAfter(DateTime from)
    {
        var d = from.Date.AddHours(Hour);
        switch (Frequency)
        {
            case ScheduleFrequency.Daily: return d > from ? d : d.AddDays(1);
            case ScheduleFrequency.Weekly:
                var add = ((int)DayOfWeek.Monday - (int)d.DayOfWeek + 7) % 7;
                var n = d.AddDays(add); return n > from ? n : n.AddDays(7);
            default:
                var m = new DateTime(from.Year, from.Month, 1).AddHours(Hour); return m > from ? m : m.AddMonths(1);
        }
    }
}

/// <summary>Email đi (báo cáo định kỳ, cảnh báo). Gửi qua SMTP nếu đã cấu hình; nếu chưa thì giữ lại để xem</summary>
public class EmailOutbox
{
    public int Id { get; set; }
    [StringLength(500)] public string ToAddresses { get; set; } = string.Empty;
    [StringLength(300)] public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    [StringLength(150)] public string? AttachmentName { get; set; }
    public byte[]? AttachmentData { get; set; }
    public MailStatus Status { get; set; } = MailStatus.Pending;
    [StringLength(500)] public string? Error { get; set; }
    [StringLength(60)] public string? Source { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SentAt { get; set; }
}

/// <summary>Quy tắc cảnh báo tự động (bật/tắt, ngưỡng, email nhận tổng hợp hằng ngày)</summary>
public class AlertRule
{
    public int Id { get; set; }
    [Required, StringLength(30)] public string Key { get; set; } = string.Empty;
    [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
    [StringLength(300)] public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>Ngưỡng (số ngày, % hoặc số tiền – tùy quy tắc)</summary>
    public decimal Threshold { get; set; }
    [StringLength(30)] public string? ThresholdLabel { get; set; }
    [StringLength(500)] public string? EmailTo { get; set; }
    public DateTime? LastEmailedOn { get; set; }
    public int SortOrder { get; set; }
}
