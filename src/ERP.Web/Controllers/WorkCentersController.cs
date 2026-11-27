using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Trung tâm sản xuất / chuyền: năng lực (giờ, hiệu suất), đơn giá nhân công và SXC theo giờ, năng suất chuẩn</summary>
public class WorkCentersController : ErpControllerBase
{
    public WorkCentersController(ErpDbContext db) : base(db) { }
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam);

    public async Task<IActionResult> Index(int? edit)
    {
        ViewBag.CanEdit = CanEdit;
        ViewBag.Edit = edit == null ? new WorkCenter() : await Db.WorkCenters.FindAsync(edit) ?? new WorkCenter();
        // Số công đoạn dùng trung tâm và tải 30 ngày gần nhất
        var from = DateTime.Today.AddDays(-30);
        ViewBag.Used = (await Db.BomOperations.Where(o => o.WorkCenterId != null).GroupBy(o => o.WorkCenterId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
        ViewBag.Hours = (await Db.ProductionOutputs.Where(o => o.Date >= from && o.WorkCenterId != null).GroupBy(o => o.WorkCenterId!.Value).Select(g => new { g.Key, M = g.Sum(x => x.Minutes), Q = g.Sum(x => x.GoodQty) }).ToListAsync())
            .ToDictionary(x => x.Key, x => (Hours: x.M / 60m, Qty: x.Q));
        return View(await Db.WorkCenters.OrderBy(w => w.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(WorkCenter model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name)) { Error("Nhập mã và tên trung tâm sản xuất."); return RedirectToAction(nameof(Index)); }
        if (model.HoursPerDay < 0 || model.HoursPerDay > 24 || model.Efficiency <= 0 || model.LaborRatePerHour < 0 || model.OverheadRatePerHour < 0 || model.UnitsPerHour < 0)
        { Error("Số giờ làm 0–24, hiệu suất > 0, đơn giá không âm."); return RedirectToAction(nameof(Index), new { edit = model.Id == 0 ? (int?)null : model.Id }); }
        if (await Db.WorkCenters.AnyAsync(w => w.Code == model.Code && w.Id != model.Id)) { Error("Mã trung tâm đã tồn tại."); return RedirectToAction(nameof(Index)); }
        var w = model.Id == 0 ? new WorkCenter() : await Db.WorkCenters.FindAsync(model.Id) ?? new WorkCenter();
        if (w.Id == 0) Db.WorkCenters.Add(w);
        w.Code = model.Code; w.Name = model.Name.Trim(); w.Department = model.Department; w.HoursPerDay = model.HoursPerDay; w.Efficiency = model.Efficiency; w.Workers = Math.Max(1, model.Workers);
        w.LaborRatePerHour = model.LaborRatePerHour; w.OverheadRatePerHour = model.OverheadRatePerHour; w.UnitsPerHour = model.UnitsPerHour; w.IsActive = model.IsActive;
        await Db.SaveChangesAsync();
        Success($"Đã lưu trung tâm sản xuất {w.Code}.");
        return RedirectToAction(nameof(Index));
    }
}
