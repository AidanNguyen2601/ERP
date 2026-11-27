using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Giá thành: tập hợp chi phí NVL – nhân công – SXC, phân bổ chi phí SXC theo tiêu thức, giá thành theo lệnh / sản phẩm / đơn hàng so với định mức</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Production)]
public class CostingController : ErpControllerBase
{
    private readonly CostingService _svc;
    public CostingController(ErpDbContext db, CostingService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string by = "order", int? year = null, int? month = null)
    {
        var (f, t) = Range(from ?? new DateTime(DateTime.Today.Year, 1, 1), to);
        int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
        ViewBag.From = f; ViewBag.To = t; ViewBag.By = by; ViewBag.Year = y; ViewBag.Month = m;
        ViewBag.Pool = await _svc.OverheadPoolAsync(y, m);
        ViewBag.Wip = await Db.ProductionOrders.Include(p => p.Item).Where(p => p.Status == ProductionStatus.InProgress).OrderBy(p => p.Number).ToListAsync();
        return View(await _svc.ReportAsync(f, t, by));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Allocate(int year, int month, string basis, decimal? amount)
    {
        var (amt, n) = await _svc.AllocateOverheadAsync(year, month, basis, amount is > 0 ? amount : null);
        Success($"Đã phân bổ {amt:N0} đ chi phí sản xuất chung tháng {month:00}/{year} cho {n} lệnh sản xuất (Nợ 154 / Có 627).");
        return RedirectToAction(nameof(Index), new { year, month });
    }
}
