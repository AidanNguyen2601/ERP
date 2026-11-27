using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Định mức nhiều cấp, phiên bản, routing: tra cứu, bung định mức, giá thành định mức theo routing</summary>
public class BomService
{
    private readonly ErpDbContext _db;
    public BomService(ErpDbContext db) => _db = db;

    /// <summary>Định mức đang áp dụng của một mặt hàng tại một ngày (phiên bản mới nhất còn hiệu lực)</summary>
    public async Task<Bom?> ResolveAsync(int itemId, DateTime date) =>
        (await _db.Boms.Include(b => b.Lines).Include(b => b.Operations).Where(b => b.FinishedItemId == itemId && b.IsActive).ToListAsync())
            .Where(b => b.EffectiveOn(date)).OrderByDescending(b => b.VersionNo).ThenByDescending(b => b.Id).FirstOrDefault();

    /// <summary>Chi phí nhân công / SXC / số giờ định mức cho 1 đơn vị sản phẩm theo routing (đơn giá theo giờ của trung tâm sản xuất)</summary>
    public static (decimal Labor, decimal Overhead, decimal Hours) RoutingPerUnit(Bom bom, IReadOnlyDictionary<int, WorkCenter> centers, decimal batchQty = 0)
    {
        decimal lab = 0, ohd = 0, hours = 0;
        var qty = batchQty > 0 ? batchQty : bom.OutputQuantity;
        foreach (var op in bom.Operations)
        {
            var minutes = (op.SetupMinutes + op.RunMinutesPerUnit * qty) / qty;   // thời gian / 1 SP gồm phần chuẩn bị chia cho lô
            hours += minutes / 60m;
            if (op.WorkCenterId is int id && centers.TryGetValue(id, out var wc))
            {
                lab += minutes / 60m * Math.Max(1, op.Workers) * wc.LaborRatePerHour;
                ohd += minutes / 60m * wc.OverheadRatePerHour;
            }
        }
        return (Math.Round(lab, 0), Math.Round(ohd, 0), Math.Round(hours, 3));
    }

    public async Task<Dictionary<int, WorkCenter>> CentersAsync() => await _db.WorkCenters.ToDictionaryAsync(w => w.Id);

    /// <summary>Thành phẩm không được nằm trong chính cây định mức của nó (vòng lặp)</summary>
    public async Task<bool> HasCycleAsync(int finishedItemId, IEnumerable<int> componentItemIds, int? excludeBomId = null)
    {
        var seen = new HashSet<int>(); var queue = new Queue<int>(componentItemIds);
        var semis = await _db.Items.Where(i => i.Type == ItemType.SemiFinished || i.Type == ItemType.FinishedGood).Select(i => i.Id).ToListAsync();
        var boms = await _db.Boms.Include(b => b.Lines).Where(b => b.IsActive && b.Id != excludeBomId).ToListAsync();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id == finishedItemId) return true;
            if (!seen.Add(id) || !semis.Contains(id)) continue;
            foreach (var b in boms.Where(b => b.FinishedItemId == id)) foreach (var l in b.Lines) queue.Enqueue(l.ItemId);
        }
        return false;
    }

    /// <summary>Bung định mức nhiều cấp: nhu cầu nguyên vật liệu gốc và các bán thành phẩm cần có (kể cả bán thành phẩm đã có tồn)</summary>
    public async Task<(Dictionary<int, decimal> Materials, Dictionary<int, decimal> SemiFinished)> ExplodeAsync(int itemId, decimal qty, DateTime date, Dictionary<int, decimal>? semiStock = null)
    {
        var mats = new Dictionary<int, decimal>(); var semis = new Dictionary<int, decimal>();
        var types = await _db.Items.ToDictionaryAsync(i => i.Id, i => i.Type);
        async Task Walk(int id, decimal q, int depth)
        {
            var bom = await ResolveAsync(id, date);
            if (bom == null || depth > 8) { mats[id] = mats.GetValueOrDefault(id) + q; return; }
            foreach (var l in bom.Lines)
            {
                var need = ProductionService.Required(l, bom.OutputQuantity, q);
                if (types.GetValueOrDefault(l.ItemId) == ItemType.SemiFinished)
                {
                    semis[l.ItemId] = semis.GetValueOrDefault(l.ItemId) + need;
                    // Phần bán thành phẩm đã có tồn thì không cần bung xuống nguyên vật liệu
                    var have = semiStock?.GetValueOrDefault(l.ItemId) ?? 0;
                    var make = Math.Max(0, need - have);
                    if (semiStock != null) semiStock[l.ItemId] = Math.Max(0, have - need);
                    if (make > 0) await Walk(l.ItemId, make, depth + 1);
                }
                else mats[l.ItemId] = mats.GetValueOrDefault(l.ItemId) + need;
            }
        }
        await Walk(itemId, qty, 0);
        return (mats, semis);
    }
}

