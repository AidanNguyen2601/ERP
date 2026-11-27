using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Gia công ngoài: giao nguyên vật liệu, nhận bán thành phẩm / thành phẩm, phí gia công</summary>
public class OutsourceController : ErpControllerBase
{
    private readonly OutsourceService _svc;
    public OutsourceController(ErpDbContext db, OutsourceService svc) : base(db) => _svc = svc;
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam + "," + AppRoles.PurchaseTeam);
    private bool CanStock => User.InAny(AppRoles.ProductionTeam + "," + AppRoles.Warehouse);

    private Task<OutsourceOrder?> LoadAsync(int id) => Db.OutsourceOrders.Include(o => o.Vendor).Include(o => o.Item).Include(o => o.Bom).Include(o => o.MaterialWarehouse).Include(o => o.ReceiveWarehouse)
        .Include(o => o.ProductionOrder).Include(o => o.Materials).ThenInclude(m => m.Item).Include(o => o.Receipts).ThenInclude(r => r.Invoice).Include(o => o.Receipts).ThenInclude(r => r.InventoryDoc)
        .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id);

    public async Task<IActionResult> Index(OutsourceStatus? status)
    {
        ViewBag.CanEdit = CanEdit; ViewBag.Status = status;
        var q = Db.OutsourceOrders.Include(o => o.Vendor).Include(o => o.Item).AsQueryable();
        if (status != null) q = q.Where(o => o.Status == status);
        ViewBag.Counts = (await Db.OutsourceOrders.Select(o => o.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return View(await q.OrderByDescending(o => o.Date).ThenByDescending(o => o.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var o = await LoadAsync(id);
        if (o == null) return NotFound();
        ViewBag.CanEdit = CanEdit; ViewBag.CanStock = CanStock;
        var ids = o.Materials.Select(m => m.ItemId).ToList();
        ViewBag.Stock = (await Db.StockLevels.Where(s => s.WarehouseId == o.MaterialWarehouseId && ids.Contains(s.ItemId)).ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        ViewBag.Docs = await Db.InventoryDocs.Where(d => d.Origin == DocOrigin.Outsource && d.OriginId == id).OrderBy(d => d.Date).ThenBy(d => d.Id).ToListAsync();
        return View(o);
    }

    public async Task<IActionResult> Create(int? bomId, decimal? qty, int? productionOrderId)
    {
        if (!CanEdit) return Denied();
        await LoadFormAsync(bomId);
        var nvl = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KNVL"); var tp = await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KTP");
        return View("Form", new OutsourceOrder { BomId = bomId, Quantity = qty ?? 10, ProductionOrderId = productionOrderId, MaterialWarehouseId = nvl?.Id, ReceiveWarehouseId = tp?.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(OutsourceOrder model)
    {
        if (!CanEdit) return Denied();
        ModelState.Remove(nameof(model.Number));
        if (!ModelState.IsValid) { await LoadFormAsync(model.BomId); return View("Form", model); }
        var o = await _svc.CreateAsync(model);
        Success($"Đã lập phiếu gia công {o.Number}. Giao nguyên vật liệu cho đơn vị gia công để bắt đầu.");
        return RedirectToAction(nameof(Details), new { id = o.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int id, DateTime? date)
    {
        if (!CanStock) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        var doc = await _svc.SendAsync(o, date ?? DateTime.Today);
        Success($"Đã giao NVL phiếu kho {doc.Number}, trị giá {o.MaterialCost.ToMoney()} đ (Nợ 154 / Có 15x).");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, DateTime? date, decimal qty, string? series, string? invoiceNo)
    {
        if (!CanStock) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        var rc = await _svc.ReceiveAsync(o, date ?? DateTime.Today, qty, series, invoiceNo);
        Success($"Đã nhận {qty.ToQty()} {o.Item?.Unit} nhập kho, giá trị {(rc.MaterialAmount + rc.FeeAmount).ToMoney()} đ (NVL {rc.MaterialAmount.ToMoney()} + phí gia công {rc.FeeAmount.ToMoney()})." + (rc.InvoiceId != null ? " Đã lập hóa đơn mua phí gia công ở trạng thái nháp – nhập số hóa đơn và ghi sổ." : ""));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanEdit) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        await _svc.CancelAsync(o);
        Success($"Đã hủy phiếu {o.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LoadFormAsync(int? bomId)
    {
        ViewBag.Boms = new SelectList(await Db.Boms.Include(b => b.FinishedItem).Where(b => b.IsActive).OrderBy(b => b.Code)
            .Select(b => new { b.Id, Name = b.Code + " – " + b.FinishedItem!.Name + " (v" + b.VersionNo + ")" }).ToListAsync(), "Id", "Name", bomId);
        ViewBag.Vendors = await VendorsAsync();
        ViewBag.Whs = await WarehousesAsync();
        ViewBag.Orders = new SelectList(await Db.ProductionOrders.Where(p => p.Status == ProductionStatus.Draft || p.Status == ProductionStatus.Approved || p.Status == ProductionStatus.Released || p.Status == ProductionStatus.InProgress)
            .OrderByDescending(p => p.StartDate).Select(p => new { p.Id, Name = p.Number }).ToListAsync(), "Id", "Name");
    }
}
