using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Hoa hồng và KPI bán hàng: doanh số theo nhân viên, chỉ tiêu tháng, bậc hoa hồng theo doanh số, theo nhóm / phòng</summary>
[Authorize(Roles = AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class CommissionController : ErpControllerBase
{
    private readonly CommissionService _svc;
    public CommissionController(ErpDbContext db, CommissionService svc) : base(db) => _svc = svc;
    private bool CanConfig => User.InAny(AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director);

    public async Task<IActionResult> Index(int? year, int? month, bool export = false)
    {
        int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
        var rows = await _svc.MonthlyAsync(y, m);
        // Nhân viên kinh doanh chỉ xem số liệu của chính mình
        if (!User.InAny(AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)) rows = rows.Where(r => r.User.Id == Me).ToList();
        if (export)
        {
            var cols = new[] { "Nhân viên", "Phòng ban", "Số đơn", "Doanh số", "Trả hàng / giảm giá", "Doanh số thuần", "Chỉ tiêu", "% hoàn thành", "Tỷ lệ HH %", "Hoa hồng" }.Select((c, i) => new ReportExport.Col(c, i is 0 or 1 ? FieldKind.Text : FieldKind.Money)).ToList();
            return File(ReportExport.Xlsx("HoaHong", $"HOA HỒNG VÀ KPI BÁN HÀNG THÁNG {m:00}/{y}", new[] { "Doanh số = hóa đơn bán đã phát hành của đơn bán do nhân viên phụ trách" }, cols,
                rows.Select(r => new object?[] { r.User.FullName, r.User.Department, r.Orders, r.Revenue, r.Returns, r.Net, r.Target, r.AchievePct, r.Rate, r.Commission }), null),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Hoa_hong_{y}{m:00}.xlsx");
        }
        ViewBag.Year = y; ViewBag.Month = m; ViewBag.CanConfig = CanConfig;
        ViewBag.Tiers = await Db.CommissionTiers.Include(t => t.User).OrderBy(t => t.UserId).ThenBy(t => t.MinRevenue).ToListAsync();
        ViewBag.Users = new SelectList(await Db.Users.Where(u => u.IsActive && u.Role == AppRoles.Sales).OrderBy(u => u.FullName).Select(u => new { u.Id, Name = u.FullName }).ToListAsync(), "Id", "Name");
        ViewBag.Targets = await Db.SalesTargets.Where(t => t.Year == y && t.Month == m).ToDictionaryAsync(t => t.UserId, t => t.Amount);
        ViewBag.SalesUsers = await Db.Users.Where(u => u.IsActive && u.Role == AppRoles.Sales).OrderBy(u => u.FullName).ToListAsync();
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTargets(int year, int month, int[] userId, decimal[] amount)
    {
        if (!CanConfig) return Denied();
        var cur = await Db.SalesTargets.Where(t => t.Year == year && t.Month == month).ToListAsync();
        for (var i = 0; i < userId.Length; i++)
        {
            var t = cur.FirstOrDefault(x => x.UserId == userId[i]); var a = Math.Max(0, amount.ElementAtOrDefault(i));
            if (t == null) { if (a > 0) Db.SalesTargets.Add(new SalesTarget { UserId = userId[i], Year = year, Month = month, Amount = a }); } else t.Amount = a;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu chỉ tiêu doanh số tháng {month:00}/{year}.");
        return RedirectToAction(nameof(Index), new { year, month });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTier(CommissionTier m)
    {
        if (!CanConfig) return Denied();
        if (m.RatePct < 0 || m.RatePct > 100 || string.IsNullOrWhiteSpace(m.Name)) { Error("Nhập tên bậc và tỷ lệ 0–100%."); return RedirectToAction(nameof(Index)); }
        Db.CommissionTiers.Add(new CommissionTier { Name = m.Name.Trim(), UserId = m.UserId, Department = string.IsNullOrWhiteSpace(m.Department) ? null : m.Department.Trim(), MinRevenue = Math.Max(0, m.MinRevenue), RatePct = m.RatePct });
        await Db.SaveChangesAsync();
        Success("Đã thêm bậc hoa hồng.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTier(int id)
    {
        if (!CanConfig) return Denied();
        var t = await Db.CommissionTiers.FindAsync(id);
        if (t != null) { Db.CommissionTiers.Remove(t); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Index));
    }
}