/// <summary>Nghiệp vụ sản xuất mở rộng: duyệt lệnh, công đoạn / sản lượng, xuất bổ sung / trả NVL, bán thành phẩm, đóng lệnh</summary>
public class ManufacturingService
{
    public static readonly string[] Shifts = { "Ca 1", "Ca 2", "Ca 3", "Hành chính" };
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly ProductionService _prod;
    private readonly BomService _boms;
    private readonly CurrentUser _user;
    public ManufacturingService(ErpDbContext db, InventoryService inv, ProductionService prod, BomService boms, CurrentUser user) { _db = db; _inv = inv; _prod = prod; _boms = boms; _user = user; }

    /// <summary>Tiến độ lệnh (0–100): trung bình sản lượng đạt của các công đoạn so với số lượng lệnh; lệnh không có routing tính theo sản lượng ghi nhận / số lượng hoàn thành</summary>
    public static int Progress(ProductionOrder po)
    {
        if (po.Status is ProductionStatus.Completed or ProductionStatus.Closed) return 100;
        if (po.Quantity <= 0) return 0;
        if (po.Operations.Any()) return (int)Math.Min(100, po.Operations.Average(o => Math.Min(1m, o.GoodQty / po.Quantity)) * 100);
        return (int)Math.Min(100, po.Outputs.Sum(o => o.GoodQty) * 100 / po.Quantity);
    }

    // ------------------------------------------------------------------ Sản lượng theo ca / công đoạn
    public async Task<ProductionOutput> AddOutputAsync(ProductionOrder po, int? operationId, DateTime date, string shift, int? workCenterId, decimal good, decimal scrap, decimal minutes, int workers, string source = "Nhập tay", int? ticketId = null, string? note = null)
    {
        if (po.Status is not (ProductionStatus.Released or ProductionStatus.InProgress)) throw new BusinessException("Chỉ ghi nhận sản lượng cho lệnh đã phát và chưa hoàn thành.");
        if (good < 0 || scrap < 0 || minutes < 0) throw new BusinessException("Số lượng và thời gian không âm.");
        if (good + scrap <= 0) throw new BusinessException("Nhập số lượng đạt hoặc số lượng lỗi.");
        ProductionOperation? op = null;
        if (operationId != null)
        {
            op = po.Operations.FirstOrDefault(o => o.Id == operationId) ?? throw new BusinessException("Công đoạn không thuộc lệnh.");
            workCenterId ??= op.WorkCenterId;
        }
        var o2 = new ProductionOutput
        {
            ProductionOrderId = po.Id, ProductionOperationId = op?.Id, Date = date, Shift = shift, WorkCenterId = workCenterId, GoodQty = good, ScrapQty = scrap,
            Minutes = minutes, Workers = Math.Max(1, workers), Source = source, ScaleTicketId = ticketId, Note = note, CreatedById = _user.Id, BranchId = po.BranchId
        };
        _db.ProductionOutputs.Add(o2);
        if (op != null)
        {
            op.GoodQty += good; op.ScrapQty += scrap; op.ActualMinutes += minutes;
            op.StartedAt ??= date;
            op.Status = op.GoodQty >= po.Quantity ? OperationStatus.Done : OperationStatus.InProgress;
            if (op.Status == OperationStatus.Done) op.FinishedAt = date;
        }
        await _db.SaveChangesAsync();
        return o2;
    }

    /// <summary>Số lượng thành phẩm đạt ghi nhận: công đoạn cuối nếu có routing, ngược lại tổng sản lượng đạt</summary>
    public static decimal RecordedGood(ProductionOrder po) =>
        po.Operations.Any() ? po.Operations.OrderBy(o => o.Seq).Last().GoodQty : po.Outputs.Sum(o => o.GoodQty);

