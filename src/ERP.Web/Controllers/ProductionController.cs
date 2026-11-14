using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Lệnh sản xuất & hoạch định nhu cầu vật tư (MRP)</summary>
public class ProductionController : ErpControllerBase
{
    private readonly ProductionService _svc;
    public ProductionController(ErpDbContext db, ProductionService svc) : base(db) => _svc = svc;
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam);

    private Task<ProductionOrder?> LoadAsync(int id) => Db.ProductionOrders.Include(p => p.Item).Include(p => p.Bom).Include(p => p.MaterialWarehouse).Include(p => p.FinishedWarehouse)
        .Include(p => p.CreatedBy).Include(p => p.SalesOrder).Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IActionResult> Index(ProductionStatus? status)
    {
        var q = Db.ProductionOrders.Include(p => p.Item).Include(p => p.Lines).AsQueryable();
        if (status.HasValue) q = q.Where(p => p.Status == status);
        ViewBag.Status = status; ViewBag.CanEdit = CanEdit;
        ViewBag.Counts = (await Db.ProductionOrders.Select(p => p.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return View(await q.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.Id).ToListAsync());
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
        return View(po);
    }

    public async Task<IActionResult> Create(int? bomId, decimal? qty, int? salesOrderId)
    {
        if (!CanEdit) return Denied();
        await LoadFormAsync(bomId, salesOrderId);
        var nvl = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KNVL"); var tp = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KTP");
        return View("Form", new ProductionOrder { BomId = bomId, Quantity = qty ?? 10, SalesOrderId = salesOrderId, MaterialWarehouseId = nvl?.Id, FinishedWarehouseId = tp?.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ProductionOrder model, bool release)
    {
        if (!CanEdit) return Denied();
        if (model.DueDate < model.StartDate) ModelState.AddModelError(nameof(model.DueDate), "Ngày hoàn thành không trước ngày bắt đầu");
        ModelState.Remove(nameof(model.Number));
        if (!ModelState.IsValid) { await LoadFormAsync(model.BomId, model.SalesOrderId); return View("Form", model); }
        var po = await _svc.CreateAsync(model);
        if (release) await _svc.ReleaseAsync(po);
        Success($"Đã tạo lệnh sản xuất {po.Number}" + (release ? " và phát lệnh." : "."));
        return RedirectToAction(nameof(Details), new { id = po.Id });
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
    public async Task<IActionResult> Complete(int id, DateTime? date, decimal completedQty, decimal labor, decimal overhead)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        await _svc.CompleteAsync(po, date ?? DateTime.Today, completedQty, labor, overhead);
        Success($"Hoàn thành {po.Number}: nhập kho {completedQty.ToQty()} {po.Item?.Unit}, giá thành {po.UnitCost.ToMoney()} đ/{po.Item?.Unit}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy lệnh.");
        if (po.Status is not (ProductionStatus.Draft or ProductionStatus.Released)) throw new BusinessException("Lệnh đã xuất NVL, không hủy được.");
        po.Status = ProductionStatus.Cancelled;
        await Db.SaveChangesAsync();
        Success($"Đã hủy lệnh {po.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Hoạch định nhu cầu vật tư: nhu cầu từ lệnh SX chưa xuất NVL so với tồn kho + hàng đang về</summary>
    public async Task<IActionResult> Mrp()
    {
        var open = await Db.ProductionOrders.Include(p => p.Lines).Include(p => p.Item)
            .Where(p => p.Status == ProductionStatus.Draft || p.Status == ProductionStatus.Released || p.Status == ProductionStatus.InProgress).ToListAsync();
        var need = open.SelectMany(p => p.Lines.Select(l => (p, l))).GroupBy(x => x.l.ItemId)
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.l.RequiredQty - x.l.IssuedQty), Orders: string.Join(", ", g.Where(x => x.l.RequiredQty > x.l.IssuedQty).Select(x => x.p.Number).Distinct())));
        var ids = need.Keys.ToList();
        var items = await Db.Items.Where(i => ids.Contains(i.Id)).ToListAsync();
        var stock = (await Db.StockLevels.Where(s => ids.Contains(s.ItemId)).ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        var incoming = (await Db.PurchaseOrderLines.Include(l => l.PurchaseOrder)
                .Where(l => ids.Contains(l.ItemId) && (l.PurchaseOrder!.Status == PoStatus.Approved || l.PurchaseOrder.Status == PoStatus.PartiallyReceived || l.PurchaseOrder.Status == PoStatus.PendingApproval || l.PurchaseOrder.Status == PoStatus.Draft)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity - l.ReceivedQty));
        var rows = items.OrderBy(i => i.Code).Select(i => (Item: i, Need: need[i.Id].Qty, Orders: need[i.Id].Orders, Stock: stock.GetValueOrDefault(i.Id),
            Incoming: incoming.GetValueOrDefault(i.Id), Shortage: Math.Max(0, need[i.Id].Qty + i.MinStock - stock.GetValueOrDefault(i.Id) - incoming.GetValueOrDefault(i.Id)))).Where(r => r.Need > 0).ToList();
        ViewBag.Orders = open.OrderBy(p => p.StartDate).ToList();
        ViewBag.Vendors = await VendorsAsync();
        ViewBag.CanBuy = User.InAny(AppRoles.PurchaseTeam);
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

    private async Task LoadFormAsync(int? bomId, int? soId)
    {
        ViewBag.Boms = new SelectList(await Db.Boms.Include(b => b.FinishedItem).Where(b => b.IsActive).OrderBy(b => b.Code)
            .Select(b => new { b.Id, Name = b.Code + " – " + b.FinishedItem!.Name }).ToListAsync(), "Id", "Name", bomId);
        ViewBag.Whs = await WarehousesAsync();
        ViewBag.SalesOrders = new SelectList(await Db.SalesOrders.Include(s => s.Customer).Where(s => s.Status == SoStatus.Draft || s.Status == SoStatus.Confirmed || s.Status == SoStatus.PartiallyDelivered)
            .OrderByDescending(s => s.Date).Select(s => new { s.Id, Name = s.Number + " – " + s.Customer!.Name }).ToListAsync(), "Id", "Name", soId);
    }
}
