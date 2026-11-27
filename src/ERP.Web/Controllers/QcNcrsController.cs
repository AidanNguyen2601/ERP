using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Báo cáo không phù hợp (NCR) và xử lý hàng không đạt</summary>
public class QcNcrsController : ErpControllerBase
{
    private readonly QualityService _qc;
    public QcNcrsController(ErpDbContext db, QualityService qc) : base(db) => _qc = qc;
    private bool CanManage => User.InAny(AppRoles.QcManagers);
    /// <summary>Lập phiếu xuất hủy / trả hàng: QC hoặc thủ kho</summary>
    private bool CanExecute => User.InAny(AppRoles.QcManagers + "," + AppRoles.Warehouse);

    public async Task<IActionResult> Index(NcrStatus? status, NcrDisposition? disposition, bool overdue = false, string? q = null)
    {
        var query = Db.QcNonconformances.Include(n => n.Item).Include(n => n.Partner).Include(n => n.Inspection).AsQueryable();
        if (status.HasValue) query = query.Where(n => n.Status == status);
        if (disposition.HasValue) query = query.Where(n => n.Disposition == disposition);
        if (overdue) { var today = DateTime.Today; query = query.Where(n => n.Status == NcrStatus.Open && n.DueDate < today); }
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(n => n.Number.Contains(q) || n.Item!.Code.Contains(q) || n.Item.Name.Contains(q) || n.Description.Contains(q));
        ViewBag.Status = status; ViewBag.Disposition = disposition; ViewBag.Overdue = overdue; ViewBag.Q = q; ViewBag.CanManage = CanManage;
        return View(await query.OrderBy(n => n.Status).ThenByDescending(n => n.Date).ThenByDescending(n => n.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var n = await Load(id);
        if (n == null) return NotFound();
        ViewBag.CanManage = CanManage; ViewBag.CanExecute = CanExecute;
        ViewBag.Returns = await Db.PurchaseReturns.Include(r => r.PurchaseOrderLine).ThenInclude(l => l!.PurchaseOrder).Include(r => r.Offsets).ThenInclude(o => o.Invoice).Where(r => r.NcrId == id).OrderBy(r => r.Id).ToListAsync();
        return View(n);
    }

    public async Task<IActionResult> Create()
    {
        if (!CanManage) return Denied();
        var n = new QcNonconformance { DueDate = DateTime.Today.AddDays(7) };
        await LoadAsync(n);
        return View("Form", n);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanManage) return Denied();
        var n = await Load(id);
        if (n == null) return NotFound();
        if (n.Status != NcrStatus.Open) { Error("NCR đã đóng."); return RedirectToAction(nameof(Details), new { id }); }
        await LoadAsync(n);
        return View("Form", n);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(QcNonconformance model)
    {
        if (!CanManage) return Denied();
        ModelState.Remove(nameof(model.Number)); // số NCR do hệ thống cấp
        var n = model.Id == 0 ? null : await Db.QcNonconformances.FindAsync(model.Id);
        if (model.Id != 0 && n == null) return NotFound();
        if (n != null && n.Status != NcrStatus.Open) { Error("NCR đã đóng."); return RedirectToAction(nameof(Details), new { id = n.Id }); }
        // Đã xuất kho theo hướng xử lý: khóa mặt hàng, số lượng, kho, hướng xử lý
        var locked = n?.InventoryDocId != null;
        if (locked && (model.ItemId != n!.ItemId || model.Quantity != n.Quantity || model.WarehouseId != n.WarehouseId || model.Disposition != n.Disposition))
            ModelState.AddModelError("", "Đã lập phiếu xuất kho theo NCR: không đổi mặt hàng, số lượng, kho, hướng xử lý.");
        if (model.Disposition == NcrDisposition.ReturnToVendor && model.PartnerId.HasValue && !await Db.Partners.AnyAsync(p => p.Id == model.PartnerId && p.IsVendor))
            ModelState.AddModelError(nameof(model.PartnerId), "Trả lại nhà cung cấp: đối tượng phải là nhà cung cấp.");
        if (!ModelState.IsValid) { await LoadAsync(model); return View("Form", model); }

        n ??= new QcNonconformance { Number = await _qc.NextNcrNumberAsync(model.Date), CreatedById = Me };
        n.Date = model.Date.Date; n.ItemId = model.ItemId; n.WarehouseId = model.WarehouseId; n.PartnerId = model.PartnerId; n.Quantity = model.Quantity;
        n.Description = model.Description.Trim(); n.RootCause = model.RootCause; n.Disposition = model.Disposition; n.CorrectiveAction = model.CorrectiveAction;
        n.Responsible = model.Responsible; n.DueDate = model.DueDate?.Date;
        if (n.Id == 0) Db.QcNonconformances.Add(n);
        await Db.SaveChangesAsync();
        Success($"Đã lưu {n.Number}.");
        return RedirectToAction(nameof(Details), new { id = n.Id });
    }

    /// <summary>Hủy bỏ / trả lại nhà cung cấp: lập và ghi sổ phiếu xuất kho</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(int id, DateTime? date)
    {
        if (!CanExecute) return Denied();
        var n = await Db.QcNonconformances.FindAsync(id);
        if (n == null) return NotFound();
        var doc = await _qc.ExecuteDispositionAsync(n, date ?? DateTime.Today);
        Success($"Đã lập và ghi sổ phiếu xuất kho {doc.Number} ({n.Disposition.GetDisplayName().ToLowerInvariant()}), giá trị {doc.TotalAmount.ToMoney()} đ.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id)
    {
        if (!CanManage) return Denied();
        var n = await Db.QcNonconformances.FindAsync(id);
        if (n == null) return NotFound();
        await _qc.CloseNcrAsync(n);
        Success($"Đã đóng {n.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private Task<QcNonconformance?> Load(int id) => Db.QcNonconformances.Include(n => n.Item).Include(n => n.Partner).Include(n => n.Warehouse)
        .Include(n => n.Inspection).Include(n => n.InventoryDoc).ThenInclude(d => d!.CounterAccount).Include(n => n.InventoryDoc).ThenInclude(d => d!.Lines).Include(n => n.CreatedBy).FirstOrDefaultAsync(n => n.Id == id);

    private async Task LoadAsync(QcNonconformance n)
    {
        ViewBag.Items = new SelectList(await Db.Items.Where(i => i.IsActive || i.Id == n.ItemId).OrderBy(i => i.Code)
            .Select(i => new { i.Id, Name = i.Code + " – " + i.Name }).ToListAsync(), "Id", "Name", n.ItemId);
        ViewBag.Whs = await WarehousesAsync(n.WarehouseId);
        ViewBag.Partners = await PartnersAsync(n.PartnerId);
        ViewBag.Locked = n.InventoryDocId != null;
    }
}