    // ------------------------------------------------------------------ Xuất bổ sung / trả lại nguyên vật liệu
    public async Task<InventoryDoc> ExtraIssueAsync(ProductionOrder po, int lineId, decimal qty, DateTime date)
    {
        if (po.Status is not (ProductionStatus.Released or ProductionStatus.InProgress)) throw new BusinessException("Lệnh chưa phát hoặc đã kết thúc.");
        var l = po.Lines.FirstOrDefault(x => x.Id == lineId) ?? throw new BusinessException("Không tìm thấy dòng NVL.");
        if (qty <= 0) throw new BusinessException("Số lượng phải lớn hơn 0.");
        var doc = await _inv.CreateAndPostAsync(InvDocType.Issue, date, po.MaterialWarehouseId!.Value, DocOrigin.Production, po.Number, po.Id, null,
            $"Xuất bổ sung NVL cho lệnh sản xuất {po.Number}", new[] { (l.ItemId, qty, 0m) }, _user.Id);
        l.IssuedQty += qty; l.Cost += doc.Lines.Sum(x => x.Amount);
        po.MaterialCost = po.Lines.Sum(x => x.Cost);
        if (po.Status == ProductionStatus.Released) po.Status = ProductionStatus.InProgress;
        await _db.SaveChangesAsync();
        return doc;
    }

    public async Task<InventoryDoc> ReturnMaterialAsync(ProductionOrder po, int lineId, decimal qty, DateTime date)
    {
        if (po.Status != ProductionStatus.InProgress) throw new BusinessException("Chỉ trả NVL cho lệnh đang sản xuất.");
        var l = po.Lines.FirstOrDefault(x => x.Id == lineId) ?? throw new BusinessException("Không tìm thấy dòng NVL.");
        if (qty <= 0 || qty > l.IssuedQty) throw new BusinessException($"Số lượng trả phải từ 0 đến {l.IssuedQty.ToQty()} (đã xuất).");
        var unit = l.IssuedQty == 0 ? 0 : l.Cost / l.IssuedQty;
        var doc = await _inv.CreateAndPostAsync(InvDocType.Receipt, date, po.MaterialWarehouseId!.Value, DocOrigin.Production, po.Number, po.Id, null,
            $"Nhập trả NVL thừa từ lệnh sản xuất {po.Number}", new[] { (l.ItemId, qty, unit) }, _user.Id);
        l.IssuedQty -= qty; l.Cost -= doc.Lines.Sum(x => x.Amount);
        po.MaterialCost = po.Lines.Sum(x => x.Cost);
        await _db.SaveChangesAsync();
        return doc;
    }

    // ------------------------------------------------------------------ Bán thành phẩm (lệnh con)
    /// <summary>Tạo lệnh sản xuất bán thành phẩm cho dòng cần bán thành phẩm còn thiếu tồn; bán thành phẩm nhập về kho NVL của lệnh cha</summary>
    public async Task<ProductionOrder> CreateChildOrderAsync(ProductionOrder parent, int lineId)
    {
        var line = parent.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new BusinessException("Không tìm thấy dòng.");
        var item = await _db.Items.FindAsync(line.ItemId);
        if (item?.Type != ItemType.SemiFinished) throw new BusinessException("Dòng này không phải bán thành phẩm.");
        var have = await _db.StockLevels.Where(s => s.ItemId == line.ItemId && s.WarehouseId == parent.MaterialWarehouseId).SumAsync(s => s.Quantity);
        var already = await _db.ProductionOrders.Where(p => p.ParentOrderId == parent.Id && p.ItemId == line.ItemId && p.Status != ProductionStatus.Cancelled).SumAsync(p => p.Quantity);
        var shortage = line.RequiredQty - line.IssuedQty - have - already;
        if (shortage <= 0) throw new BusinessException("Đã đủ tồn / đã có lệnh sản xuất bán thành phẩm cho dòng này.");
        var bom = await _boms.ResolveAsync(line.ItemId, parent.StartDate) ?? throw new BusinessException($"Chưa có định mức đang áp dụng cho {item.Code}.");
        var qty = Math.Ceiling(shortage);
        var po = new ProductionOrder
        {
            BomId = bom.Id, Quantity = qty, MaterialWarehouseId = parent.MaterialWarehouseId, FinishedWarehouseId = parent.MaterialWarehouseId,
            StartDate = parent.StartDate.AddDays(-3) < DateTime.Today ? DateTime.Today : parent.StartDate.AddDays(-3), DueDate = parent.StartDate, OrderType = parent.OrderType,
            SalesOrderId = parent.SalesOrderId, ParentOrderId = parent.Id, Note = $"Bán thành phẩm cho lệnh {parent.Number}"
        };
        if (po.DueDate < po.StartDate) po.DueDate = po.StartDate;
        return await _prod.CreateAsync(po);
    }

