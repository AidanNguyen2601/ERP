using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Quản lý kho: tồn kho, phiếu nhập / xuất / chuyển kho / kiểm kê, thẻ kho, danh mục kho</summary>
public class InventoryController : ErpControllerBase
{
    private readonly InventoryService _inv;
    public InventoryController(ErpDbContext db, InventoryService inv) : base(db) => _inv = inv;
    private bool CanWrite => User.InAny(AppRoles.WarehouseTeam + "," + AppRoles.Accountant);

    public async Task<IActionResult> Index(int? warehouseId, ItemType? type, bool low = false)
    {
        var whs = await Db.Warehouses.OrderBy(w => w.Code).ToListAsync();
        var items = await Db.Items.Where(i => i.Type != ItemType.Service).OrderBy(i => i.Type).ThenBy(i => i.Code).ToListAsync();
        if (type.HasValue) items = items.Where(i => i.Type == type).ToList();
        var levels = await Db.StockLevels.ToListAsync();
        var rows = items.Select(i => (Item: i, ByWh: whs.ToDictionary(w => w.Id, w => levels.Where(l => l.ItemId == i.Id && l.WarehouseId == w.Id).Sum(l => l.Quantity))))
            .Select(x => (x.Item, x.ByWh, Total: x.ByWh.Values.Sum())).ToList();
        if (warehouseId.HasValue) rows = rows.Where(r => r.ByWh[warehouseId.Value] != 0).ToList();
        if (low) rows = rows.Where(r => r.Item.MinStock > 0 && r.Total < r.Item.MinStock).ToList();
        ViewBag.Warehouses = whs; ViewBag.WarehouseId = warehouseId; ViewBag.Type = type; ViewBag.Low = low; ViewBag.CanWrite = CanWrite;
        // Hàng đang về (đơn mua đã duyệt chưa nhận đủ) & cam kết giao (đơn bán đã xác nhận chưa giao)
        ViewBag.Incoming = (await Db.PurchaseOrderLines.Include(l => l.PurchaseOrder).Where(l => l.PurchaseOrder!.Status == PoStatus.Approved || l.PurchaseOrder.Status == PoStatus.PartiallyReceived).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity - l.ReceivedQty));
        ViewBag.Committed = (await Db.SalesOrderLines.Include(l => l.SalesOrder).Where(l => l.SalesOrder!.Status == SoStatus.Confirmed || l.SalesOrder.Status == SoStatus.PartiallyDelivered).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity - l.DeliveredQty));
        return View(rows);
    }

    public async Task<IActionResult> Docs(InvDocType? type, DateTime? from, DateTime? to, int? warehouseId, DocStatus? status)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-1)).Date;
        var q = Db.InventoryDocs.Include(d => d.Warehouse).Include(d => d.ToWarehouse).Include(d => d.Lines).Include(d => d.Partner).Where(d => d.Date >= f && d.Date <= t);
        if (type.HasValue) q = q.Where(d => d.Type == type);
        if (warehouseId.HasValue) q = q.Where(d => d.WarehouseId == warehouseId || d.ToWarehouseId == warehouseId);
        if (status.HasValue) q = q.Where(d => d.Status == status);
        ViewBag.Type = type; ViewBag.From = f; ViewBag.To = t; ViewBag.Status = status; ViewBag.Whs = await WarehousesAsync(warehouseId); ViewBag.CanWrite = CanWrite;
        return View(await q.OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var d = await Db.InventoryDocs.Include(x => x.Warehouse).Include(x => x.ToWarehouse).Include(x => x.Partner).Include(x => x.CounterAccount).Include(x => x.CreatedBy)
            .Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        ViewBag.CanWrite = CanWrite;
        if (d.Status == DocStatus.Draft && d.Type == InvDocType.Adjustment)
            ViewBag.Book = await _inv.OnHandAsync(d.WarehouseId, d.Lines.Select(l => l.ItemId));
        return View(d);
    }

    public async Task<IActionResult> Create(InvDocType type = InvDocType.Receipt)
    {
        if (!CanWrite) return Denied();
        var f = new InventoryDocForm { Type = type, Lines = { new LineInput { Quantity = 1 } } };
        await LoadAsync(f);
        return View("Form", f);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanWrite) return Denied();
        var d = await Db.InventoryDocs.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        if (d.Status != DocStatus.Draft || d.Origin != DocOrigin.Manual) { Error("Chỉ sửa phiếu thủ công đang nháp."); return RedirectToAction(nameof(Details), new { id }); }
        var f = new InventoryDocForm
        {
            Id = d.Id, Type = d.Type, Date = d.Date, WarehouseId = d.WarehouseId, ToWarehouseId = d.ToWarehouseId, PartnerId = d.PartnerId, CounterAccountId = d.CounterAccountId,
            Reason = d.Reason, Number = d.Number,
            Lines = d.Lines.Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = l.UnitCost, CountedQty = l.CountedQty, LotNo = l.LotNo, ExpiryDate = l.ExpiryDate, BinId = l.BinId, ToBinId = l.ToBinId }).ToList()
        };
        await LoadAsync(f);
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(InventoryDocForm form, bool post)
    {
        if (!CanWrite) return Denied();
        var lines = form.Lines.Where(l => l.ItemId != null).ToList();
        await UomService.NormalizeAsync(Db, lines);
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng hàng.");
        if (form.Type is InvDocType.Receipt or InvDocType.Issue && form.CounterAccountId == null) ModelState.AddModelError(nameof(form.CounterAccountId), "Chọn tài khoản đối ứng.");
        if (form.Type == InvDocType.Transfer && (form.ToWarehouseId == null || form.ToWarehouseId == form.WarehouseId)) ModelState.AddModelError(nameof(form.ToWarehouseId), "Chọn kho nhận khác kho xuất.");
        if (form.Type == InvDocType.Adjustment && lines.Any(l => l.CountedQty == null || l.CountedQty < 0)) ModelState.AddModelError("", "Nhập số lượng thực tế kiểm đếm (≥ 0) cho từng dòng.");
        if (form.Type != InvDocType.Adjustment && lines.Any(l => l.Quantity <= 0)) ModelState.AddModelError("", "Số lượng phải lớn hơn 0.");
        if (!ModelState.IsValid) { await LoadAsync(form); return View("Form", form); }

        InventoryDoc d;
        if (form.Id == 0)
        {
            d = new InventoryDoc { Type = form.Type, CreatedById = Me, Origin = DocOrigin.Manual, Number = await _inv.NextNumberAsync(form.Type, form.Date) };
            Db.InventoryDocs.Add(d);
        }
        else
        {
            d = await Db.InventoryDocs.Include(x => x.Lines).FirstAsync(x => x.Id == form.Id);
            if (d.Status != DocStatus.Draft) throw new BusinessException("Phiếu đã ghi sổ.");
            Db.InventoryDocLines.RemoveRange(d.Lines.ToList()); d.Lines.Clear();
        }
        d.Date = form.Date.Date; d.WarehouseId = form.WarehouseId!.Value; d.ToWarehouseId = form.Type == InvDocType.Transfer ? form.ToWarehouseId : null;
        d.PartnerId = form.PartnerId; d.CounterAccountId = form.Type is InvDocType.Receipt or InvDocType.Issue ? form.CounterAccountId : null; d.Reason = form.Reason;
        foreach (var l in lines)
            d.Lines.Add(new InventoryDocLine { ItemId = l.ItemId!.Value, Quantity = form.Type == InvDocType.Adjustment ? 0 : l.Quantity, CountedQty = l.CountedQty, UnitCost = form.Type == InvDocType.Receipt ? l.UnitPrice : 0,
                LotNo = string.IsNullOrWhiteSpace(l.LotNo) ? null : l.LotNo.Trim(), ExpiryDate = l.ExpiryDate, BinId = l.BinId, ToBinId = form.Type == InvDocType.Transfer ? l.ToBinId : null });
        await Db.SaveChangesAsync();
        if (post)
        {
            try { await _inv.PostAsync(d); }
            catch (BusinessException ex)
            {
                ModelState.AddModelError("", ex.Message + " Phiếu đã được lưu nháp.");
                form.Id = d.Id; form.Number = d.Number; ModelState.Remove(nameof(form.Id));
                await LoadAsync(form);
                return View("Form", form);
            }
            Success($"Đã ghi sổ phiếu {d.Number}.");
        }
        else Success($"Đã lưu nháp phiếu {d.Number}.");
        return RedirectToAction(nameof(Details), new { id = d.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Post(int id)
    {
        if (!CanWrite) return Denied();
        var d = await Db.InventoryDocs.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        await _inv.PostAsync(d);
        Success($"Đã ghi sổ phiếu {d.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!CanWrite) return Denied();
        var d = await Db.InventoryDocs.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        if (d.Status != DocStatus.Draft) throw new BusinessException("Chỉ xóa phiếu nháp.");
        Db.InventoryDocLines.RemoveRange(d.Lines.ToList()); Db.InventoryDocs.Remove(d);
        await Db.SaveChangesAsync();
        Success($"Đã xóa phiếu nháp {d.Number}.");
        return RedirectToAction(nameof(Docs));
    }

    /// <summary>Thẻ kho: nhập – xuất – tồn theo mặt hàng</summary>
    public async Task<IActionResult> StockCard(int itemId, int? warehouseId, DateTime? from, DateTime? to)
    {
        var item = await Db.Items.FindAsync(itemId);
        if (item == null) return NotFound();
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-4)).Date;
        var q = Db.StockMoves.Include(m => m.Warehouse).Where(m => m.ItemId == itemId);
        if (warehouseId.HasValue) q = q.Where(m => m.WarehouseId == warehouseId);
        var moves = await q.OrderBy(m => m.Date).ThenBy(m => m.Id).ToListAsync();
        var before = moves.Where(m => m.Date < f).ToList();
        ViewBag.OpenQty = before.Sum(m => m.Quantity); ViewBag.OpenValue = before.Sum(m => m.Amount);
        ViewBag.Item = item; ViewBag.From = f; ViewBag.To = t; ViewBag.WarehouseId = warehouseId; ViewBag.Whs = await WarehousesAsync(warehouseId);
        ViewBag.Items = await Db.Items.Where(i => i.Type != ItemType.Service).OrderBy(i => i.Code).ToListAsync();
        ViewBag.Docs = (await Db.InventoryDocs.Select(d => new { d.Number, d.Id }).ToListAsync()).GroupBy(d => d.Number).ToDictionary(g => g.Key, g => g.First().Id);
        return View(moves.Where(m => m.Date >= f && m.Date <= t).ToList());
    }

    /// <summary>Hủy (đảo) phiếu kho đã ghi sổ</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        if (!CanWrite) return Denied();
        var d = await Db.InventoryDocs.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        await _inv.ReverseAsync(d, DateTime.Today, reason ?? "");
        Success($"Đã hủy phiếu {d.Number}: tồn kho và bút toán đã được đảo lại.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Danh mục kho
    public async Task<IActionResult> Warehouses() => View(await Db.Warehouses.OrderBy(w => w.Code).ToListAsync());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.WarehouseTeam)]
    public async Task<IActionResult> SaveWarehouse(Warehouse model)
    {
        model.Code = model.Code?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name)) throw new BusinessException("Nhập mã và tên kho.");
        if (await Db.Warehouses.AnyAsync(w => w.Code == model.Code && w.Id != model.Id)) throw new BusinessException("Mã kho đã tồn tại.");
        if (model.Id == 0) Db.Warehouses.Add(model);
        else { var w = await Db.Warehouses.FindAsync(model.Id); if (w == null) return NotFound(); w.Code = model.Code; w.Name = model.Name; w.Address = model.Address; w.IsActive = model.IsActive; }
        await Db.SaveChangesAsync();
        Success($"Đã lưu kho {model.Code}.");
        return RedirectToAction(nameof(Warehouses));
    }

    private async Task LoadAsync(InventoryDocForm f)
    {
        ViewBag.Whs = await WarehousesAsync(f.WarehouseId);
        ViewBag.ToWhs = await WarehousesAsync(f.ToWarehouseId);
        ViewBag.Partners = await PartnersAsync(f.PartnerId);
        ViewBag.Counter = await AccountSelectAsync(f.CounterAccountId, a => !a.Code.StartsWith("15"));
        ViewBag.LineOptions = new LineEditorOptions
        {
            ShowUnit = true, Uoms = await UomService.LoadAsync(Db),
            Items = await ItemsAsync(i => i.IsStocked), PriceSource = "cost", ShowVat = false, ShowPrice = f.Type == InvDocType.Receipt,
            ShowCounted = f.Type == InvDocType.Adjustment, PriceLabel = "Đơn giá nhập", Lines = f.Lines,
            ShowLot = true, ShowToBin = f.Type == InvDocType.Transfer, Bins = await Db.BinLocations.Include(b => b.Warehouse).Where(b => b.IsActive).OrderBy(b => b.WarehouseId).ThenBy(b => b.Code).ToListAsync()
        };
    }
}
