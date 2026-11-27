using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  SẢN XUẤT NÂNG CAO: quy trình (routing), kế hoạch tổng thể (MPS), sản lượng, gia công ngoài
// =====================================================================
public enum ProductionType
{
    [Display(Name = "Sản xuất theo kho (MTS)")] MakeToStock = 1,
    [Display(Name = "Sản xuất theo đơn hàng (MTO)")] MakeToOrder = 2
}

public enum OperationStatus
{
    [Display(Name = "Chưa làm")] Pending = 1,
    [Display(Name = "Đang làm")] InProgress = 2,
    [Display(Name = "Hoàn thành")] Done = 3
}

public enum OutsourceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã giao NVL")] Sent = 2,
    [Display(Name = "Đã nhận đủ")] Received = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4
}

/// <summary>Trung tâm sản xuất / chuyền / tổ máy: nơi thực hiện công đoạn, có năng lực và đơn giá nhân công, SXC theo giờ</summary>
public class WorkCenter
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã"), StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên"), StringLength(100)][Display(Name = "Tên trung tâm / chuyền")] public string Name { get; set; } = string.Empty;
    [StringLength(100)][Display(Name = "Bộ phận")] public string? Department { get; set; }
    [Range(0, 24)][Display(Name = "Số giờ làm / ngày")] public decimal HoursPerDay { get; set; } = 8;
    [Range(1, 200)][Display(Name = "Hiệu suất (%)")] public decimal Efficiency { get; set; } = 85;
    [Range(1, 1000)][Display(Name = "Số công nhân / ca")] public int Workers { get; set; } = 1;
    [Range(0, 100_000_000)][Display(Name = "Nhân công / giờ")] public decimal LaborRatePerHour { get; set; }
    [Range(0, 100_000_000)][Display(Name = "SXC / giờ")] public decimal OverheadRatePerHour { get; set; }
    [Range(0, 1_000_000)][Display(Name = "Năng suất chuẩn (SP / giờ)")] public decimal UnitsPerHour { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

/// <summary>Công đoạn trong quy trình sản xuất (routing) của một định mức</summary>
public class BomOperation
{
    public int Id { get; set; }
    public int BomId { get; set; }
    public Bom? Bom { get; set; }
    public int Seq { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    public int? WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    /// <summary>Thời gian chuẩn bị máy cho cả lô (phút)</summary>
    public decimal SetupMinutes { get; set; }
    /// <summary>Thời gian chạy máy cho 1 đơn vị sản phẩm (phút)</summary>
    public decimal RunMinutesPerUnit { get; set; }
    /// <summary>Số công nhân tham gia công đoạn (tính nhân công định mức)</summary>
    public int Workers { get; set; } = 1;
    [StringLength(200)] public string? Note { get; set; }
}

public partial class Bom
{
    [Display(Name = "Phiên bản")] public int VersionNo { get; set; } = 1;
    [DataType(DataType.Date)][Display(Name = "Áp dụng từ ngày")] public DateTime? EffectiveFrom { get; set; }
    [DataType(DataType.Date)][Display(Name = "Áp dụng đến ngày")] public DateTime? EffectiveTo { get; set; }
    public ICollection<BomOperation> Operations { get; set; } = new List<BomOperation>();

    public bool EffectiveOn(DateTime d) => IsActive && (EffectiveFrom == null || EffectiveFrom <= d) && (EffectiveTo == null || EffectiveTo >= d);
}

/// <summary>Công đoạn của lệnh sản xuất (sao từ routing của định mức khi lập lệnh) để theo dõi tiến độ</summary>
public class ProductionOperation
{
    public int Id { get; set; }
    public int ProductionOrderId { get; set; }
    public ProductionOrder? ProductionOrder { get; set; }
    public int Seq { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    public int? WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    public decimal PlannedMinutes { get; set; }
    public decimal ActualMinutes { get; set; }
    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>Ghi nhận sản lượng theo ca, chuyền, công đoạn</summary>
public class ProductionOutput : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    public int ProductionOrderId { get; set; }
    public ProductionOrder? ProductionOrder { get; set; }
    public int? ProductionOperationId { get; set; }
    public ProductionOperation? Operation { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    [StringLength(20)] public string Shift { get; set; } = "Ca 1";
    public int? WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    /// <summary>Tổng thời gian máy / người làm việc trong ca cho sản lượng này (phút)</summary>
    public decimal Minutes { get; set; }
    public int Workers { get; set; } = 1;
    [StringLength(20)] public string Source { get; set; } = "Nhập tay";
    public int? ScaleTicketId { get; set; }
    [StringLength(300)] public string? Note { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public partial class ProductionOrder
{
    [Display(Name = "Hình thức sản xuất")] public ProductionType OrderType { get; set; } = ProductionType.MakeToStock;
    /// <summary>Lệnh cha (lệnh thành phẩm đang cần bán thành phẩm do lệnh này sản xuất)</summary>
    public int? ParentOrderId { get; set; }
    public int? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ClosedDate { get; set; }
    public ICollection<ProductionOperation> Operations { get; set; } = new List<ProductionOperation>();
    public ICollection<ProductionOutput> Outputs { get; set; } = new List<ProductionOutput>();
}

// ---------------------------------------------------------------------
//  Kế hoạch sản xuất tổng thể (MPS)
// ---------------------------------------------------------------------
public class MpsPlan : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên kế hoạch"), StringLength(150)][Display(Name = "Tên kế hoạch")] public string Name { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Từ tháng")] public DateTime FromMonth { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [Range(1, 12)][Display(Name = "Số tháng")] public int Months { get; set; } = 3;
    [StringLength(300)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<MpsLine> Lines { get; set; } = new List<MpsLine>();
}

public class MpsLine
{
    public int Id { get; set; }
    public int MpsPlanId { get; set; }
    public MpsPlan? MpsPlan { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int? BomId { get; set; }
    public Bom? Bom { get; set; }
    /// <summary>Đầu tháng của kỳ kế hoạch</summary>
    public DateTime Period { get; set; }
    /// <summary>Dự báo nhu cầu (ngoài đơn hàng đã xác nhận)</summary>
    public decimal ForecastQty { get; set; }
    /// <summary>Số lượng kế hoạch sản xuất trong kỳ</summary>
    public decimal PlannedQty { get; set; }
    public int? ProductionOrderId { get; set; }
    [StringLength(200)] public string? Note { get; set; }
}

// ---------------------------------------------------------------------
//  Gia công ngoài
// ---------------------------------------------------------------------
public class OutsourceOrder : IBranchOwned
{
    public int Id { get; set; }
    public int? BranchId { get; set; }
    [Required, StringLength(30)][Display(Name = "Số phiếu")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày lập")] public DateTime Date { get; set; } = DateTime.Today;
    [Required(ErrorMessage = "Chọn đơn vị gia công")][Display(Name = "Đơn vị gia công")] public int? VendorId { get; set; }
    public Partner? Vendor { get; set; }
    [Required(ErrorMessage = "Chọn định mức")][Display(Name = "Định mức (BOM)")] public int? BomId { get; set; }
    public Bom? Bom { get; set; }
    /// <summary>Thành phẩm / bán thành phẩm nhận về</summary>
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    [Range(0.001, 1_000_000)][Display(Name = "Số lượng gia công")] public decimal Quantity { get; set; } = 1;
    public decimal ReceivedQty { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Phí gia công / đơn vị")] public decimal FeePerUnit { get; set; }
    [Display(Name = "Thuế GTGT (%)")] public int VatRate { get; set; } = 10;
    [Required(ErrorMessage = "Chọn kho xuất NVL")][Display(Name = "Kho xuất NVL")] public int? MaterialWarehouseId { get; set; }
    public Warehouse? MaterialWarehouse { get; set; }
    [Required(ErrorMessage = "Chọn kho nhận")][Display(Name = "Kho nhận hàng")] public int? ReceiveWarehouseId { get; set; }
    public Warehouse? ReceiveWarehouse { get; set; }
    public int? ProductionOrderId { get; set; }
    public ProductionOrder? ProductionOrder { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hạn nhận hàng")] public DateTime ExpectedDate { get; set; } = DateTime.Today.AddDays(10);
    public OutsourceStatus Status { get; set; } = OutsourceStatus.Draft;
    /// <summary>Giá trị NVL đã giao đơn vị gia công (giá vốn bình quân)</summary>
    public decimal MaterialCost { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int? CreatedById { get; set; }
    public ICollection<OutsourceMaterial> Materials { get; set; } = new List<OutsourceMaterial>();
    public ICollection<OutsourceReceipt> Receipts { get; set; } = new List<OutsourceReceipt>();
    [NotMapped] public decimal FeeTotal => Math.Round(Quantity * FeePerUnit, 0);
}

public class OutsourceMaterial
{
    public int Id { get; set; }
    public int OutsourceOrderId { get; set; }
    public OutsourceOrder? OutsourceOrder { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal Cost { get; set; }
}

public class OutsourceReceipt
{
    public int Id { get; set; }
    public int OutsourceOrderId { get; set; }
    public OutsourceOrder? OutsourceOrder { get; set; }
    public DateTime Date { get; set; }
    public decimal Quantity { get; set; }
    public decimal MaterialAmount { get; set; }
    public decimal FeeAmount { get; set; }
    public int? InventoryDocId { get; set; }
    public InventoryDoc? InventoryDoc { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
}