    // ------------------------------------------------------------------ Đóng lệnh
    public async Task CloseAsync(ProductionOrder po)
    {
        if (po.Status != ProductionStatus.Completed) throw new BusinessException("Chỉ đóng lệnh đã hoàn thành.");
        var open = await _db.ProductionOrders.AnyAsync(p => p.ParentOrderId == po.Id && p.Status != ProductionStatus.Completed && p.Status != ProductionStatus.Closed && p.Status != ProductionStatus.Cancelled);
        if (open) throw new BusinessException("Còn lệnh sản xuất bán thành phẩm chưa hoàn thành.");
        po.Status = ProductionStatus.Closed; po.ClosedDate = DateTime.Today;
        await _db.SaveChangesAsync();
    }
}

/// <summary>Kế hoạch sản xuất tổng thể (MPS): nhu cầu từ đơn hàng + dự báo, tồn kho, đề xuất sản xuất, kiểm tra công suất</summary>
public class MpsService
{
    public const decimal WorkDaysPerMonth = 22;
    private readonly ErpDbContext _db;
    private readonly BomService _boms;
    private readonly ProductionService _prod;
    private readonly CurrentUser _user;
    public MpsService(ErpDbContext db, BomService boms, ProductionService prod, CurrentUser user) { _db = db; _boms = boms; _prod = prod; _user = user; }

    public record Row(MpsLine Line, decimal OrderDemand, decimal Opening, decimal Suggested, decimal Closing);
    public record Load(WorkCenter Center, DateTime Period, decimal Hours, decimal Capacity) { public decimal Pct => Capacity <= 0 ? 0 : Hours * 100 / Capacity; }
    public record Result(List<Row> Rows, List<Load> Loads);

