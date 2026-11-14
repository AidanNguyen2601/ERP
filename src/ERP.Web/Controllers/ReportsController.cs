using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Báo cáo kế toán tổng hợp & kỳ kế toán</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class ReportsController : ErpControllerBase
{
    private readonly ReportService _rpt;
    private readonly PeriodService _periods;
    private readonly AccountingService _acc;
    public ReportsController(ErpDbContext db, ReportService rpt, PeriodService periods, AccountingService acc) : base(db) { _rpt = rpt; _periods = periods; _acc = acc; }

    public async Task<IActionResult> TrialBalance(DateTime? from, DateTime? to)
    {
        var (f, t) = Range(from, to);
        ViewBag.From = f; ViewBag.To = t;
        return View(await _rpt.TrialBalanceAsync(f, t));
    }

    public async Task<IActionResult> Ledger(string? account, DateTime? from, DateTime? to, int? partnerId)
    {
        var (f, t) = Range(from, to);
        account = string.IsNullOrWhiteSpace(account) ? "1111" : account.Trim();
        var acc = await Db.Accounts.FirstOrDefaultAsync(a => a.Code == account);
        if (acc == null) { Error($"Không có tài khoản {account}."); account = "1111"; acc = await Db.Accounts.FirstAsync(a => a.Code == account); }
        var (opening, rows) = await _rpt.LedgerAsync(account, f, t, partnerId);
        ViewBag.Account = acc; ViewBag.From = f; ViewBag.To = t; ViewBag.Opening = opening; ViewBag.PartnerId = partnerId;
        ViewBag.Accounts = await Db.Accounts.OrderBy(a => a.Code).ToListAsync();
        ViewBag.Partners = await PartnersAsync(partnerId);
        return View(rows);
    }

    public async Task<IActionResult> IncomeStatement(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.Today).Date;
        var f = (from ?? new DateTime(t.Year, 1, 1)).Date;
        // So sánh cùng kỳ năm trước
        var pf = f.AddYears(-1); var pt = t.AddYears(-1);
        ViewBag.From = f; ViewBag.To = t; ViewBag.PrevFrom = pf; ViewBag.PrevTo = pt;
        return View(await _rpt.IncomeStatementAsync(f, t, pf, pt));
    }

    public async Task<IActionResult> BalanceSheet(DateTime? at)
    {
        var d = (at ?? DateTime.Today).Date;
        var (assets, sources) = await _rpt.BalanceSheetAsync(d);
        ViewBag.At = d; ViewBag.Sources = sources;
        return View(assets);
    }

    // ------------------------------------------------------------------ Kỳ kế toán
    public async Task<IActionResult> Periods()
    {
        var first = await Db.JournalEntries.OrderBy(j => j.Date).Select(j => (DateTime?)j.Date).FirstOrDefaultAsync() ?? DateTime.Today;
        var stored = await Db.FiscalPeriods.ToListAsync();
        var list = new List<(int Year, int Month, FiscalPeriod? P, bool Closing, decimal Revenue, decimal Profit)>();
        for (var m = new DateTime(first.Year, first.Month, 1); m <= DateTime.Today; m = m.AddMonths(1))
        {
            var end = m.AddMonths(1).AddDays(-1);
            var rev = -await _acc.MovementAsync("511", m, end);
            var profit = -(await _acc.MovementAsync("5", m, end) + await _acc.MovementAsync("6", m, end) + await _acc.MovementAsync("7", m, end) + await _acc.MovementAsync("8", m, end));
            list.Add((m.Year, m.Month, stored.FirstOrDefault(p => p.Year == m.Year && p.Month == m.Month), await _periods.HasClosingEntryAsync(m.Year, m.Month), rev, profit));
        }
        list.Reverse();
        return View(list);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> ClosingEntry(int year, int month)
    {
        var je = await _periods.CloseEntriesAsync(year, month);
        if (je == null) Error("Không có doanh thu / chi phí để kết chuyển.");
        else Success($"Đã kết chuyển kết quả kinh doanh tháng {month:00}/{year}: bút toán {je.Number}.");
        return RedirectToAction(nameof(Periods));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Close(int year, int month, bool withClosing = true)
    {
        await _periods.CloseAsync(year, month, withClosing);
        Success($"Đã khóa sổ kỳ {month:00}/{year}. Mọi chứng từ có ngày trong kỳ này sẽ không ghi sổ được.");
        return RedirectToAction(nameof(Periods));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Approvers + "," + AppRoles.Accountant)]
    public async Task<IActionResult> Reopen(int year, int month)
    {
        await _periods.ReopenAsync(year, month);
        Success($"Đã mở khóa kỳ {month:00}/{year}.");
        return RedirectToAction(nameof(Periods));
    }
}
