using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>
/// Mua hàng: lập đơn → gửi duyệt (từ ngưỡng phải Ban Giám đốc duyệt) → nhận hàng (phiếu nhập kho, Nợ 15x / Có 331)
/// → kế toán ghi nhận hóa đơn mua (thuế GTGT đầu vào) → thanh toán.
/// </summary>
public class PurchaseOrdersController : ErpControllerBase
{
    private readonly PurchaseService _svc;
    public PurchaseOrdersController(ErpDbContext db, PurchaseService svc) : base(db) => _svc = svc;

    private bool CanEdit => User.InAny(AppRoles.PurchaseTeam);
    private bool CanReceive => User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Warehouse);

    private Task<PurchaseOrder?> LoadAsync(int id) => Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Warehouse).Include(p => p.CreatedBy).Include(p => p.ApprovedBy)
        .Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IActionResult> Index(PoStatus? status, string? q)
    {
        var query = Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Warehouse).AsQueryable();
        if (status.HasValue) query = query.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => p.Number.Contains(q) || p.Vendor!.Name.Contains(q));
        ViewBag.Status = status; ViewBag.Q = q; ViewBag.CanEdit = CanEdit; ViewBag.Threshold = _svc.ApprovalThreshold;
        ViewBag.Counts = (await Db.PurchaseOrders.Select(p => p.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return View(await query.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var po = await LoadAsync(id);
        if (po == null) return NotFound();
        ViewBag.Receipts = await Db.InventoryDocs.Include(d => d.Lines).Where(d => d.Origin == DocOrigin.Purchase && d.OriginId == id).OrderBy(d => d.Date).ToListAsync();
        ViewBag.Invoices = await Db.Invoices.Where(i => i.PurchaseOrderId == id).OrderBy(i => i.Date).ToListAsync();
        ViewBag.Returns = await Db.PurchaseReturns.Include(r => r.PurchaseOrderLine).ThenInclude(l => l!.Item).Include(r => r.Ncr).Include(r => r.InventoryDoc).Include(r => r.Offsets)
            .Where(r => r.PurchaseOrderLine!.PurchaseOrderId == id).OrderBy(r => r.Date).ThenBy(r => r.Id).ToListAsync();
        ViewBag.CanEdit = CanEdit; ViewBag.CanReceive = CanReceive; ViewBag.Threshold = _svc.ApprovalThreshold;
        return View(po);
    }

    public async Task<IActionResult> Create(int? itemId, int? vendorId)
    {
        if (!CanEdit) return Denied();
        var f = new OrderForm { PartnerId = vendorId, DeliveryDate = DateTime.Today.AddDays(7) };
        if (itemId.HasValue && await Db.Items.FindAsync(itemId) is { } it)
        {
            var need = Math.Max(it.MinStock * 2 - (await Db.StockLevels.Where(s => s.ItemId == it.Id).SumAsync(s => s.Quantity)), 1);
            f.Lines.Add(new LineInput { ItemId = it.Id, Quantity = Math.Ceiling(need), UnitPrice = Math.Round(it.AvgCost, 0), VatRate = it.VatRate });
            f.WarehouseId = (await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == (it.Type == ItemType.Material ? "KNVL" : "KHH")))?.Id;
        }
        else f.Lines.Add(new LineInput { Quantity = 1, VatRate = 10 });
        await FormDataAsync(f);
        return View("Form", f);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id);
        if (po == null) return NotFound();
        if (po.Status != PoStatus.Draft) { Error("Chỉ sửa đơn nháp."); return RedirectToAction(nameof(Details), new { id }); }
        var f = new OrderForm
        {
            Id = po.Id, Number = po.Number, Date = po.Date, PartnerId = po.VendorId, WarehouseId = po.WarehouseId, DeliveryDate = po.ExpectedDate, Note = po.Note,
            Lines = po.Lines.Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate }).ToList()
        };
        await FormDataAsync(f);
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(OrderForm form, bool submit)
    {
        if (!CanEdit) return Denied();
        var lines = form.Lines.Where(l => l.ItemId != null).ToList();
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng hàng.");
        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0)) ModelState.AddModelError("", "Số lượng phải > 0, đơn giá không âm.");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) ModelState.AddModelError("", "Mỗi mặt hàng chỉ một dòng.");
        if (!ModelState.IsValid) { await FormDataAsync(form); return View("Form", form); }

        PurchaseOrder po;
        if (form.Id == 0)
        {
            po = new PurchaseOrder { CreatedById = Me, Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(x => x.Number), "DMH", form.Date) };
            Db.PurchaseOrders.Add(po);
        }
        else
        {
            po = (await LoadAsync(form.Id))!;
            if (po.Status != PoStatus.Draft) throw new BusinessException("Đơn đã gửi duyệt, không sửa được.");
            Db.PurchaseOrderLines.RemoveRange(po.Lines.ToList()); po.Lines.Clear();
        }
        po.Date = form.Date.Date; po.VendorId = form.PartnerId; po.WarehouseId = form.WarehouseId; po.ExpectedDate = form.DeliveryDate.Date; po.Note = form.Note;
        foreach (var l in lines) po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate });
        PurchaseService.Recalc(po);
        await Db.SaveChangesAsync();
        if (submit) Success(await _svc.SubmitAsync(po));
        else Success($"Đã lưu nháp đơn mua {po.Number}.");
        return RedirectToAction(nameof(Details), new { id = po.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        Success(await _svc.SubmitAsync(po));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, bool approve)
    {
        if (!User.InAny(AppRoles.Approvers)) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        await _svc.ApproveAsync(po, approve);
        Success(approve ? $"Đã duyệt đơn mua {po.Number}." : $"Đã trả lại đơn {po.Number} về nháp.");
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Receive(int id)
    {
        if (!CanReceive) return Denied();
        var po = await LoadAsync(id);
        if (po == null) return NotFound();
        if (po.Status is not (PoStatus.Approved or PoStatus.PartiallyReceived)) { Error("Đơn chưa duyệt hoặc đã nhận đủ."); return RedirectToAction(nameof(Details), new { id }); }
        return View(po);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, DateTime date, Dictionary<int, decimal> qty)
    {
        if (!CanReceive) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        var doc = await _svc.ReceiveAsync(po, date, qty);
        Success($"Đã nhập kho phiếu {doc.Number} ({doc.TotalAmount.ToMoney()} đ) và ghi nhận công nợ phải trả {po.Vendor?.Name}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInvoice(int id, DateTime date, string? series, string? invoiceNo)
    {
        if (!User.CanAccounting()) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        var inv = await _svc.CreateInvoiceAsync(po, date, series, invoiceNo);
        Success($"Đã lập hóa đơn mua nháp {inv.Number}. Kiểm tra rồi bấm Ghi sổ.");
        return RedirectToAction("Details", "Invoices", new { id = inv.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanEdit) return Denied();
        var po = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        if (po.Lines.Any(l => l.ReceivedQty > 0)) throw new BusinessException("Đơn đã nhận hàng, không hủy được.");
        po.Status = PoStatus.Cancelled;
        await Db.SaveChangesAsync();
        Success($"Đã hủy đơn {po.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task FormDataAsync(OrderForm f)
    {
        ViewBag.Partners = await VendorsAsync(f.PartnerId);
        ViewBag.Whs = await WarehousesAsync(f.WarehouseId);
        ViewBag.LineOptions = new LineEditorOptions { Items = await ItemsAsync(i => i.IsStocked && i.Type != ItemType.FinishedGood), PriceSource = "cost", Lines = f.Lines, PriceLabel = "Đơn giá mua" };
        ViewBag.Threshold = _svc.ApprovalThreshold;
    }
}
