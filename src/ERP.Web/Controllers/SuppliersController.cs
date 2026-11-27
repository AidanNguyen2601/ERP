using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Hồ sơ và đánh giá nhà cung cấp: thông tin pháp lý, phân loại, chấm điểm định kỳ, chỉ số tự động từ nhận hàng và kiểm tra chất lượng</summary>
[Authorize(Roles = AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Quality)]
public class SuppliersController : ErpControllerBase
{
    private readonly SupplierService _svc;
    public SuppliersController(ErpDbContext db, SupplierService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index(string? grade)
    {
        var from = DateTime.Today.AddMonths(-12); var to = DateTime.Today;
        var vendors = await Db.Partners.Where(p => p.IsVendor && (grade == null || p.Grade == grade)).OrderBy(p => p.Code).ToListAsync();
        var stats = new Dictionary<int, SupplierStats>();
        foreach (var v in vendors) stats[v.Id] = await _svc.StatsAsync(v.Id, from, to);
        ViewBag.Stats = stats; ViewBag.Grade = grade;
        ViewBag.LastEval = (await Db.SupplierEvaluations.OrderByDescending(e => e.Date).ToListAsync()).GroupBy(e => e.PartnerId).ToDictionary(g => g.Key, g => g.First());
        return View(vendors);
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await Db.Partners.FindAsync(id);
        if (p == null || !p.IsVendor) return NotFound();
        ViewBag.Evals = await Db.SupplierEvaluations.Where(e => e.PartnerId == id).OrderByDescending(e => e.Date).ToListAsync();
        ViewBag.Stats = await _svc.StatsAsync(id, DateTime.Today.AddMonths(-12), DateTime.Today);
        ViewBag.Orders = await Db.PurchaseOrders.Where(o => o.VendorId == id).OrderByDescending(o => o.Date).Take(10).ToListAsync();
        ViewBag.CanEval = User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Quality);
        return View(p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(SupplierEvaluation m)
    {
        if (!User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Quality)) return Denied();
        if (new[] { m.Quality, m.Delivery, m.Price, m.Service }.Any(x => x < 0 || x > 10)) { Error("Điểm từ 0 đến 10."); return RedirectToAction(nameof(Details), new { id = m.PartnerId }); }
        m.Id = 0; m.Date = DateTime.Today; if (string.IsNullOrWhiteSpace(m.Period)) m.Period = $"{DateTime.Today:MM/yyyy}";
        var e = await _svc.EvaluateAsync(m, User.GetFullName());
        Success($"Đã ghi nhận đánh giá: {e.Total:0.0} điểm – xếp hạng {e.Grade}.");
        return RedirectToAction(nameof(Details), new { id = m.PartnerId });
    }
}
