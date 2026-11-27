using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Lệnh sản xuất, hoạch định nhu cầu vật tư (MRP nhiều cấp), thống kê sản lượng và so sánh định mức với thực tế</summary>
public class ProductionController : ErpControllerBase
{
    private readonly ProductionService _svc;
    private readonly ManufacturingService _mfg;
    private readonly BomService _boms;
    private readonly WorkflowService _wf;
    public ProductionController(ErpDbContext db, ProductionService svc, ManufacturingService mfg, BomService boms, WorkflowService wf) : base(db) { _svc = svc; _mfg = mfg; _boms = boms; _wf = wf; }
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam);

    private Task<ProductionOrder?> LoadAsync(int id) => Db.ProductionOrders.Include(p => p.Item).Include(p => p.Bom).ThenInclude(b => b!.Lines).Include(p => p.MaterialWarehouse).Include(p => p.FinishedWarehouse)
        .Include(p => p.CreatedBy).Include(p => p.SalesOrder).Include(p => p.Lines).ThenInclude(l => l.Item).Include(p => p.Operations).ThenInclude(o => o.WorkCenter).Include(p => p.Outputs)
        .AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IActionResult> Index(ProductionStatus? status, ProductionType? type)
    {
        var q = Db.ProductionOrders.Include(p => p.Item).Include(p => p.Lines).Include(p => p.Operations).Include(p => p.Outputs).AsQueryable();
        if (status.HasValue) q = q.Where(p => p.Status == status);
        if (type.HasValue) q = q.Where(p => p.OrderType == type);
        ViewBag.Status = status; ViewBag.Type = type; ViewBag.CanEdit = CanEdit;
        ViewBag.Counts = (await Db.ProductionOrders.Select(p => p.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        ViewBag.TypeCounts = (await Db.ProductionOrders.Where(p => p.Status != ProductionStatus.Cancelled).Select(p => p.OrderType).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return View(await q.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.Id).AsSplitQuery().ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var po = await LoadAsync(id);
        if (po == null) return NotFound();
        var ids = po.Lines.Select(l => l.ItemId).ToList();
        ViewBag.Stock = (await Db.StockLevels.Where(s => s.WarehouseId == po.MaterialWarehouseId && ids.Contains(s.ItemId)).ToListAsync())
            .GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        ViewBag.Docs = await Db.InventoryDocs.Include(d => d.Lines).Where(d => d.Origin == DocOrigin.Production && d.OriginId == id).OrderBy(d => d.Date).ThenBy(d => d.Id).ToListAsync();
        ViewBag.CanEdit = CanEdit;
        ViewBag.Avg = (await Db.Items.Where(i => ids.Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id, i => i.AvgCost);
        ViewBag.Children = await Db.ProductionOrders.Include(p => p.Item).Where(p => p.ParentOrderId == id).OrderBy(p => p.Number).ToListAsync();
        ViewBag.Parent = po.ParentOrderId == null ? null : await Db.ProductionOrders.FindAsync(po.ParentOrderId);
        ViewBag.Approval = await _wf.LatestAsync(ApprovalDocType.ProductionOrder, id);
        ViewBag.Centers = await Db.WorkCenters.Where(w => w.IsActive).OrderBy(w => w.Code).ToListAsync();
        ViewBag.RecordedGood = ManufacturingService.RecordedGood(po);
        ViewBag.Outsourced = await Db.OutsourceOrders.Where(o => o.ProductionOrderId == id).ToListAsync();
        // Nhân công, SXC mặc định: theo routing nếu có, ngược lại theo định mức nhập tay
        var bom = po.Bom!;
        var rt = BomService.RoutingPerUnit(bom, await _boms.CentersAsync(), po.Quantity);
        ViewBag.StdLabor = (bom.LaborCostPerUnit > 0 || !bom.Operations.Any() ? bom.LaborCostPerUnit : rt.Labor) * po.Quantity;
        ViewBag.StdOverhead = (bom.OverheadPerUnit > 0 || !bom.Operations.Any() ? bom.OverheadPerUnit : rt.Overhead) * po.Quantity;
        // Bán thành phẩm: dòng còn thiếu tồn và chưa có lệnh con
        var semiIds = po.Lines.Where(l => l.Item?.Type == ItemType.SemiFinished).Select(l => l.ItemId).ToList();
        ViewBag.ChildQty = (await Db.ProductionOrders.Where(p => p.ParentOrderId == id && p.Status != ProductionStatus.Cancelled).ToListAsync()).GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity));
        // Phiếu cân tham chiếu số lệnh chưa ghi nhận sản lượng
        var used = await Db.ProductionOutputs.Where(o => o.ScaleTicketId != null).Select(o => o.ScaleTicketId!.Value).ToListAsync();
        ViewBag.Tickets = await Db.ScaleTickets.Where(t => t.Reference == po.Number && !used.Contains(t.Id)).OrderBy(t => t.WeighedAt).ToListAsync();
        return View(po);
    }

    public async Task<IActionResult> Create(int? bomId, decimal? qty, int? salesOrderId, int? itemId)
    {
        if (!CanEdit) return Denied();
        if (bomId == null && itemId != null) bomId = (await _boms.ResolveAsync(itemId.Value, DateTime.Today))?.Id;
        await LoadFormAsync(bomId, salesOrderId);
        var nvl = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KNVL"); var tp = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KTP");
        return View("Form", new ProductionOrder { BomId = bomId, Quantity = qty ?? 10, SalesOrderId = salesOrderId, OrderType = salesOrderId != null ? ProductionType.MakeToOrder : ProductionType.MakeToStock, MaterialWarehouseId = nvl?.Id, FinishedWarehouseId = tp?.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ProductionOrder model, bool release)
    {
        if (!CanEdit) return Denied();
        if (model.DueDate < model.StartDate) ModelState.AddModelError(nameof(model.DueDate), "Ngày hoàn thành không trước ngày bắt đầu");
        if (model.OrderType == ProductionType.MakeToOrder && model.SalesOrderId == null) ModelState.AddModelError(nameof(model.SalesOrderId), "Sản xuất theo đơn hàng (MTO) phải chọn đơn bán liên quan.");
        ModelState.Remove(nameof(model.Number));
        if (!ModelState.IsValid) { await LoadFormAsync(model.BomId, model.SalesOrderId); return View("Form", model); }
        var po = await _svc.CreateAsync(model);
        if (release)
        {
            var full = await LoadAsync(po.Id);
            try { await _svc.ReleaseAsync(full!); Success($"Đã tạo lệnh sản xuất {po.Number} và phát lệnh."); }
            catch (PendingApprovalException ex) { Success($"Đã tạo lệnh sản xuất {po.Number}."); TempData["Warning"] = ex.Message; }
        }
        else Success($"Đã tạo lệnh sản xuất {po.Number}.");
        return RedirectToAction(nameof(Details), new { id = po.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        var msg = await _svc.SubmitAsync(po);
        if (msg != null) TempData["Warning"] = msg; else Success($"Lệnh {po.Number} không cần duyệt theo quy tắc, đã được duyệt tự động.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Release(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        await _svc.ReleaseAsync(po);
        Success($"Đã phát lệnh {po.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueMaterials(int id, DateTime? date)
    {
        if (!User.InAny(AppRoles.ProductionTeam + "," + AppRoles.Warehouse)) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        var doc = await _svc.IssueMaterialsAsync(po, date ?? DateTime.Today);
        Success($"Đã xuất NVL phiếu {doc.Number}, trị giá {doc.TotalAmount.ToMoney()} đ (Nợ 154 / Có 152).");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExtraIssue(int id, int lineId, decimal qty, DateTime? date)
    {
        if (!User.InAny(AppRoles.ProductionTeam + "," + AppRoles.Warehouse)) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        var doc = await _mfg.ExtraIssueAsync(po, lineId, qty, date ?? DateTime.Today);
        Success($"Đã xuất bổ sung NVL phiếu {doc.Number}, trị giá {doc.TotalAmount.ToMoney()} đ.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnMaterial(int id, int lineId, decimal qty, DateTime? date)
    {
        if (!User.InAny(AppRoles.ProductionTeam + "," + AppRoles.Warehouse)) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        var doc = await _mfg.ReturnMaterialAsync(po, lineId, qty, date ?? DateTime.Today);
        Success($"Đã nhập trả NVL thừa phiếu {doc.Number}, trị giá {doc.TotalAmount.ToMoney()} đ.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOutput(int id, int? operationId, DateTime? date, string shift, int? workCenterId, decimal good, decimal scrap, decimal minutes, int workers, string? note)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        await _mfg.AddOutputAsync(po, operationId, date ?? DateTime.Today, shift, workCenterId, good, scrap, minutes, workers, "Nhập tay", null, note);
        Success($"Đã ghi nhận sản lượng {good.ToQty()} đạt, {scrap.ToQty()} lỗi ({shift}).");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Ghi nhận sản lượng từ phiếu cân đẩy về (khối lượng tịnh = sản lượng đạt, cùng đơn vị tính của thành phẩm)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OutputFromTicket(int id, int ticketId, string shift, int? operationId, int? workCenterId)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        var t = await Db.ScaleTickets.FindAsync(ticketId) ?? throw new BusinessException("Không tìm thấy phiếu cân.");
        if (t.Reference != po.Number) throw new BusinessException("Phiếu cân không tham chiếu lệnh này.");
        if (await Db.ProductionOutputs.AnyAsync(o => o.ScaleTicketId == ticketId)) throw new BusinessException("Phiếu cân đã được ghi nhận.");
        await _mfg.AddOutputAsync(po, operationId, t.WeighedAt.Date, shift, workCenterId, t.NetKg, 0, 0, 1, "Phiếu cân", t.Id, $"Phiếu cân {t.TicketNo} ({t.Device})");
        Success($"Đã ghi nhận {t.NetKg.ToQty()} từ phiếu cân {t.TicketNo}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, DateTime? date, decimal completedQty, decimal labor, decimal overhead)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        await _svc.CompleteAsync(po, date ?? DateTime.Today, completedQty, labor, overhead);
        Success($"Hoàn thành {po.Number}: nhập kho {completedQty.ToQty()} {po.Item?.Unit}, giá thành {po.UnitCost.ToMoney()} đ/{po.Item?.Unit}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        await _mfg.CloseAsync(po);
        Success($"Đã đóng lệnh {po.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateChild(int id, int lineId)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        var child = await _mfg.CreateChildOrderAsync(po, lineId);
        Success($"Đã tạo lệnh sản xuất bán thành phẩm {child.Number} ({child.Quantity.ToQty()}). Phát lệnh và sản xuất xong thì bán thành phẩm nhập kho để lệnh này xuất dùng.");
        return RedirectToAction(nameof(Details), new { id = child.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        if (po.Status is not (ProductionStatus.Draft or ProductionStatus.Released or ProductionStatus.PendingApproval or ProductionStatus.Approved)) throw new BusinessException("Lệnh đã xuất NVL, không hủy được.");
        po.Status = ProductionStatus.Cancelled;
        await Db.SaveChangesAsync();
        Success($"Đã hủy lệnh {po.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ MRP nhiều cấp
    /// <summary>Hoạch định nhu cầu vật tư: nhu cầu từ lệnh SX chưa xuất NVL so với tồn kho + hàng đang về. Bán thành phẩm còn thiếu được bung tiếp xuống nguyên vật liệu theo định mức của chúng</summary>
    public async Task<IActionResult> Mrp()
    {
        var open = await Db.ProductionOrders.Include(p => p.Lines).Include(p => p.Item)
            .Where(p => p.Status == ProductionStatus.Draft || p.Status == ProductionStatus.PendingApproval || p.Status == ProductionStatus.Approved || p.Status == ProductionStatus.Released || p.Status == ProductionStatus.InProgress).AsSplitQuery().ToListAsync();
        var need = new Dictionary<int, (decimal Qty, HashSet<string> Orders)>();
        void AddNeed(int itemId, decimal qty, string label) { if (qty <= 0) return; var cur = need.GetValueOrDefault(itemId, (0m, new HashSet<string>())); cur.Item2.Add(label); need[itemId] = (cur.Item1 + qty, cur.Item2); }
        foreach (var p in open) foreach (var l in p.Lines.Where(l => l.RequiredQty > l.IssuedQty)) AddNeed(l.ItemId, l.RequiredQty - l.IssuedQty, p.Number);

        var allItems = await Db.Items.ToDictionaryAsync(i => i.Id);
        var stockAll = (await Db.StockLevels.ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        // Bán thành phẩm đã có lệnh sản xuất đang mở (chưa hoàn thành) được tính là hàng đang về
        var semiIncoming = open.GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity));
        foreach (var semiId in need.Keys.Where(k => allItems[k].Type == ItemType.SemiFinished).ToList())
        {
            var shortage = need[semiId].Qty - stockAll.GetValueOrDefault(semiId) - semiIncoming.GetValueOrDefault(semiId);
            if (shortage <= 0) continue;
            var (mats, semis) = await _boms.ExplodeAsync(semiId, shortage, DateTime.Today);
            var label = $"BTP {allItems[semiId].Code}";
            foreach (var m in mats) AddNeed(m.Key, m.Value, label);
            foreach (var s in semis.Where(s => s.Key != semiId)) AddNeed(s.Key, s.Value, label);
        }
        var ids = need.Keys.ToList();
        var items = ids.Select(i => allItems[i]).ToList();
        var incoming = (await Db.PurchaseOrderLines.Include(l => l.PurchaseOrder)
                .Where(l => ids.Contains(l.ItemId) && (l.PurchaseOrder!.Status == PoStatus.Approved || l.PurchaseOrder.Status == PoStatus.PartiallyReceived || l.PurchaseOrder.Status == PoStatus.PendingApproval || l.PurchaseOrder.Status == PoStatus.Draft)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity - l.ReceivedQty));
        foreach (var kv in semiIncoming) incoming[kv.Key] = incoming.GetValueOrDefault(kv.Key) + kv.Value;
        var rows = items.OrderBy(i => i.Type == ItemType.SemiFinished ? 0 : 1).ThenBy(i => i.Code).Select(i => (Item: i, Need: need[i.Id].Qty, Orders: string.Join(", ", need[i.Id].Orders), Stock: stockAll.GetValueOrDefault(i.Id),
            Incoming: incoming.GetValueOrDefault(i.Id), Shortage: Math.Max(0, need[i.Id].Qty + i.MinStock - stockAll.GetValueOrDefault(i.Id) - incoming.GetValueOrDefault(i.Id)))).Where(r => r.Need > 0).ToList();
        ViewBag.Orders = open.OrderBy(p => p.StartDate).ToList();
        ViewBag.Vendors = await VendorsAsync();
        ViewBag.CanBuy = User.InAny(AppRoles.PurchaseTeam);
        ViewBag.CanMake = CanEdit;
        return View(rows);
    }

    /// <summary>Tạo đơn mua nháp từ các vật tư thiếu</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePurchase(int vendorId, Dictionary<int, decimal> qty)
    {
        if (!User.InAny(AppRoles.PurchaseTeam)) return Denied();
        var lines = qty.Where(q => q.Value > 0).ToList();
        if (!lines.Any()) throw new BusinessException("Chọn số lượng cần mua.");
        var items = (await Db.Items.Where(i => lines.Select(l => l.Key).Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id);
        if (lines.Any(l => items[l.Key].Type == ItemType.SemiFinished)) throw new BusinessException("Bán thành phẩm tự sản xuất – lập lệnh sản xuất, không đặt mua.");
        var wh = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KNVL") ?? await Db.Warehouses.FirstAsync(w => w.IsActive);
        var po = new PurchaseOrder
        {
            Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(x => x.Number), "DMH", DateTime.Today), VendorId = vendorId, WarehouseId = wh.Id, CreatedById = Me,
            ExpectedDate = DateTime.Today.AddDays(7), Note = "Tạo từ hoạch định nhu cầu vật tư (MRP)"
        };
        foreach (var l in lines) po.Lines.Add(new PurchaseOrderLine { ItemId = l.Key, Quantity = Math.Ceiling(l.Value), UnitPrice = Math.Round(items[l.Key].AvgCost, 0), VatRate = items[l.Key].VatRate });
        PurchaseService.Recalc(po);
        Db.PurchaseOrders.Add(po);
        await Db.SaveChangesAsync();
        Success($"Đã tạo đơn mua nháp {po.Number} cho {po.Lines.Count} vật tư. Kiểm tra giá rồi gửi duyệt.");
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    // ------------------------------------------------------------------ Thống kê sản lượng & năng suất
    public async Task<IActionResult> Outputs(DateTime? from, DateTime? to, int? workCenterId, string? shift)
    {
        var (f, t) = Range(from, to);
        var q = Db.ProductionOutputs.Include(o => o.ProductionOrder).ThenInclude(p => p!.Item).Include(o => o.WorkCenter).Include(o => o.Operation).Where(o => o.Date >= f && o.Date <= t);
        if (workCenterId != null) q = q.Where(o => o.WorkCenterId == workCenterId);
        if (!string.IsNullOrEmpty(shift)) q = q.Where(o => o.Shift == shift);
        ViewBag.From = f; ViewBag.To = t; ViewBag.WorkCenterId = workCenterId; ViewBag.Shift = shift;
        ViewBag.Centers = await Db.WorkCenters.OrderBy(w => w.Code).ToListAsync();
        var list = await q.OrderByDescending(o => o.Date).ThenBy(o => o.Shift).ToListAsync();
        // Lệnh thuộc loại MTO / MTS trong kỳ
        ViewBag.Types = (await Db.ProductionOrders.Where(p => p.Status != ProductionStatus.Cancelled && p.StartDate <= t && p.DueDate >= f).ToListAsync())
            .GroupBy(p => p.OrderType).ToDictionary(g => g.Key, g => (Count: g.Count(), Qty: g.Sum(p => p.Quantity), Done: g.Where(p => p.Status is ProductionStatus.Completed or ProductionStatus.Closed).Sum(p => p.CompletedQty)));
        return View(list);
    }

    // ------------------------------------------------------------------ So sánh định mức với thực tế
    public async Task<IActionResult> Variance(DateTime? from, DateTime? to, decimal threshold = 5)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-3)).Date;
        var orders = await Db.ProductionOrders.Include(p => p.Item).Include(p => p.Bom).ThenInclude(b => b!.Lines).Include(p => p.Bom).ThenInclude(b => b!.Operations)
            .Include(p => p.Lines).ThenInclude(l => l.Item).Include(p => p.Operations)
            .Where(p => (p.Status == ProductionStatus.Completed || p.Status == ProductionStatus.Closed) && p.CompletedDate >= f && p.CompletedDate <= t).OrderByDescending(p => p.CompletedDate).AsSplitQuery().ToListAsync();
        ViewBag.From = f; ViewBag.To = t; ViewBag.Threshold = threshold;
        ViewBag.Centers = await _boms.CentersAsync();
        return View(orders);
    }

    private async Task LoadFormAsync(int? bomId, int? soId)
    {
        ViewBag.Boms = new SelectList(await Db.Boms.Include(b => b.FinishedItem).Where(b => b.IsActive).OrderBy(b => b.Code)
            .Select(b => new { b.Id, Name = b.Code + " – " + b.FinishedItem!.Name + " (v" + b.VersionNo + ")" }).ToListAsync(), "Id", "Name", bomId);
        ViewBag.Whs = await WarehousesAsync();
        ViewBag.SalesOrders = new SelectList(await Db.SalesOrders.Include(s => s.Customer).Where(s => s.Status == SoStatus.Draft || s.Status == SoStatus.Confirmed || s.Status == SoStatus.PartiallyDelivered)
            .OrderByDescending(s => s.Date).Select(s => new { s.Id, Name = s.Number + " – " + s.Customer!.Name }).ToListAsync(), "Id", "Name", soId);
    }
}
