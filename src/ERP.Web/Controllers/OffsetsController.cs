using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Bù trừ công nợ phải thu – phải trả cùng đối tượng; lịch thanh toán nợ phải trả</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class OffsetsController : ErpControllerBase
{
    private readonly OffsetService _svc;
    public OffsetsController(ErpDbContext db, OffsetService svc) : base(db) => _svc = svc;

    public record Candidate(Partner Partner, decimal Ar, decimal Ap);

    public async Task<IActionResult> Index()
    {
        var open = await Db.Invoices.Include(i => i.Partner).Where(i => i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total && i.CurrencyCode == "VND" && i.PartnerId != null).ToListAsync();
        ViewBag.Candidates = open.GroupBy(i => i.PartnerId).Select(g => new Candidate(g.First().Partner!, g.Where(i => i.Direction == InvoiceDirection.Sales).Sum(i => i.Outstanding), g.Where(i => i.Direction == InvoiceDirection.Purchase).Sum(i => i.Outstanding)))
            .Where(c => c.Ar > 0 && c.Ap > 0).OrderBy(c => c.Partner.Code).ToList();
        return View(await Db.DebtOffsets.Include(o => o.Partner).Include(o => o.Lines).ThenInclude(l => l.Invoice).OrderByDescending(o => o.Date).ThenByDescending(o => o.Id).Take(200).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create(int partnerId, DateTime date, decimal? amount, string? note)
    {
        var off = await _svc.CreateAsync(partnerId, date, amount, note);
        Success($"Đã bù trừ công nợ {off.Number}: {off.Amount:N0} đ.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Cancel(int id, string reason)
    {
        var off = await Db.DebtOffsets.FindAsync(id); if (off == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) { Error("Nhập lý do hủy."); return RedirectToAction(nameof(Index)); }
        await _svc.CancelAsync(off, reason);
        Success($"Đã hủy bù trừ {off.Number}.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Lịch thanh toán nợ phải trả: theo hạn / ngày dự kiến trả, nhóm theo tuần; so với số dư tiền hiện có</summary>
    public async Task<IActionResult> Schedule([FromServices] CashService cash, int weeks = 8)
    {
        var list = await Db.Invoices.Include(i => i.Partner).Where(i => i.Direction == InvoiceDirection.Purchase && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).ToListAsync();
        ViewBag.Weeks = Math.Clamp(weeks, 2, 26);
        decimal funds = 0;
        foreach (var ca in await Db.CashAccounts.Include(c => c.Account).Where(c => c.IsActive && c.CurrencyCode == "VND").ToListAsync()) funds += await cash.BalanceAsync(ca);
        ViewBag.Funds = funds;
        var rec = await Db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).Select(i => new { i.DueDate, Amt = i.Total - i.PaidAmount }).ToListAsync();
        ViewBag.Receipts = rec.Select(x => (x.DueDate, x.Amt)).ToList();
        return View(list.OrderBy(i => i.PlannedPayDate ?? i.DueDate).ThenBy(i => i.Partner?.Code).ToList());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Plan(int[] invoiceId, DateTime?[] planned)
    {
        var invs = await Db.Invoices.Where(i => invoiceId.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        for (var k = 0; k < invoiceId.Length; k++) if (invs.TryGetValue(invoiceId[k], out var i)) i.PlannedPayDate = planned.ElementAtOrDefault(k)?.Date;
        await Db.SaveChangesAsync();
        Success("Đã cập nhật ngày dự kiến thanh toán.");
        return RedirectToAction(nameof(Schedule));
    }
}