    public async Task<MpsPlan> CreateAsync(string name, DateTime fromMonth, int months, string? note, IEnumerable<int>? itemIds)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("Nhập tên kế hoạch.");
        var from = new DateTime(fromMonth.Year, fromMonth.Month, 1);
        var plan = new MpsPlan
        {
            Name = name.Trim(), FromMonth = from, Months = Math.Clamp(months, 1, 12), Note = note, CreatedById = _user.Id,
            Code = await DocNumbers.NextAsync(_db.MpsPlans.Select(p => p.Code), "KH", from)
        };
        var wanted = itemIds?.ToHashSet();
        var items = await _db.Items.Where(i => i.IsActive && (i.Type == ItemType.FinishedGood || i.Type == ItemType.SemiFinished)).OrderBy(i => i.Code).ToListAsync();
        foreach (var it in items)
        {
            if (wanted != null && wanted.Count > 0 && !wanted.Contains(it.Id)) continue;
            var bom = await _boms.ResolveAsync(it.Id, from);
            if (bom == null) continue;
            for (var m = 0; m < plan.Months; m++) plan.Lines.Add(new MpsLine { ItemId = it.Id, BomId = bom.Id, Period = from.AddMonths(m) });
        }
        if (!plan.Lines.Any()) throw new BusinessException("Không có thành phẩm / bán thành phẩm nào có định mức đang áp dụng.");
        _db.MpsPlans.Add(plan);
        await _db.SaveChangesAsync();
        return plan;
    }

    /// <summary>Tính nhu cầu, tồn dự kiến, đề xuất và tải công suất theo trung tâm sản xuất</summary>
    public async Task<Result> ComputeAsync(MpsPlan plan, bool auto = false)
    {
        var lines = await _db.MpsLines.Include(l => l.Item).Include(l => l.Bom).ThenInclude(b => b!.Operations).Where(l => l.MpsPlanId == plan.Id).ToListAsync();
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var stock = (await _db.StockLevels.Where(s => itemIds.Contains(s.ItemId)).ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        // Nhu cầu từ đơn bán đã xác nhận chưa giao đủ, theo ngày giao (đơn trễ tính vào kỳ đầu)
        var firm = await _db.SalesOrderLines.Include(l => l.SalesOrder)
            .Where(l => itemIds.Contains(l.ItemId) && l.Quantity > l.DeliveredQty && (l.SalesOrder!.Status == SoStatus.Confirmed || l.SalesOrder.Status == SoStatus.PartiallyDelivered))
            .Select(l => new { l.ItemId, l.SalesOrder!.DeliveryDate, Q = l.Quantity - l.DeliveredQty }).ToListAsync();
        // Lệnh sản xuất đang mở (chưa hoàn thành) được tính là nguồn cung ở kỳ đến hạn
        var openMo = await _db.ProductionOrders.Where(p => itemIds.Contains(p.ItemId) && (p.Status == ProductionStatus.Draft || p.Status == ProductionStatus.PendingApproval || p.Status == ProductionStatus.Approved
            || p.Status == ProductionStatus.Released || p.Status == ProductionStatus.InProgress)).Select(p => new { p.Id, p.ItemId, p.DueDate, p.Quantity }).ToListAsync();
        var linkedMo = lines.Where(l => l.ProductionOrderId != null).Select(l => l.ProductionOrderId!.Value).ToHashSet();

        var rows = new List<Row>(); var first = plan.FromMonth; var last = plan.FromMonth.AddMonths(plan.Months);
        foreach (var g in lines.GroupBy(l => l.ItemId))
        {
            var opening = stock.GetValueOrDefault(g.Key);
            foreach (var l in g.OrderBy(x => x.Period))
            {
                var next = l.Period.AddMonths(1);
                var demand = firm.Where(f => f.ItemId == g.Key && ((l.Period == first && f.DeliveryDate < next) || (f.DeliveryDate >= l.Period && f.DeliveryDate < next))).Sum(f => f.Q);
                var inbound = openMo.Where(p => p.ItemId == g.Key && !linkedMo.Contains(p.Id) && ((l.Period == first && p.DueDate < next) || (p.DueDate >= l.Period && p.DueDate < next))).Sum(p => p.Quantity);
                var safety = l.Item?.MinStock ?? 0;
                var suggested = Math.Max(0, Math.Ceiling(demand + l.ForecastQty + safety - opening - inbound));
                if (auto && l.ProductionOrderId == null) l.PlannedQty = suggested;
                var closing = opening + inbound + l.PlannedQty - demand - l.ForecastQty;
                rows.Add(new Row(l, demand, opening + inbound, suggested, closing));
                opening = closing;
            }
        }
        // Tải công suất: Σ (thời gian chuẩn bị + chạy máy × SL kế hoạch) theo trung tâm / kỳ so với giờ khả dụng
        var centers = await _db.WorkCenters.Where(w => w.IsActive).ToListAsync();
        var hours = new Dictionary<(int, DateTime), decimal>();
        foreach (var l in lines.Where(x => x.PlannedQty > 0 && x.Bom != null))
            foreach (var op in l.Bom!.Operations.Where(o => o.WorkCenterId != null))
            {
                var h = (op.SetupMinutes + op.RunMinutesPerUnit * l.PlannedQty) / 60m;
                hours[(op.WorkCenterId!.Value, l.Period)] = hours.GetValueOrDefault((op.WorkCenterId!.Value, l.Period)) + h;
            }
        var loads = new List<Load>();
        foreach (var wc in centers)
            for (var m = 0; m < plan.Months; m++)
            {
                var p = plan.FromMonth.AddMonths(m);
                loads.Add(new Load(wc, p, Math.Round(hours.GetValueOrDefault((wc.Id, p)), 1), Math.Round(wc.HoursPerDay * wc.Efficiency / 100m * WorkDaysPerMonth, 1)));
            }
        return new Result(rows, loads.Where(x => x.Hours > 0 || centers.Count <= 12).ToList());
    }

    public async Task ApplySuggestionsAsync(MpsPlan plan)
    {
        await ComputeAsync(plan, true);   // đề xuất tính tuần tự từng kỳ: tồn dự kiến cuối kỳ trước chuyển sang kỳ sau
        await _db.SaveChangesAsync();
    }

    /// <summary>Tạo lệnh sản xuất nháp từ các dòng kế hoạch đã có số lượng (theo đơn hàng nếu kỳ có đơn bán, ngược lại theo kho)</summary>
    public async Task<List<ProductionOrder>> CreateOrdersAsync(MpsPlan plan, IEnumerable<int> lineIds)
    {
        var ids = lineIds.ToHashSet();
        var lines = await _db.MpsLines.Include(l => l.Item).Where(l => l.MpsPlanId == plan.Id && ids.Contains(l.Id) && l.PlannedQty > 0 && l.ProductionOrderId == null).ToListAsync();
        if (!lines.Any()) throw new BusinessException("Chọn các dòng có số lượng kế hoạch và chưa lập lệnh.");
        var nvl = await _db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KNVL") ?? await _db.Warehouses.FirstAsync(w => w.IsActive);
        var tp = await _db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KTP") ?? nvl;
        var result = new List<ProductionOrder>();
        foreach (var l in lines)
        {
            var bom = (l.BomId != null ? await _db.Boms.FindAsync(l.BomId) : null) ?? await _boms.ResolveAsync(l.ItemId, l.Period) ?? throw new BusinessException($"{l.Item?.Code} chưa có định mức.");
            var so = await _db.SalesOrderLines.Include(x => x.SalesOrder).Where(x => x.ItemId == l.ItemId && x.Quantity > x.DeliveredQty
                    && (x.SalesOrder!.Status == SoStatus.Confirmed || x.SalesOrder.Status == SoStatus.PartiallyDelivered) && x.SalesOrder.DeliveryDate >= l.Period && x.SalesOrder.DeliveryDate < l.Period.AddMonths(1))
                .OrderBy(x => x.SalesOrder!.DeliveryDate).Select(x => x.SalesOrderId).FirstOrDefaultAsync();
            var start = l.Period < DateTime.Today ? DateTime.Today : l.Period;
            var po = new ProductionOrder
            {
                BomId = bom.Id, Quantity = l.PlannedQty, MaterialWarehouseId = nvl.Id, FinishedWarehouseId = l.Item?.Type == ItemType.SemiFinished ? nvl.Id : tp.Id,
                StartDate = start, DueDate = l.Period.AddMonths(1).AddDays(-1) < start ? start : l.Period.AddMonths(1).AddDays(-1),
                SalesOrderId = so == 0 ? null : so, OrderType = so == 0 ? ProductionType.MakeToStock : ProductionType.MakeToOrder, Note = $"Từ kế hoạch tổng thể {plan.Code}"
            };
            await _prod.CreateAsync(po);
            l.ProductionOrderId = po.Id;
            result.Add(po);
        }
        await _db.SaveChangesAsync();
        return result;
    }
}

