using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Kế toán công nợ: phải thu (kind=ar, TK 131) và phải trả (kind=ap, TK 331)</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class DebtsController : ErpControllerBase
{
    private readonly AccountingService _acc;
    private readonly ReportService _rpt;
    public DebtsController(ErpDbContext db, AccountingService acc, ReportService rpt) : base(db) { _acc = acc; _rpt = rpt; }

    private static (bool Ar, string Acc, InvoiceDirection Dir, string Title) Kind(string? kind) =>
        kind == "ap" ? (false, "331", InvoiceDirection.Purchase, "phải trả") : (true, "131", InvoiceDirection.Sales, "phải thu");

    public async Task<IActionResult> Index(string? kind)
    {
        var k = Kind(kind);
        var bal = await _acc.PartnerBalancesAsync(k.Acc);
        var partners = await Db.Partners.Where(p => k.Ar ? p.IsCustomer : p.IsVendor).OrderBy(p => p.Code).ToListAsync();
        var open = await Db.Invoices.Where(i => i.Direction == k.Dir && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).ToListAsync();
        var since = DateTime.Today.AddDays(-30);
        var vouchers = await Db.CashVouchers.Include(v => v.Partner).Include(v => v.CashAccount)
            .Where(v => v.Status == DocStatus.Posted && v.Purpose == (k.Ar ? CashPurpose.CustomerReceipt : CashPurpose.VendorPayment) && v.Date >= since)
            .OrderByDescending(v => v.Date).ThenByDescending(v => v.Id).Take(15).ToListAsync();
        ViewBag.Kind = k.Ar ? "ar" : "ap"; ViewBag.Title2 = k.Title;
        ViewBag.Rows = partners.Select(p => (Partner: p, Balance: bal.GetValueOrDefault(p.Id) * (k.Ar ? 1 : -1),
            Open: open.Where(i => i.PartnerId == p.Id).ToList())).Where(x => x.Balance != 0 || x.Open.Any()).ToList();
        ViewBag.Aging = await _rpt.AgingAsync(k.Dir, DateTime.Today);
        return View(vouchers);
    }

    public async Task<IActionResult> Aging(string? kind, DateTime? at)
    {
        var k = Kind(kind);
        var d = (at ?? DateTime.Today).Date;
        ViewBag.Kind = k.Ar ? "ar" : "ap"; ViewBag.At = d; ViewBag.Title2 = k.Title;
        return View(await _rpt.AgingAsync(k.Dir, d));
    }

    /// <summary>Sổ chi tiết công nợ một đối tượng + hóa đơn còn nợ</summary>
    public async Task<IActionResult> Statement(int id, string? kind, DateTime? from, DateTime? to)
    {
        var k = Kind(kind);
        var p = await Db.Partners.FindAsync(id);
        if (p == null) return NotFound();
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-6)).Date;
        var (opening, rows) = await _rpt.LedgerAsync(k.Acc, f, t, id);
        ViewBag.Partner = p; ViewBag.Kind = k.Ar ? "ar" : "ap"; ViewBag.Title2 = k.Title; ViewBag.From = f; ViewBag.To = t; ViewBag.Opening = opening;
        ViewBag.Invoices = await Db.Invoices.Where(i => i.PartnerId == id && i.Direction == k.Dir && i.Status == InvoiceStatus.Issued).OrderByDescending(i => i.Date).ToListAsync();
        return View(rows);
    }
}
