using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Web.Models.Entities;

// =====================================================================
//  MUA HÀNG: ĐỀ NGHỊ MUA, YÊU CẦU BÁO GIÁ, ĐÁNH GIÁ NCC, CHI PHÍ MUA HÀNG
// =====================================================================
public enum RequestStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt")] PendingApproval = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Đã lập đơn mua / báo giá")] Converted = 4,
    [Display(Name = "Đã hủy")] Cancelled = 5
}

public class PurchaseRequisition : IBranchOwned
{
    public int Id { get; set; }
    [StringLength(30)][Display(Name = "Số đề nghị")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày đề nghị")] public DateTime Date { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Cần hàng trước ngày")] public DateTime RequiredDate { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(100)][Display(Name = "Bộ phận đề nghị")] public string? Department { get; set; }
    [StringLength(300)][Display(Name = "Mục đích")] public string? Purpose { get; set; }
    [StringLength(30)][Display(Name = "Nguồn")] public string Source { get; set; } = "Thủ công";
    [Display(Name = "Trạng thái")] public RequestStatus Status { get; set; } = RequestStatus.Draft;
    public int? RequesterId { get; set; }
    public AppUser? Requester { get; set; }
    public int? BranchId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<PurchaseRequisitionLine> Lines { get; set; } = new List<PurchaseRequisitionLine>();
    [NotMapped] public decimal Estimated => Lines.Sum(l => l.Quantity * l.EstimatedPrice);
}

