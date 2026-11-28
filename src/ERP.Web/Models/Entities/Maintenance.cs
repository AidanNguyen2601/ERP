using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  BẢO DƯỠNG THIẾT BỊ: hồ sơ thiết bị, kế hoạch bảo dưỡng định kỳ / TPM, sửa chữa đột xuất,
//  phụ tùng thay thế, lịch sử – thời gian dừng máy – chi phí
// =====================================================================
public enum EquipmentStatus
{
    [Display(Name = "Đang chạy")] Running = 1,
    [Display(Name = "Đang dừng (hỏng)")] Down = 2,
    [Display(Name = "Đang bảo dưỡng")] UnderMaintenance = 3,
    [Display(Name = "Ngưng sử dụng")] Retired = 4
}

public enum MaintenanceKind
{
    [Display(Name = "Bảo dưỡng định kỳ")] Preventive = 1,
    [Display(Name = "Sửa chữa đột xuất")] Breakdown = 2,
    [Display(Name = "TPM – tự bảo dưỡng")] Autonomous = 3,
    [Display(Name = "Kiểm định / hiệu chuẩn")] Calibration = 4
}

public enum MaintenanceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt")] PendingApproval = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Đang thực hiện")] InProgress = 4,
    [Display(Name = "Chờ nghiệm thu")] Completed = 5,
    [Display(Name = "Đã nghiệm thu")] Accepted = 6,
    [Display(Name = "Đã hủy")] Cancelled = 7
}

public enum MaintenancePlanStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt")] PendingApproval = 2,
    [Display(Name = "Đang áp dụng")] Active = 3,
    [Display(Name = "Tạm ngưng")] Suspended = 4
}

public enum Criticality
{
    [Display(Name = "A – Quan trọng (dừng là ngừng sản xuất)")] A = 1,
    [Display(Name = "B – Trung bình")] B = 2,
    [Display(Name = "C – Thứ yếu")] C = 3
}

public enum MaintenancePriority
{
    [Display(Name = "Thấp")] Low = 1,
    [Display(Name = "Bình thường")] Normal = 2,
    [Display(Name = "Khẩn cấp")] Urgent = 3
}

/// <summary>Loại bảo dưỡng: danh mục dùng khi lập kế hoạch (cấp 1, cấp 2, thay dầu, vệ sinh…)</summary>
public class MaintenanceType
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)][Display(Name = "Tên loại bảo dưỡng")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Hình thức")] public MaintenanceKind Kind { get; set; } = MaintenanceKind.Preventive;
    [Range(1, 3650)][Display(Name = "Chu kỳ mặc định (ngày)")] public int DefaultIntervalDays { get; set; } = 30;
    [StringLength(300)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

/// <summary>Hồ sơ thiết bị / máy móc</summary>
public class Equipment : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(30)][Display(Name = "Mã thiết bị")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(200)][Display(Name = "Tên thiết bị")] public string Name { get; set; } = string.Empty;
    [StringLength(100)][Display(Name = "Model")] public string? Model { get; set; }
    [StringLength(100)][Display(Name = "Hãng sản xuất")] public string? Manufacturer { get; set; }
    [StringLength(100)][Display(Name = "Số serial")] public string? SerialNo { get; set; }
    [Display(Name = "Tài sản cố định")] public int? FixedAssetId { get; set; }
    public FixedAsset? FixedAsset { get; set; }
    [Display(Name = "Trung tâm sản xuất / chuyền")] public int? WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    [StringLength(100)][Display(Name = "Bộ phận sử dụng")] public string? Department { get; set; }
    [StringLength(150)][Display(Name = "Vị trí lắp đặt")] public string? Location { get; set; }
    [Display(Name = "Mức độ quan trọng")] public Criticality Criticality { get; set; } = Criticality.B;
    [DataType(DataType.Date)][Display(Name = "Ngày lắp đặt / đưa vào sử dụng")] public DateTime? InstallDate { get; set; }
    [DataType(DataType.Date)][Display(Name = "Bảo hành đến")] public DateTime? WarrantyUntil { get; set; }
    [Display(Name = "TK chi phí bảo dưỡng (để trống = mặc định)")] public int? ExpenseAccountId { get; set; }
    public Account? ExpenseAccount { get; set; }
    [Display(Name = "Trạng thái")] public EquipmentStatus Status { get; set; } = EquipmentStatus.Running;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public ICollection<EquipmentDocument> Documents { get; set; } = new List<EquipmentDocument>();
}

