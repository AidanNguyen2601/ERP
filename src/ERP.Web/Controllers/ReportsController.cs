using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Báo cáo kế toán tổng hợp & kỳ kế toán</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class ReportsController : ErpControllerBase
{
    private readonly ReportService _rpt;
    private readonly PeriodService _periods;
    private readonly AccountingService _acc;
    public ReportsController(ErpDbContext db, ReportService rpt, PeriodService periods, AccountingService acc) : base(db) { _rpt = rpt; _periods = periods; _acc = acc; }

    public async Task<IActionResult> TrialBalance(DateTime? from, DateTime? to, bool export = false)
    {
        var (f, t) = Range(from, to);
        ViewBag.From = f; ViewBag.To = t;
        var tb = await _rpt.TrialBalanceAsync(f, t);
        if (export) return Xl("So_can_doi_TK", "BẢNG CÂN ĐỐI SỐ PHÁT SINH", $"Từ {f:dd/MM/yyyy} đến {t:dd/MM/yyyy}", new[] { "Số hiệu TK", "Tên tài khoản", "Dư Nợ đầu kỳ", "Dư Có đầu kỳ", "Phát sinh Nợ", "Phát sinh Có", "Dư Nợ cuối kỳ", "Dư Có cuối kỳ" }, new[] { FieldKind.Text, FieldKind.Text, FieldKind.Money, FieldKind.Money, FieldKind.Money, FieldKind.Money, FieldKind.Money, FieldKind.Money },
            tb.Select(r => new object?[] { r.Account.Code, r.Account.Name, r.OpenDebit, r.OpenCredit, r.Debit, r.Credit, r.CloseDebit, r.CloseCredit }));
        return View(tb);
    }

    public async Task<IActionResult> Ledger(string? account, DateTime? from, DateTime? to, int? partnerId, bool export = false)
    {
        var (f, t) = Range(from, to);
        account = string.IsNullOrWhiteSpace(account) ? "1111" : account.Trim();
        var acc = await Db.Accounts.FirstOrDefaultAsync(a => a.Code == account);
        if (acc == null) { Error($"Không có tài khoản {account}."); account = "1111"; acc = await Db.Accounts.FirstAsync(a => a.Code == account); }
        var (opening, rows) = await _rpt.LedgerAsync(account, f, t, partnerId);
        ViewBag.Account = acc; ViewBag.From = f; ViewBag.To = t; ViewBag.Opening = opening; ViewBag.PartnerId = partnerId;
        ViewBag.Accounts = await Db.Accounts.OrderBy(a => a.Code).ToListAsync();
        ViewBag.Partners = await PartnersAsync(partnerId);
        if (export) return Xl("So_cai", $"SỔ CÁI TÀI KHOẢN {acc.Code} – {acc.Name}", $"Từ {f:dd/MM/yyyy} đến {t:dd/MM/yyyy} · Số dư đầu kỳ {opening:N0}", new[] { "Ngày", "Số CT", "Diễn giải", "TK đối ứng", "Đối tượng", "Nợ", "Có", "Số dư" }, new[] { FieldKind.Text, FieldKind.Text, FieldKind.Text, FieldKind.Text, FieldKind.Text, FieldKind.Money, FieldKind.Money, FieldKind.Money },
            rows.Select(r => new object?[] { r.Date.ToString("dd/MM/yyyy"), r.Number, r.Description, r.CounterAccounts, r.PartnerName, r.Debit, r.Credit, r.Balance }));
        return View(rows);
    }

    public async Task<IActionResult> IncomeStatement(DateTime? from, DateTime? to, bool export = false)
    {
        var t = (to ?? DateTime.Today).Date;
        var f = (from ?? new DateTime(t.Year, 1, 1)).Date;
        // So sánh cùng kỳ năm trước
        var pf = f.AddYears(-1); var pt = t.AddYears(-1);
        ViewBag.From = f; ViewBag.To = t; ViewBag.PrevFrom = pf; ViewBag.PrevTo = pt;
        var is_ = await _rpt.IncomeStatementAsync(f, t, pf, pt);
        if (export) return Xl("KQHDKD", "BÁO CÁO KẾT QUẢ HOẠT ĐỘNG KINH DOANH (B02-DN)", $"Từ {f:dd/MM/yyyy} đến {t:dd/MM/yyyy}", new[] { "Mã số", "Chỉ tiêu", "Kỳ này", "Cùng kỳ năm trước" }, new[] { FieldKind.Text, FieldKind.Text, FieldKind.Money, FieldKind.Money },
            is_.Select(r => new object?[] { r.Code, new string(' ', r.Indent * 3) + r.Name, r.Amount, r.Previous }));
        return View(is_);
    }

    public async Task<IActionResult> BalanceSheet(DateTime? at, bool export = false)
    {
        var d = (at ?? DateTime.Today).Date;
        var (assets, sources) = await _rpt.BalanceSheetAsync(d);
        ViewBag.At = d; ViewBag.Sources = sources;
        if (export) return Xl("CDKT", "BẢNG CÂN ĐỐI KẾ TOÁN (B01-DN)", $"Tại ngày {d:dd/MM/yyyy}", new[] { "Mã số", "Chỉ tiêu", "Số cuối kỳ" }, new[] { FieldKind.Text, FieldKind.Text, FieldKind.Money },
            assets.Concat(sources).Select(r => new object?[] { r.Code, new string(' ', r.Indent * 3) + r.Name, r.Amount }));
        return View(assets);
    }

    private FileContentResult Xl(string name, string title, string sub, string[] cols, FieldKind[] kinds, IEnumerable<object?[]> rows) =>
        File(ReportExport.Xlsx(name, title, new[] { HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ErpSettings>>().Value.CompanyName, sub }, cols.Select((c, i) => new ReportExport.Col(c, kinds[i])).ToList(), rows, null),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}_{DateTime.Now:yyyyMMdd}.xlsx");

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
