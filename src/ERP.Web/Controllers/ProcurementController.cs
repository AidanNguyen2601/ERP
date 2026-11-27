using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Kiểm soát mua hàng: đối chiếu 3 bên (đơn mua – phiếu nhập – hóa đơn) và chi phí mua hàng phân bổ vào giá nhập</summary>
[Authorize(Roles = AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Warehouse)]
public class ProcurementController : ErpControllerBase
{
    public ProcurementController(ErpDbContext db) : base(db) { }

    public async Task<IActionResult> Match(DateTime? from, DateTime? to, int? vendorId, [FromServices] ThreeWayMatchService svc, bool issues = true)
    {
        var (f, t) = Range(from ?? DateTime.Today.AddMonths(-3), to);
        ViewBag.From = f; ViewBag.To = t; ViewBag.VendorId = vendorId; ViewBag.Issues = issues;
        ViewBag.Vendors = await VendorsAsync(vendorId);
        return View(await svc.RunAsync(f, t, vendorId, issues));
    }

    public async Task<IActionResult> LandedCosts()
    {
        ViewBag.Orders = new SelectList(await Db.PurchaseOrders.Include(p => p.Vendor).Where(p => p.Status == PoStatus.PartiallyReceived || p.Status == PoStatus.Received || p.Status == PoStatus.Completed).OrderByDescending(p => p.Date)
            .Select(p => new { p.Id, Name = p.Number + " – " + p.Vendor!.Name }).ToListAsync(), "Id", "Name");
        ViewBag.Counter = await AccountSelectAsync(null, a => new[] { "331", "111", "112", "3333" }.Any(p => a.Code.StartsWith(p)));
        ViewBag.Partners = await PartnersAsync();
        return View(await Db.LandedCosts.Include(l => l.PurchaseOrder).Include(l => l.Partner).Include(l => l.Lines).OrderByDescending(l => l.Date).ThenByDescending(l => l.Id).Take(200).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting + "," + AppRoles.Purchasing)]
    public async Task<IActionResult> CreateLandedCost(int poId, DateTime date, LandedCostKind kind, string description, decimal amount, int vatRate, string basis, int counterAccountId, int? partnerId, string? vendorInvoiceNo, string? vendorSeries, [FromServices] LandedCostService svc)
    {
        var lc = await svc.CreateAsync(poId, date, kind, string.IsNullOrWhiteSpace(description) ? kind.GetDisplayName() : description, amount, vatRate, basis, counterAccountId, partnerId, vendorInvoiceNo, vendorSeries);
        Success($"Đã ghi nhận chi phí mua hàng {lc.Number}: {amount:N0} đ phân bổ cho {lc.Lines.Count} dòng hàng (vào tồn kho {lc.Lines.Sum(l => l.ToInventory):N0}, vào giá vốn {lc.Lines.Sum(l => l.ToCogs):N0}).");
        return RedirectToAction(nameof(LandedCosts));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> CancelLandedCost(int id, string reason, [FromServices] LandedCostService svc)
    {
        var lc = await Db.LandedCosts.Include(l => l.Lines).FirstOrDefaultAsync(l => l.Id == id); if (lc == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) { Error("Nhập lý do hủy."); return RedirectToAction(nameof(LandedCosts)); }
        await svc.CancelAsync(lc, reason);
        Success($"Đã hủy {lc.Number}.");
        return RedirectToAction(nameof(LandedCosts));
    }
}