/// <summary>Gia công ngoài: giao NVL cho đơn vị gia công (Nợ 154 / Có 15x), nhận hàng nhập kho (Nợ 15x / Có 154) và lập hóa đơn mua phí gia công (Nợ 154)</summary>
public class OutsourceService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly InvoiceService _invoices;
    private readonly AccountingService _acc;
    private readonly BomService _boms;
    private readonly CurrentUser _user;
    public OutsourceService(ErpDbContext db, InventoryService inv, InvoiceService invoices, AccountingService acc, BomService boms, CurrentUser user)
    { _db = db; _inv = inv; _invoices = invoices; _acc = acc; _boms = boms; _user = user; }

    public async Task<OutsourceOrder> CreateAsync(OutsourceOrder o)
    {
        var bom = await _db.Boms.Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == o.BomId) ?? throw new BusinessException("Chọn định mức.");
        if (o.Quantity <= 0) throw new BusinessException("Số lượng gia công phải lớn hơn 0.");
        var vendor = await _db.Partners.FindAsync(o.VendorId) ?? throw new BusinessException("Chọn đơn vị gia công.");
        if (!vendor.IsVendor) throw new BusinessException("Đơn vị gia công phải là nhà cung cấp.");
        if (vendor.IsBlocked) throw new BusinessException($"Nhà cung cấp {vendor.Name} đang tạm ngừng giao dịch.");
        o.ItemId = bom.FinishedItemId!.Value;
        o.Number = await DocNumbers.NextAsync(_db.OutsourceOrders.Select(x => x.Number), "GC", o.Date);
        o.CreatedById = _user.Id; o.Status = OutsourceStatus.Draft;
        foreach (var l in bom.Lines) o.Materials.Add(new OutsourceMaterial { ItemId = l.ItemId, Quantity = ProductionService.Required(l, bom.OutputQuantity, o.Quantity) });
        _db.OutsourceOrders.Add(o);
        await _db.SaveChangesAsync();
        return o;
    }

    /// <summary>Giao nguyên vật liệu: xuất kho, giá trị chuyển vào chi phí dở dang (TK 154) chờ nhận hàng</summary>
    public async Task<InventoryDoc> SendAsync(OutsourceOrder o, DateTime date)
    {
        if (o.Status != OutsourceStatus.Draft) throw new BusinessException("Chỉ giao NVL cho phiếu nháp.");
        var doc = await _inv.CreateAndPostAsync(InvDocType.Issue, date, o.MaterialWarehouseId!.Value, DocOrigin.Outsource, o.Number, o.Id, o.VendorId,
            $"Giao NVL gia công ngoài {o.Number}", o.Materials.Select(m => (m.ItemId, m.Quantity, 0m)).ToList(), _user.Id);
        foreach (var m in o.Materials) m.Cost = doc.Lines.Where(x => x.ItemId == m.ItemId).Sum(x => x.Amount);
        o.MaterialCost = o.Materials.Sum(m => m.Cost);
        o.Status = OutsourceStatus.Sent;
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Nhận hàng gia công (có thể nhiều lần): nhập kho theo giá trị NVL + phí gia công, lập hóa đơn mua phí gia công ở trạng thái nháp</summary>
    public async Task<OutsourceReceipt> ReceiveAsync(OutsourceOrder o, DateTime date, decimal qty, string? invoiceSeries, string? invoiceNo)
    {
        if (o.Status != OutsourceStatus.Sent) throw new BusinessException("Phiếu chưa giao NVL hoặc đã nhận đủ.");
        var remaining = o.Quantity - o.ReceivedQty;
        if (qty <= 0 || qty > remaining) throw new BusinessException($"Số lượng nhận phải từ 0 đến {remaining.ToQty()} (còn lại).");
        var matPer = o.MaterialCost / o.Quantity;
        // Lần nhận cuối lấy phần giá trị NVL còn lại để TK 154 về đúng 0 (không dư do làm tròn)
        var matAmount = qty == remaining ? o.MaterialCost - o.Receipts.Sum(r => r.MaterialAmount) : Math.Round(matPer * qty, 0);
        var fee = Math.Round(o.FeePerUnit * qty, 0);
        var total = matAmount + fee;
        var doc = await _inv.CreateAndPostAsync(InvDocType.Receipt, date, o.ReceiveWarehouseId!.Value, DocOrigin.Outsource, o.Number, o.Id, o.VendorId,
            $"Nhận hàng gia công ngoài {o.Number}", new[] { (o.ItemId, qty, total / qty) }, _user.Id);
        Invoice? inv = null;
        if (fee > 0)
        {
            var vendor = await _db.Partners.FindAsync(o.VendorId);
            inv = new Invoice
            {
                Direction = InvoiceDirection.Purchase, Number = await _invoices.NextNumberAsync(InvoiceDirection.Purchase, date), Date = date,
                DueDate = date.AddDays(vendor?.PaymentTermDays ?? 30), PartnerId = o.VendorId, BranchId = o.BranchId, Series = invoiceSeries, InvoiceNo = invoiceNo, CreatedById = _user.Id,
                Note = $"Phí gia công ngoài theo phiếu {o.Number}"
            };
            inv.Lines.Add(new InvoiceLine
            {
                Description = $"Phí gia công {o.Item?.Name ?? ""} – phiếu {o.Number}", Unit = o.Item?.Unit, Quantity = qty, UnitPrice = o.FeePerUnit, VatRate = o.VatRate,
                AccountId = await _acc.RuleAsync(PostingCatalog.PrdWip)
            });
            InvoiceService.Recalc(inv);
            _db.Invoices.Add(inv);
            await _db.SaveChangesAsync();
        }
        var rc = new OutsourceReceipt { OutsourceOrderId = o.Id, Date = date, Quantity = qty, MaterialAmount = matAmount, FeeAmount = fee, InventoryDocId = doc.Id, InvoiceId = inv?.Id };
        _db.OutsourceReceipts.Add(rc);
        o.ReceivedQty += qty;
        if (o.ReceivedQty >= o.Quantity) o.Status = OutsourceStatus.Received;
        await _db.SaveChangesAsync();
        return rc;
    }

    public async Task CancelAsync(OutsourceOrder o)
    {
        if (o.Status != OutsourceStatus.Draft) throw new BusinessException("Đã giao NVL, không hủy được.");
        o.Status = OutsourceStatus.Cancelled;
        await _db.SaveChangesAsync();
    }
}