/// <summary>Tài liệu trong hồ sơ thiết bị: hướng dẫn vận hành, chứng nhận kiểm định, bảo hành…</summary>
public class EquipmentDocument
{
    public int Id { get; set; }
    public int EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }
    [Required, StringLength(150)] public string Title { get; set; } = string.Empty;
    [StringLength(40)] public string? DocType { get; set; }
    [StringLength(60)] public string? Number { get; set; }
    [DataType(DataType.Date)] public DateTime? IssuedDate { get; set; }
    [DataType(DataType.Date)] public DateTime? ExpiryDate { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

/// <summary>Kế hoạch bảo dưỡng định kỳ của một thiết bị (cũng dùng cho TPM tự bảo dưỡng và hiệu chuẩn)</summary>
public class MaintenancePlan : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [StringLength(30)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn thiết bị")][Display(Name = "Thiết bị")] public int? EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }
    [Display(Name = "Loại bảo dưỡng")] public int? MaintenanceTypeId { get; set; }
    public MaintenanceType? MaintenanceType { get; set; }
    [Required(ErrorMessage = "Nhập nội dung"), StringLength(200)][Display(Name = "Nội dung bảo dưỡng")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Hình thức")] public MaintenanceKind Kind { get; set; } = MaintenanceKind.Preventive;
    [Range(1, 3650, ErrorMessage = "Chu kỳ 1–3650 ngày")][Display(Name = "Chu kỳ (ngày)")] public int IntervalDays { get; set; } = 30;
    [DataType(DataType.Date)][Display(Name = "Lần đến hạn kế tiếp")] public DateTime NextDueDate { get; set; } = DateTime.Today.AddDays(30);
    [DataType(DataType.Date)][Display(Name = "Lần thực hiện gần nhất")] public DateTime? LastDoneDate { get; set; }
    [Range(0, 10_000)][Display(Name = "Số giờ dự kiến")] public decimal EstimatedHours { get; set; } = 2;
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí dự kiến")] public decimal EstimatedCost { get; set; }
    [Range(0, 3650)][Display(Name = "Tạo phiếu trước hạn (ngày)")] public int LeadDays { get; set; } = 3;
    [StringLength(150)][Display(Name = "Người phụ trách")] public string? Responsible { get; set; }
    [StringLength(2000)][Display(Name = "Danh mục công việc (mỗi dòng một việc)")] public string? Checklist { get; set; }
    [Display(Name = "Trạng thái")] public MaintenancePlanStatus Status { get; set; } = MaintenancePlanStatus.Draft;
    [StringLength(300)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int? CreatedById { get; set; }
    public int? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
    [NotMapped] public bool IsOverdue => Status == MaintenancePlanStatus.Active && NextDueDate.Date < DateTime.Today;
}

/// <summary>Phiếu công việc bảo dưỡng / sửa chữa</summary>
public class MaintenanceOrder : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [StringLength(30)][Display(Name = "Số phiếu")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Hình thức")] public MaintenanceKind Kind { get; set; } = MaintenanceKind.Breakdown;
    [Required(ErrorMessage = "Chọn thiết bị")][Display(Name = "Thiết bị")] public int? EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }
    public int? PlanId { get; set; }
    public MaintenancePlan? Plan { get; set; }
    [Required(ErrorMessage = "Nhập nội dung"), StringLength(300)][Display(Name = "Nội dung / yêu cầu")] public string Title { get; set; } = string.Empty;
    [StringLength(1000)][Display(Name = "Hiện tượng hỏng")] public string? Symptom { get; set; }
    [StringLength(1000)][Display(Name = "Nguyên nhân hỏng")] public string? Cause { get; set; }
    [StringLength(1000)][Display(Name = "Biện pháp xử lý / kết quả")] public string? Resolution { get; set; }
    [Display(Name = "Mức ưu tiên")] public MaintenancePriority Priority { get; set; } = MaintenancePriority.Normal;
    [Display(Name = "Trạng thái")] public MaintenanceStatus Status { get; set; } = MaintenanceStatus.Draft;
    [DataType(DataType.Date)][Display(Name = "Ngày đến hạn / cần xử lý")] public DateTime DueDate { get; set; } = DateTime.Today;
    [Display(Name = "Thời điểm báo hỏng")] public DateTime ReportedAt { get; set; } = DateTime.Now;
    [StringLength(150)][Display(Name = "Người yêu cầu")] public string? RequestedBy { get; set; }
    [StringLength(150)][Display(Name = "Người thực hiện")] public string? Technician { get; set; }
    [Display(Name = "Kho xuất phụ tùng")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Thời gian máy dừng (giờ): từ lúc báo hỏng đến khi sửa xong hoặc theo thực tế nhập</summary>
    [Range(0, 100_000)][Display(Name = "Thời gian dừng máy (giờ)")] public decimal DowntimeHours { get; set; }
    [Range(0, 100_000)][Display(Name = "Giờ công")] public decimal LaborHours { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí nhân công")] public decimal LaborCost { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí thuê ngoài")] public decimal OutsideCost { get; set; }
    [Display(Name = "Chi phí phụ tùng (xuất kho)")] public decimal PartsCost { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Chi phí dự kiến")] public decimal EstimatedCost { get; set; }
    [StringLength(150)][Display(Name = "Người nghiệm thu")] public string? AcceptedBy { get; set; }
    public DateTime? AcceptedAt { get; set; }
    [StringLength(500)][Display(Name = "Ý kiến nghiệm thu")] public string? AcceptanceNote { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<MaintenancePart> Parts { get; set; } = new List<MaintenancePart>();
    public ICollection<MaintenanceTask> Tasks { get; set; } = new List<MaintenanceTask>();
    [NotMapped] public decimal TotalCost => PartsCost + LaborCost + OutsideCost;
    [NotMapped] public bool IsOpen => Status is not (MaintenanceStatus.Accepted or MaintenanceStatus.Cancelled);
}

/// <summary>Phụ tùng thay thế dự kiến và thực xuất cho phiếu</summary>
public class MaintenancePart
{
    public int Id { get; set; }
    public int MaintenanceOrderId { get; set; }
    public MaintenanceOrder? Order { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal PlannedQty { get; set; }
    public decimal IssuedQty { get; set; }
    /// <summary>Phụ tùng chưa dùng, trả lại kho</summary>
    public decimal ReturnedQty { get; set; }
    /// <summary>Phụ tùng cũ / hỏng thu hồi (theo dõi số lượng, không hạch toán)</summary>
    public decimal RecoveredOldQty { get; set; }
    public decimal Cost { get; set; }
    [NotMapped] public decimal UsedQty => IssuedQty - ReturnedQty;
}

/// <summary>Đầu việc / hạng mục kiểm tra trong phiếu (checklist bảo dưỡng, TPM)</summary>
public class MaintenanceTask
{
    public int Id { get; set; }
    public int MaintenanceOrderId { get; set; }
    public MaintenanceOrder? Order { get; set; }
    public int Seq { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    public bool Done { get; set; }
    [StringLength(300)] public string? Result { get; set; }
}
