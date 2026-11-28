using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Danh mục máy móc thiết bị: hồ sơ thiết bị, tài liệu / kiểm định, loại bảo dưỡng</summary>
public class EquipmentController : ErpControllerBase
{
    private readonly MaintenanceService _svc;
    public EquipmentController(ErpDbContext db, MaintenanceService svc) : base(db) => _svc = svc;
    private bool CanEdit => User.InAny(AppRoles.MaintenanceTeam);

    public async Task<IActionResult> Index(EquipmentStatus? status, string? dept, string? q)
    {
        var query = Db.Equipments.Include(e => e.WorkCenter).Include(e => e.FixedAsset).AsQueryable();
        if (status.HasValue) query = query.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(dept)) query = query.Where(e => e.Department == dept);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(e => e.Code.Contains(q) || e.Name.Contains(q) || (e.SerialNo != null && e.SerialNo.Contains(q)) || (e.Model != null && e.Model.Contains(q)));
        ViewBag.Status = status; ViewBag.Dept = dept; ViewBag.Q = q; ViewBag.CanEdit = CanEdit;
        ViewBag.Depts = await Db.Equipments.Where(e => e.Department != null).Select(e => e.Department!).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.NextDue = (await Db.MaintenancePlans.Where(p => p.Status == MaintenancePlanStatus.Active).ToListAsync()).GroupBy(p => p.EquipmentId!.Value).ToDictionary(g => g.Key, g => g.Min(p => p.NextDueDate));
        ViewBag.Open = (await Db.MaintenanceOrders.Where(o => o.Status != MaintenanceStatus.Accepted && o.Status != MaintenanceStatus.Cancelled).Select(o => o.EquipmentId!.Value).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var linked = await Db.Equipments.Where(e => e.FixedAssetId != null).Select(e => e.FixedAssetId!.Value).ToListAsync();
        ViewBag.AssetsWithout = CanEdit ? await Db.FixedAssets.Include(a => a.Category).Where(a => a.Status != AssetStatus.Disposed && !linked.Contains(a.Id)).OrderBy(a => a.Code).Take(30).ToListAsync() : new List<FixedAsset>();
        return View(await query.OrderBy(e => e.Code).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var e = await Db.Equipments.Include(x => x.WorkCenter).Include(x => x.FixedAsset).Include(x => x.ExpenseAccount).Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return NotFound();
        ViewBag.CanEdit = CanEdit;
        ViewBag.Plans = await Db.MaintenancePlans.Include(p => p.MaintenanceType).Where(p => p.EquipmentId == id).OrderBy(p => p.NextDueDate).ToListAsync();
        ViewBag.Orders = await Db.MaintenanceOrders.Where(o => o.EquipmentId == id).OrderByDescending(o => o.ReportedAt).Take(30).ToListAsync();
        var to = DateTime.Today; var from = to.AddMonths(-12);
        ViewBag.Kpi = (await _svc.KpiAsync(from, to)).FirstOrDefault(k => k.Equipment.Id == id);
        return View(e);
    }

    public async Task<IActionResult> Create(int? assetId)
    {
        if (!CanEdit) return Denied();
        var m = new Equipment();
        if (assetId != null)
        {
            var a = await Db.FixedAssets.FindAsync(assetId);
            if (a != null) { m.FixedAssetId = a.Id; m.Code = "TB-" + a.Code; m.Name = a.Name; m.Department = a.Department; m.SerialNo = a.SerialNo; m.InstallDate = a.AcquisitionDate; }
        }
        await LoadAsync(m);
        return View("Form", m);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var m = await Db.Equipments.FindAsync(id);
        if (m == null) return NotFound();
        await LoadAsync(m);
        return View("Form", m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Equipment model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (await Db.Equipments.AnyAsync(e => e.Code == model.Code && e.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã thiết bị đã tồn tại");
        if (model.FixedAssetId != null && await Db.Equipments.AnyAsync(e => e.FixedAssetId == model.FixedAssetId && e.Id != model.Id)) ModelState.AddModelError(nameof(model.FixedAssetId), "Tài sản này đã có hồ sơ thiết bị khác");
        if (!ModelState.IsValid) { await LoadAsync(model); return View("Form", model); }
        var e = model.Id == 0 ? new Equipment() : await Db.Equipments.FindAsync(model.Id) ?? new Equipment();
        if (e.Id == 0) Db.Equipments.Add(e);
        e.Code = model.Code; e.Name = model.Name.Trim(); e.Model = model.Model; e.Manufacturer = model.Manufacturer; e.SerialNo = model.SerialNo; e.FixedAssetId = model.FixedAssetId;
        e.WorkCenterId = model.WorkCenterId; e.Department = model.Department; e.Location = model.Location; e.Criticality = model.Criticality; e.InstallDate = model.InstallDate;
        e.WarrantyUntil = model.WarrantyUntil; e.ExpenseAccountId = model.ExpenseAccountId; e.Note = model.Note;
        if (model.Status != e.Status && (e.Id == 0 || !Db.MaintenanceOrders.Any(o => o.EquipmentId == e.Id && o.Status == MaintenanceStatus.InProgress))) e.Status = model.Status;
        await Db.SaveChangesAsync();
        Success($"Đã lưu hồ sơ thiết bị {e.Code}.");
        return RedirectToAction(nameof(Details), new { id = e.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDocument(int id, string title, string? docType, string? number, DateTime? issuedDate, DateTime? expiryDate, string? note)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(title)) { Error("Nhập tên tài liệu."); return RedirectToAction(nameof(Details), new { id }); }
        Db.EquipmentDocuments.Add(new EquipmentDocument { EquipmentId = id, Title = title.Trim(), DocType = docType, Number = number, IssuedDate = issuedDate, ExpiryDate = expiryDate, Note = note });
        await Db.SaveChangesAsync();
        Success("Đã thêm tài liệu vào hồ sơ.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDocument(int id, int docId)
    {
        if (!CanEdit) return Denied();
        var d = await Db.EquipmentDocuments.FirstOrDefaultAsync(x => x.Id == docId && x.EquipmentId == id);
        if (d != null) { Db.EquipmentDocuments.Remove(d); await Db.SaveChangesAsync(); }
        Success("Đã xóa tài liệu.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Loại bảo dưỡng
    public async Task<IActionResult> Types(int? edit)
    {
        ViewBag.CanEdit = CanEdit;
        ViewBag.Edit = edit == null ? new MaintenanceType() : await Db.MaintenanceTypes.FindAsync(edit) ?? new MaintenanceType();
        ViewBag.Used = (await Db.MaintenancePlans.Where(p => p.MaintenanceTypeId != null).GroupBy(p => p.MaintenanceTypeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
        return View(await Db.MaintenanceTypes.OrderBy(t => t.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveType(MaintenanceType model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name)) { Error("Nhập mã và tên loại bảo dưỡng."); return RedirectToAction(nameof(Types)); }
        if (model.DefaultIntervalDays < 1) { Error("Chu kỳ tối thiểu 1 ngày."); return RedirectToAction(nameof(Types)); }
        if (await Db.MaintenanceTypes.AnyAsync(t => t.Code == model.Code && t.Id != model.Id)) { Error("Mã loại đã tồn tại."); return RedirectToAction(nameof(Types)); }
        var t = model.Id == 0 ? new MaintenanceType() : await Db.MaintenanceTypes.FindAsync(model.Id) ?? new MaintenanceType();
        if (t.Id == 0) Db.MaintenanceTypes.Add(t);
        t.Code = model.Code; t.Name = model.Name.Trim(); t.Kind = model.Kind; t.DefaultIntervalDays = model.DefaultIntervalDays; t.Note = model.Note; t.IsActive = model.IsActive;
        await Db.SaveChangesAsync();
        Success($"Đã lưu loại bảo dưỡng {t.Code}.");
        return RedirectToAction(nameof(Types));
    }

    private async Task LoadAsync(Equipment m)
    {
        ViewBag.Assets = new SelectList(await Db.FixedAssets.Where(a => a.Status != AssetStatus.Disposed || a.Id == m.FixedAssetId).OrderBy(a => a.Code).Select(a => new { a.Id, Name = a.Code + " – " + a.Name }).ToListAsync(), "Id", "Name", m.FixedAssetId);
        ViewBag.Centers = new SelectList(await Db.WorkCenters.OrderBy(w => w.Code).Select(w => new { w.Id, Name = w.Code + " – " + w.Name }).ToListAsync(), "Id", "Name", m.WorkCenterId);
        ViewBag.Accounts = await AccountSelectAsync(m.ExpenseAccountId, a => a.Code.StartsWith("627") || a.Code.StartsWith("641") || a.Code.StartsWith("642") || a.Code.StartsWith("811"));
    }
}
