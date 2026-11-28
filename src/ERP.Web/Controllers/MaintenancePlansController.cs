using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Kế hoạch bảo dưỡng định kỳ (cả TPM và hiệu chuẩn): lịch năm / tháng / tuần, duyệt nhiều cấp, tự tạo phiếu khi đến hạn</summary>
public class MaintenancePlansController : ErpControllerBase
{
    private readonly MaintenanceService _svc;
    private readonly WorkflowService _wf;
    public MaintenancePlansController(ErpDbContext db, MaintenanceService svc, WorkflowService wf) : base(db) { _svc = svc; _wf = wf; }
    private bool CanEdit => User.InAny(AppRoles.MaintenanceTeam);

    public async Task<IActionResult> Index(MaintenancePlanStatus? status, MaintenanceKind? kind, int? equipmentId)
    {
        var q = Db.MaintenancePlans.Include(p => p.Equipment).Include(p => p.MaintenanceType).AsQueryable();
        if (status.HasValue) q = q.Where(p => p.Status == status);
        if (kind.HasValue) q = q.Where(p => p.Kind == kind);
        if (equipmentId.HasValue) q = q.Where(p => p.EquipmentId == equipmentId);
        ViewBag.Status = status; ViewBag.Kind = kind; ViewBag.EquipmentId = equipmentId; ViewBag.CanEdit = CanEdit;
        ViewBag.Counts = (await Db.MaintenancePlans.Select(p => p.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        ViewBag.Open = (await Db.MaintenanceOrders.Where(o => o.PlanId != null && o.Status != MaintenanceStatus.Accepted && o.Status != MaintenanceStatus.Cancelled).ToListAsync()).GroupBy(o => o.PlanId!.Value).ToDictionary(g => g.Key, g => g.First());
        ViewBag.Approval = await Db.ApprovalRequests.Where(r => r.DocType == ApprovalDocType.MaintenancePlan && r.Status == ApprovalStatus.Pending).Include(r => r.Steps).ToDictionaryAsync(r => r.DocId);
        return View(await q.OrderBy(p => p.NextDueDate).ToListAsync());
    }

    /// <summary>Lịch bảo dưỡng: view = year (số việc theo tháng × thiết bị), month (theo ngày), week (theo tuần)</summary>
    public async Task<IActionResult> Calendar(string view = "month", DateTime? date = null)
    {
        var d = (date ?? DateTime.Today).Date;
        DateTime from, to;
        switch (view)
        {
            case "year": from = new DateTime(d.Year, 1, 1); to = new DateTime(d.Year, 12, 31); break;
            case "week": from = d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); to = from.AddDays(6); break;
            default: view = "month"; from = new DateTime(d.Year, d.Month, 1); to = from.AddMonths(1).AddDays(-1); break;
        }
        var plans = await Db.MaintenancePlans.Include(p => p.Equipment).Where(p => p.Status == MaintenancePlanStatus.Active).ToListAsync();
        var items = plans.SelectMany(p => MaintenanceService.Occurrences(p, from, to).Select(x => (Plan: p, Due: x))).OrderBy(x => x.Due).ThenBy(x => x.Plan.Equipment!.Code).ToList();
        // Phiếu đã phát sinh trong khoảng (kể cả đã thực hiện) để thấy tiến độ thực tế
        ViewBag.Orders = await Db.MaintenanceOrders.Include(o => o.Equipment).Where(o => o.PlanId != null && o.DueDate >= from && o.DueDate <= to && o.Status != MaintenanceStatus.Cancelled).OrderBy(o => o.DueDate).ToListAsync();
        ViewBag.View = view; ViewBag.Date = d; ViewBag.From = from; ViewBag.To = to; ViewBag.CanEdit = CanEdit;
        return View(items);
    }

    public async Task<IActionResult> Create(int? equipmentId)
    {
        if (!CanEdit) return Denied();
        var p = new MaintenancePlan { EquipmentId = equipmentId };
        await LoadAsync(p);
        return View("Form", p);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var p = await Db.MaintenancePlans.FindAsync(id);
        if (p == null) return NotFound();
        await LoadAsync(p);
        return View("Form", p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(MaintenancePlan model, bool submit)
    {
        if (!CanEdit) return Denied();
        ModelState.Remove(nameof(model.Code));
        if (!ModelState.IsValid) { await LoadAsync(model); return View("Form", model); }
        var p = model.Id == 0 ? new MaintenancePlan { Code = await _svc.NextPlanCodeAsync(DateTime.Today), CreatedById = Me } : await Db.MaintenancePlans.FindAsync(model.Id) ?? new MaintenancePlan();
        if (p.Id != 0 && p.Status == MaintenancePlanStatus.PendingApproval) { Error("Kế hoạch đang chờ duyệt, không sửa được."); return RedirectToAction(nameof(Index)); }
        if (p.Id == 0) { Db.MaintenancePlans.Add(p); var eq = await Db.Equipments.FindAsync(model.EquipmentId); p.BranchId = eq?.BranchId; }
        var changed = p.Id != 0 && p.Status == MaintenancePlanStatus.Active && (p.IntervalDays != model.IntervalDays || p.EstimatedCost != model.EstimatedCost || p.Title != model.Title);
        p.EquipmentId = model.EquipmentId; p.MaintenanceTypeId = model.MaintenanceTypeId; p.Title = model.Title.Trim(); p.Kind = model.Kind; p.IntervalDays = model.IntervalDays;
        p.NextDueDate = model.NextDueDate.Date; p.EstimatedHours = model.EstimatedHours; p.EstimatedCost = model.EstimatedCost; p.LeadDays = model.LeadDays;
        p.Responsible = model.Responsible; p.Checklist = model.Checklist; p.Note = model.Note;
        if (changed) p.Status = MaintenancePlanStatus.Draft;   // thay đổi nội dung kế hoạch đang áp dụng phải duyệt lại
        await Db.SaveChangesAsync();
        if (submit && p.Status == MaintenancePlanStatus.Draft)
        {
            var msg = await _svc.SubmitPlanAsync(p);
            if (msg != null) TempData["Warning"] = msg; else Success($"Kế hoạch {p.Code} không cần duyệt theo quy tắc, đã áp dụng.");
        }
        else Success($"Đã lưu kế hoạch {p.Code}." + (changed ? " Nội dung đã thay đổi nên cần gửi duyệt lại." : ""));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        if (!CanEdit) return Denied();
        var p = await Db.MaintenancePlans.FindAsync(id);
        if (p == null) return NotFound();
        var msg = await _svc.SubmitPlanAsync(p);
        if (msg != null) TempData["Warning"] = msg; else Success($"Kế hoạch {p.Code} không cần duyệt theo quy tắc, đã áp dụng.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        if (!CanEdit) return Denied();
        var p = await Db.MaintenancePlans.FindAsync(id);
        if (p == null) return NotFound();
        if (p.Status == MaintenancePlanStatus.Active) p.Status = MaintenancePlanStatus.Suspended;
        else if (p.Status == MaintenancePlanStatus.Suspended) p.Status = MaintenancePlanStatus.Active;
        else throw new BusinessException("Chỉ tạm ngưng / áp dụng lại kế hoạch đã duyệt.");
        await Db.SaveChangesAsync();
        Success(p.Status == MaintenancePlanStatus.Active ? $"Kế hoạch {p.Code} áp dụng trở lại." : $"Đã tạm ngưng kế hoạch {p.Code}.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Tạo phiếu bảo dưỡng cho các kế hoạch đã đến hạn</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate()
    {
        if (!CanEdit) return Denied();
        var made = await _svc.GenerateDueAsync(DateTime.Today);
        if (made.Count == 0) Success("Không có kế hoạch nào đến hạn cần tạo phiếu.");
        else Success($"Đã tạo {made.Count} phiếu bảo dưỡng: {string.Join(", ", made.Select(m => m.Number))}.");
        return RedirectToAction("Index", "MaintenanceOrders");
    }

    private async Task LoadAsync(MaintenancePlan p)
    {
        ViewBag.Equipments = new SelectList(await Db.Equipments.Where(e => e.Status != EquipmentStatus.Retired || e.Id == p.EquipmentId).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.Name }).ToListAsync(), "Id", "Name", p.EquipmentId);
        ViewBag.Types = await Db.MaintenanceTypes.Where(t => t.IsActive || t.Id == p.MaintenanceTypeId).OrderBy(t => t.Code).ToListAsync();
    }
}
