using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  QUẢN LÝ CHẤT LƯỢNG NÂNG CAO: lấy mẫu AQL, ngưỡng đạt, kiểm tra theo công đoạn / lô,
//  giấy chứng nhận chất lượng, cách ly, tái chế và hành động khắc phục (CAPA)
// =====================================================================
public enum QcSamplingMethod
{
    [Display(Name = "Theo tỷ lệ % của lô")] Percent = 1,
    [Display(Name = "Theo AQL (ISO 2859-1, mức II, lấy mẫu một lần)")] Aql = 2,
    [Display(Name = "Số lượng mẫu cố định")] Fixed = 3
}

public enum QcActionKind
{
    [Display(Name = "Khắc phục")] Corrective = 1,
    [Display(Name = "Phòng ngừa")] Preventive = 2
}

public enum QcActionStatus
{
    [Display(Name = "Đang thực hiện")] Open = 1,
    [Display(Name = "Hoàn thành")] Done = 2
}

public partial class QcPlan
{
    [Display(Name = "Cách lấy mẫu")] public QcSamplingMethod SamplingMethod { get; set; } = QcSamplingMethod.Percent;
    /// <summary>Mức chất lượng chấp nhận AQL (%): 0.65 / 1.0 / 1.5 / 2.5 / 4.0</summary>
    [Display(Name = "AQL (%)")] public decimal? AqlValue { get; set; }
    [Range(0, 100_000_000)][Display(Name = "Số lượng mẫu cố định")] public decimal FixedSampleQty { get; set; }
    /// <summary>Ngưỡng lô đạt: tỷ lệ lỗi trong mẫu tối đa (%). 0 = không áp dụng</summary>
    [Range(0, 100)][Display(Name = "Ngưỡng đạt: tỷ lệ lỗi tối đa (%)")] public decimal MaxDefectPct { get; set; }
    /// <summary>Công đoạn sản xuất áp dụng (kiểm tra trong quá trình theo công đoạn); trống = mọi công đoạn</summary>
    [StringLength(100)][Display(Name = "Công đoạn sản xuất áp dụng")] public string? OperationName { get; set; }
}

public partial class QcInspection
{
    [Range(0, 100_000_000)][Display(Name = "SL mẫu lỗi")] public decimal DefectQty { get; set; }
    /// <summary>Số lỗi tối đa trong mẫu để chấp nhận lô / số lỗi từ đó bắt buộc loại lô (theo AQL)</summary>
    public int? AcceptNumber { get; set; }
    public int? RejectNumber { get; set; }
    [StringLength(60)][Display(Name = "Số lô / serial")] public string? LotNo { get; set; }
    public int? ProductionOperationId { get; set; }
    [StringLength(100)][Display(Name = "Công đoạn sản xuất")] public string? OperationName { get; set; }
    [StringLength(30)][Display(Name = "Số giấy chứng nhận")] public string? CertificateNo { get; set; }
    public DateTime? CertificateDate { get; set; }
    public int? CertificateById { get; set; }
}

public partial class QcNonconformance
{
    /// <summary>Kho đang giữ hàng trước khi cách ly</summary>
    public int? OriginalWarehouseId { get; set; }
    public int? QuarantineDocId { get; set; }
    public DateTime? QuarantinedAt { get; set; }
    public int? ReleaseDocId { get; set; }
    public DateTime? ReleasedAt { get; set; }
    /// <summary>Phiếu kiểm tra lại sau tái chế / sửa chữa</summary>
    public int? ReinspectionId { get; set; }
    [NotMapped] public bool IsQuarantined => QuarantinedAt != null && ReleasedAt == null;
}

/// <summary>Hành động khắc phục / phòng ngừa (CAPA) của báo cáo không phù hợp</summary>
public class QcNcrAction
{
    public int Id { get; set; }
    public int NcrId { get; set; }
    public QcNonconformance? Ncr { get; set; }
    public QcActionKind Kind { get; set; } = QcActionKind.Corrective;
    [Required, StringLength(500)] public string Description { get; set; } = string.Empty;
    [StringLength(150)] public string? Responsible { get; set; }
    [DataType(DataType.Date)] public DateTime? DueDate { get; set; }
    public QcActionStatus Status { get; set; } = QcActionStatus.Open;
    public DateTime? DoneAt { get; set; }
    [StringLength(300)] public string? Note { get; set; }
    [NotMapped] public bool IsOverdue => Status == QcActionStatus.Open && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
}
