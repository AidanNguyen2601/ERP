using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Tổng quan bảo dưỡng; lịch sử – thời gian dừng máy – chi phí; chỉ số MTBF / MTTR / độ sẵn sàng; chi phí so với ngân sách</summary>
public class MaintenanceReportsController : ErpControllerBase
{
    private readonly MaintenanceService _svc;
    public MaintenanceReportsController(ErpDbContext db, MaintenanceService svc) : base(db) => _svc = svc;

    /// <summary>Bảng điều khiển: thiết bị đang dừng, kế hoạch đến hạn / quá hạn, sửa chữa đang mở, giấy tờ sắp hết hạn</summary>
    public async Task<IActionResult> Index()
    {
        var today = DateTime.Today;
        ViewBag.Down = await Db.Equipments.Where(e => e.Status == EquipmentStatus.Down || e.Status == EquipmentStatus.UnderMaintenance).OrderBy(e => e.Code).ToListAsync();
        var plans = await Db.MaintenancePlans.Include(p => p.Equipment).Where(p => p.Status == MaintenancePlanStatus.Active).OrderBy(p => p.NextDueDate).ToListAsync();
        ViewBag.Overdue = plans.Where(p => p.NextDueDate.Date < today).ToList();
        ViewBag.Upcoming = plans.Where(p => p.NextDueDate.Date >= today && p.NextDueDate.Date <= today.AddDays(14)).ToList();
        ViewBag.OpenOrders = await Db.MaintenanceOrders.Include(o => o.Equipment).Where(o => o.Status != MaintenanceStatus.Accepted && o.Status != MaintenanceStatus.Cancelled).OrderByDescending(o => o.Priority).ThenBy(o => o.DueDate).Take(15).ToListAsync();
        ViewBag.Expiring = await Db.EquipmentDocuments.Include(d => d.Equipment).Where(d => d.ExpiryDate != null && d.ExpiryDate <= today.AddDays(45)).OrderBy(d => d.ExpiryDate).ToListAsync();
        ViewBag.Warranty = await Db.Equipments.Where(e => e.WarrantyUntil != null && e.WarrantyUntil >= today && e.WarrantyUntil <= today.AddDays(45)).ToListAsync();
        var from = today.AddMonths(-12);
        var kpis = await _svc.KpiAsync(from, today);
        ViewBag.Total = (Breakdowns: kpis.Sum(k => k.Breakdowns), Downtime: kpis.Sum(k => k.DowntimeHours), Cost: kpis.Sum(k => k.TotalCost),
            Mttr: kpis.Sum(k => k.Breakdowns) == 0 ? 0 : kpis.Sum(k => k.RepairHours) / kpis.Sum(k => k.Breakdowns),
            Pm: kpis.Sum(k => k.PmDue) == 0 ? 100m : kpis.Sum(k => k.PmOnTime) * 100m / kpis.Sum(k => k.PmDue));
        return View();
    }

    public async Task<IActionResult> Kpi(DateTime? from, DateTime? to, decimal hours = 8)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-12)).Date;
        ViewBag.From = f; ViewBag.To = t; ViewBag.Hours = hours;
        return View((await _svc.KpiAsync(f, t, hours)).OrderByDescending(k => k.TotalCost).ToList());
    }

    /// <summary>Lịch sử bảo trì: từng phiếu, thời gian dừng máy và chi phí; lọc theo thiết bị</summary>
    public async Task<IActionResult> History(int? equipmentId, DateTime? from, DateTime? to, MaintenanceKind? kind)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-12)).Date;
        var q = Db.MaintenanceOrders.Include(o => o.Equipment).Where(o => o.Status == MaintenanceStatus.Completed || o.Status == MaintenanceStatus.Accepted)
            .Where(o => o.CompletedAt >= f && o.CompletedAt < t.AddDays(1));
        if (equipmentId.HasValue) q = q.Where(o => o.EquipmentId == equipmentId);
        if (kind.HasValue) q = q.Where(o => o.Kind == kind);
        ViewBag.From = f; ViewBag.To = t; ViewBag.EquipmentId = equipmentId; ViewBag.Kind = kind;
        ViewBag.Equipments = await Db.Equipments.OrderBy(e => e.Code).ToListAsync();
        return View(await q.OrderByDescending(o => o.CompletedAt).ToListAsync());
    }

    /// <summary>Chi phí bảo dưỡng theo tháng (phụ tùng, nhân công, thuê ngoài) so với ngân sách khoản mục tương ứng</summary>
    public async Task<IActionResult> Costs(int? year)
    {
        var y = year ?? DateTime.Today.Year;
        var orders = await Db.MaintenanceOrders.Include(o => o.Equipment).Where(o => (o.Status == MaintenanceStatus.Completed || o.Status == MaintenanceStatus.Accepted) && o.CompletedAt != null && o.CompletedAt.Value.Year == y).ToListAsync();
        var budgets = await Db.Budgets.Include(b => b.Lines).Where(b => b.IsActive && b.Year == y).ToListAsync();
        var lines = budgets.SelectMany(b => b.Lines).Where(l => l.AccountPrefix.StartsWith("6272") || l.AccountPrefix.StartsWith("627") && (l.Note ?? "").Contains("bảo dưỡng", StringComparison.OrdinalIgnoreCase) || (l.Note ?? "").Contains("bảo dưỡng", StringComparison.OrdinalIgnoreCase)).ToList();
        ViewBag.Year = y; ViewBag.BudgetLines = lines;
        return View(orders);
    }
}