public class PurchaseRequisitionLine
{
    public int Id { get; set; }
    public int PurchaseRequisitionId { get; set; }
    public PurchaseRequisition? PurchaseRequisition { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    [Display(Name = "Đơn giá dự kiến")] public decimal EstimatedPrice { get; set; }
    /// <summary>Số lượng đã đặt hàng (đã lập đơn mua)</summary>
    public decimal OrderedQty { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public enum RfqStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã gửi nhà cung cấp")] Sent = 2,
    [Display(Name = "Đã chọn nhà cung cấp")] Awarded = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4
}

public class Rfq
{
    public int Id { get; set; }
    [StringLength(30)][Display(Name = "Số RFQ")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hạn nhận báo giá")] public DateTime DueDate { get; set; } = DateTime.Today.AddDays(5);
    public int? PurchaseRequisitionId { get; set; }
    public PurchaseRequisition? PurchaseRequisition { get; set; }
    [Display(Name = "Trạng thái")] public RfqStatus Status { get; set; } = RfqStatus.Draft;
    [StringLength(500)][Display(Name = "Yêu cầu / điều kiện")] public string? Note { get; set; }
    public int? AwardedPartnerId { get; set; }
    public Partner? AwardedPartner { get; set; }
    public int? PurchaseOrderId { get; set; }
    public int? CreatedById { get; set; }
    public ICollection<RfqLine> Lines { get; set; } = new List<RfqLine>();
    public ICollection<RfqVendor> Vendors { get; set; } = new List<RfqVendor>();
    public ICollection<RfqQuote> Quotes { get; set; } = new List<RfqQuote>();
}

public class RfqLine
{
    public int Id { get; set; }
    public int RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    public int? RequisitionLineId { get; set; }
}

public class RfqVendor
{
    public int Id { get; set; }
    public int RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [Display(Name = "Điều kiện thanh toán / giao hàng")][StringLength(300)] public string? Terms { get; set; }
    [Display(Name = "Số ngày giao hàng")] public int LeadDays { get; set; }
    public bool Responded { get; set; }
}

public class RfqQuote
{
    public int Id { get; set; }
    public int RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    public int RfqLineId { get; set; }
    public RfqLine? RfqLine { get; set; }
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    public decimal UnitPrice { get; set; }
}

public class SupplierEvaluation
{
    public int Id { get; set; }
    public int PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    [StringLength(30)][Display(Name = "Kỳ đánh giá")] public string Period { get; set; } = string.Empty;
    [Range(0, 10)][Display(Name = "Chất lượng")] public decimal Quality { get; set; }
    [Range(0, 10)][Display(Name = "Giao hàng đúng hạn")] public decimal Delivery { get; set; }
    [Range(0, 10)][Display(Name = "Giá cả")] public decimal Price { get; set; }
    [Range(0, 10)][Display(Name = "Dịch vụ, hỗ trợ")] public decimal Service { get; set; }
    [Display(Name = "Điểm tổng")] public decimal Total { get; set; }
    [StringLength(2)][Display(Name = "Xếp hạng")] public string Grade { get; set; } = "C";
    [StringLength(500)][Display(Name = "Nhận xét")] public string? Note { get; set; }
    [StringLength(100)] public string? EvaluatedBy { get; set; }
}

public enum LandedCostKind
{
    [Display(Name = "Vận chuyển, bốc xếp")] Freight = 1,
    [Display(Name = "Thuế nhập khẩu")] ImportDuty = 2,
    [Display(Name = "Bảo hiểm hàng hóa")] Insurance = 3,
    [Display(Name = "Chi phí mua hàng khác")] Other = 4
}

/// <summary>Chi phí mua hàng (vận chuyển, thuế nhập khẩu, bảo hiểm...) phân bổ vào giá nhập của hàng trên một đơn mua</summary>
public class LandedCost : IBranchOwned
{
    public int Id { get; set; }
    [StringLength(30)] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public LandedCostKind Kind { get; set; } = LandedCostKind.Freight;
    /// <summary>Nhà cung cấp dịch vụ (vận chuyển...) – ghi công nợ phải trả</summary>
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [StringLength(300)] public string Description { get; set; } = string.Empty;
    [Display(Name = "Chi phí chưa thuế")] public decimal Amount { get; set; }
    [Display(Name = "Thuế GTGT được khấu trừ")] public decimal VatAmount { get; set; }
    /// <summary>qty = theo số lượng; value = theo giá trị hàng nhập</summary>
    [StringLength(10)] public string Basis { get; set; } = "value";
    public int CounterAccountId { get; set; }
    public Account? CounterAccount { get; set; }
    public DocStatus Status { get; set; } = DocStatus.Posted;
    public int? JournalEntryId { get; set; }
    /// <summary>Hóa đơn mua của nhà cung cấp dịch vụ (khi ghi nợ phải trả 331)</summary>
    public int? InvoiceId { get; set; }
    [StringLength(30)] public string? VendorInvoiceNo { get; set; }
    public int? BranchId { get; set; }
    public int? CreatedById { get; set; }
    public ICollection<LandedCostLine> Lines { get; set; } = new List<LandedCostLine>();
}

public class LandedCostLine
{
    public int Id { get; set; }
    public int LandedCostId { get; set; }
    public LandedCost? LandedCost { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Allocated { get; set; }
    /// <summary>Phần cộng vào giá trị tồn kho</summary>
    public decimal ToInventory { get; set; }
    /// <summary>Phần hàng đã xuất bán: ghi vào giá vốn (632)</summary>
    public decimal ToCogs { get; set; }
}

public partial class PurchaseOrder
{
    public int? RequisitionId { get; set; }
    public int? RfqId { get; set; }
}

// =====================================================================
//  HỢP ĐỒNG: ĐỢT GIAO HÀNG, ĐỢT THANH TOÁN
// =====================================================================
public enum MilestoneKind
{
    [Display(Name = "Giao hàng")] Delivery = 1,
    [Display(Name = "Thanh toán")] Payment = 2
}

public class ContractMilestone
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public MilestoneKind Kind { get; set; } = MilestoneKind.Delivery;
    [StringLength(200)] public string Name { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);
    public decimal Amount { get; set; }
    public bool Done { get; set; }
    [DataType(DataType.Date)] public DateTime? DoneDate { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

// =====================================================================
//  BÁN HÀNG: BẢNG GIÁ, KHUYẾN MẠI, BÁO GIÁ, VẬN CHUYỂN, HOA HỒNG
// =====================================================================
public class PriceList
{
    public int Id { get; set; }
    [StringLength(20)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Tên bảng giá")] public string Name { get; set; } = string.Empty;
    [StringLength(50)][Display(Name = "Nhóm khách hàng")] public string? PartnerGroup { get; set; }
    [Display(Name = "Khách hàng cụ thể")] public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [StringLength(3)][Display(Name = "Loại tiền")] public string CurrencyCode { get; set; } = "VND";
    [DataType(DataType.Date)][Display(Name = "Từ ngày")] public DateTime ValidFrom { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Đến ngày")] public DateTime? ValidTo { get; set; }
    [Display(Name = "Ưu tiên (số lớn áp dụng trước)")] public int Priority { get; set; }
    [Display(Name = "Đang áp dụng")] public bool IsActive { get; set; } = true;
    public ICollection<PriceListLine> Lines { get; set; } = new List<PriceListLine>();
}

public class PriceListLine
{
    public int Id { get; set; }
    public int PriceListId { get; set; }
    public PriceList? PriceList { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    [Display(Name = "Từ số lượng")] public decimal MinQty { get; set; }
    public decimal Price { get; set; }
    [Display(Name = "Chiết khấu %")] public decimal DiscountPct { get; set; }
}

public class Promotion
{
    public int Id { get; set; }
    [StringLength(150)] public string Name { get; set; } = string.Empty;
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(50)] public string? PartnerGroup { get; set; }
    public decimal MinQty { get; set; }
    [Display(Name = "Chiết khấu / khuyến mại %")] public decimal DiscountPct { get; set; }
    [DataType(DataType.Date)] public DateTime ValidFrom { get; set; } = DateTime.Today;
    [DataType(DataType.Date)] public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum QuoteStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt (lợi nhuận thấp)")] PendingApproval = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Đã gửi khách")] Sent = 4,
    [Display(Name = "Khách đồng ý – đã lập đơn bán")] Converted = 5,
    [Display(Name = "Khách từ chối")] Rejected = 6,
    [Display(Name = "Đã hủy")] Cancelled = 7
}

public class SalesQuote : IBranchOwned
{
    public int Id { get; set; }
    [StringLength(30)][Display(Name = "Số báo giá")] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime ValidUntil { get; set; } = DateTime.Today.AddDays(15);
    [Display(Name = "Khách hàng")] public int? CustomerId { get; set; }
    public Partner? Customer { get; set; }
    [Display(Name = "Kho xuất dự kiến")] public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [Display(Name = "Trạng thái")] public QuoteStatus Status { get; set; } = QuoteStatus.Draft;
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal Total { get; set; }
    /// <summary>Giá vốn dự toán của hàng</summary>
    public decimal CostTotal { get; set; }
    [Display(Name = "Chi phí khác (vận chuyển, lắp đặt, hoa hồng...)")] public decimal OtherCost { get; set; }
    [Display(Name = "Lợi nhuận %")] public decimal MarginPct { get; set; }
    public int? SalespersonId { get; set; }
    public AppUser? Salesperson { get; set; }
    public int? SalesOrderId { get; set; }
    public int? ApprovedById { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    public int? BranchId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<SalesQuoteLine> Lines { get; set; } = new List<SalesQuoteLine>();
    [NotMapped] public decimal Profit => Subtotal - CostTotal - OtherCost;
}

public class SalesQuoteLine
{
    public int Id { get; set; }
    public int SalesQuoteId { get; set; }
    public SalesQuote? SalesQuote { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public int VatRate { get; set; }
    /// <summary>Giá vốn dự toán / đơn vị</summary>
    public decimal UnitCost { get; set; }
    [NotMapped] public decimal Amount => Math.Round(Quantity * UnitPrice * (100 - DiscountPct) / 100, 0);
}

public partial class SalesOrder
{
    public int? SalespersonId { get; set; }
    public int? QuoteId { get; set; }
}

public enum ShipmentStatus
{
    [Display(Name = "Chuẩn bị")] Preparing = 1,
    [Display(Name = "Đang vận chuyển")] InTransit = 2,
    [Display(Name = "Đã giao")] Delivered = 3,
    [Display(Name = "Giao thất bại / hoàn")] Failed = 4
}

public class Shipment
{
    public int Id { get; set; }
    [StringLength(30)] public string Number { get; set; } = string.Empty;
    public int InventoryDocId { get; set; }
    public InventoryDoc? InventoryDoc { get; set; }
    public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    [StringLength(100)][Display(Name = "Đơn vị vận chuyển")] public string? Carrier { get; set; }
    [StringLength(20)][Display(Name = "Biển số xe")] public string? VehicleNo { get; set; }
    [StringLength(100)][Display(Name = "Tài xế / người giao")] public string? Driver { get; set; }
    [StringLength(60)][Display(Name = "Mã vận đơn")] public string? TrackingNo { get; set; }
    [StringLength(300)][Display(Name = "Địa chỉ giao hàng")] public string? Address { get; set; }
    public ShipmentStatus Status { get; set; } = ShipmentStatus.Preparing;
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    [StringLength(100)][Display(Name = "Người nhận")] public string? Receiver { get; set; }
    public decimal Freight { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public class SalesTarget
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Bậc hoa hồng: doanh số tháng từ MinRevenue trở lên được hưởng RatePct (%) trên toàn bộ doanh số; UserId trống = áp dụng chung</summary>
public class CommissionTier
{
    public int Id { get; set; }
    [StringLength(100)] public string Name { get; set; } = string.Empty;
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    [StringLength(100)] public string? Department { get; set; }
    public decimal MinRevenue { get; set; }
    [Range(0, 100)] public decimal RatePct { get; set; }
    public bool IsActive { get; set; } = true;
}

public partial class Partner
{
    [StringLength(50)][Display(Name = "Nhóm đối tượng")] public string? PartnerGroup { get; set; }
    [StringLength(5)][Display(Name = "Xếp hạng")] public string? Grade { get; set; }
    [StringLength(50)][Display(Name = "Số ĐKKD")] public string? BusinessLicenseNo { get; set; }
    [StringLength(100)][Display(Name = "Người đại diện pháp luật")] public string? LegalRepresentative { get; set; }
    [StringLength(300)][Display(Name = "Điều kiện thanh toán")] public string? PaymentCondition { get; set; }
    [Display(Name = "Tạm ngừng giao dịch")] public bool IsBlocked { get; set; }
    [StringLength(300)][Display(Name = "Lý do tạm ngừng")] public string? BlockReason { get; set; }
}

// =====================================================================
//  KHO: NHÓM HÀNG, QUY ĐỔI ĐVT, LÔ / SERIAL / HẠN DÙNG, VỊ TRÍ, KIỂM KÊ
// =====================================================================
public enum TrackMode
{
    [Display(Name = "Không theo dõi")] None = 0,
    [Display(Name = "Theo lô")] Lot = 1,
    [Display(Name = "Theo số serial")] Serial = 2
}

public enum CostMethod
{
    [Display(Name = "Bình quân gia quyền")] Average = 0,
    [Display(Name = "Nhập trước xuất trước (FIFO)")] Fifo = 1,
    [Display(Name = "Đích danh (theo lô)")] Specific = 2
}

public class ItemGroup
{
    public int Id { get; set; }
    [StringLength(20)][Display(Name = "Mã nhóm")] public string Code { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Tên nhóm")] public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public ItemGroup? Parent { get; set; }
}

public class UomConversion
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(20)][Display(Name = "Đơn vị")] public string Unit { get; set; } = string.Empty;
    /// <summary>1 đơn vị này = Factor đơn vị tính cơ bản của hàng</summary>
    [Display(Name = "Quy đổi ra ĐVT cơ bản")] public decimal Factor { get; set; } = 1;
}

public partial class Item
{
    [Display(Name = "Nhóm hàng")] public int? GroupId { get; set; }
    public ItemGroup? Group { get; set; }
    [Range(0, 1_000_000_000)][Display(Name = "Tồn tối đa")] public decimal MaxStock { get; set; }
    [Display(Name = "Theo dõi")] public TrackMode TrackMode { get; set; }
    [Display(Name = "Quản lý hạn dùng")] public bool TrackExpiry { get; set; }
    [Display(Name = "Số ngày cảnh báo trước khi hết hạn")] public int ExpiryAlertDays { get; set; } = 30;
    [Display(Name = "Tính giá xuất kho")] public CostMethod CostMethod { get; set; }
    [StringLength(50)][Display(Name = "Mã vạch")] public string? Barcode { get; set; }
    [Range(0, 120)][Display(Name = "Bảo hành (tháng)")] public int WarrantyMonths { get; set; }
}

public class StockLot
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [StringLength(40)] public string LotNo { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime? MfgDate { get; set; }
    [DataType(DataType.Date)] public DateTime? ExpiryDate { get; set; }
    [DataType(DataType.Date)] public DateTime ReceivedDate { get; set; } = DateTime.Today;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public int? PartnerId { get; set; }
    [StringLength(30)] public string? SourceDoc { get; set; }
}

public class StockLotMove
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int WarehouseId { get; set; }
    [StringLength(40)] public string LotNo { get; set; } = string.Empty;
    /// <summary>Dương = nhập, âm = xuất</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    [StringLength(30)] public string DocNumber { get; set; } = string.Empty;
    public int InventoryDocId { get; set; }
    [StringLength(30)] public string DocType { get; set; } = string.Empty;
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [StringLength(30)] public string? OriginRef { get; set; }
    [DataType(DataType.Date)] public DateTime? ExpiryDate { get; set; }
}

public class BinLocation
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [StringLength(30)][Display(Name = "Mã vị trí")] public string Code { get; set; } = string.Empty;
    [StringLength(150)][Display(Name = "Mô tả")] public string? Name { get; set; }
    [StringLength(20)][Display(Name = "Loại")] public string Kind { get; set; } = "Kệ";
    public bool IsActive { get; set; } = true;
}

/// <summary>Tồn theo vị trí lưu kho; phần tồn chưa gán vị trí = tồn kho − tổng các vị trí</summary>
public class StockBin
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int WarehouseId { get; set; }
    public int BinId { get; set; }
    public BinLocation? Bin { get; set; }
    public decimal Quantity { get; set; }
}

public partial class InventoryDocLine
{
    [StringLength(40)] public string? LotNo { get; set; }
    [DataType(DataType.Date)] public DateTime? MfgDate { get; set; }
    [DataType(DataType.Date)] public DateTime? ExpiryDate { get; set; }
    public int? BinId { get; set; }
    public int? ToBinId { get; set; }
}

public enum StocktakeStatus
{
    [Display(Name = "Đang kiểm đếm")] Counting = 1,
    [Display(Name = "Đã xử lý chênh lệch")] Posted = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public class Stocktake : IBranchOwned
{
    public int Id { get; set; }
    [StringLength(30)] public string Number { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime Date { get; set; } = DateTime.Today;
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public StocktakeStatus Status { get; set; } = StocktakeStatus.Counting;
    [StringLength(300)] public string? Note { get; set; }
    public int? InventoryDocId { get; set; }
    public int? CreatedById { get; set; }
    public int? BranchId { get; set; }
    public ICollection<StocktakeLine> Lines { get; set; } = new List<StocktakeLine>();
}

public class StocktakeLine
{
    public int Id { get; set; }
    public int StocktakeId { get; set; }
    public Stocktake? Stocktake { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    /// <summary>Tồn sổ sách tại thời điểm lập đợt kiểm kê</summary>
    public decimal BookQty { get; set; }
    public decimal? CountedQty { get; set; }
    [StringLength(200)] public string? Note { get; set; }
}

/// <summary>Pallet: đơn vị lưu kho vật lý có mã riêng (in tem, quét), chứa nhiều mặt hàng / lô và nằm tại một vị trí</summary>
public class Pallet
{
    public int Id { get; set; }
    [StringLength(30)][Display(Name = "Mã pallet")] public string Code { get; set; } = string.Empty;
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public int? BinId { get; set; }
    public BinLocation? Bin { get; set; }
    [StringLength(200)] public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<PalletItem> Items { get; set; } = new List<PalletItem>();
}

public class PalletItem
{
    public int Id { get; set; }
    public int PalletId { get; set; }
    public Pallet? Pallet { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    [StringLength(40)] public string? LotNo { get; set; }
    public decimal Quantity { get; set; }
}
