using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Bút toán định kỳ (khấu trừ cố định hằng tháng) và bút toán phân bổ (chia số phát sinh của TK nguồn cho nhiều TK / bộ phận theo tỷ lệ)</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class RecurringController : ErpControllerBase
{
    private readonly RecurringService _svc;
    public RecurringController(ErpDbContext db, RecurringService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index() =>
        View(await Db.RecurringJournals.Include(r => r.Lines).ThenInclude(l => l.Account).Include(r => r.SourceAccount).OrderBy(r => r.NextDate).ToListAsync());

    private async Task BagAsync()
    {
        ViewBag.Accounts = await PostableAccountsAsync();
        ViewBag.Partners = await PartnersAsync();
        ViewBag.Depts = await Db.CatalogItems.Where(c => c.Type == CatalogType.Department).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(int? id, bool allocation = false)
    {
        var r = id == null ? new RecurringJournal { IsAllocation = allocation, NextDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1).AddDays(-1) }
            : await Db.RecurringJournals.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();
        if (r.Lines.Count == 0) { r.Lines.Add(new RecurringJournalLine()); r.Lines.Add(new RecurringJournalLine()); }
        await BagAsync();
        return View(r);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(RecurringJournal m, List<RecurringJournalLine> lines)
    {
        lines = lines.Where(l => l.AccountId > 0 && (l.Debit != 0 || l.Credit != 0 || l.Percent != 0)).ToList();
        ModelState.Clear();
        if (string.IsNullOrWhiteSpace(m.Name)) ModelState.AddModelError("", "Nhập tên mẫu.");
        if (string.IsNullOrWhiteSpace(m.Description)) m.Description = m.Name;
        if (m.IsAllocation)
        {
            if (m.SourceAccountId == null) ModelState.AddModelError("", "Chọn TK nguồn cần phân bổ.");
            if (lines.Count == 0 || Math.Abs(lines.Sum(l => l.Percent) - 100) > 0.001m) ModelState.AddModelError("", "Tổng tỷ lệ phân bổ phải bằng 100%.");
        }
        else
        {
            if (lines.Count < 2) ModelState.AddModelError("", "Nhập ít nhất 2 dòng định khoản.");
            if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit) || lines.Sum(l => l.Debit) == 0) ModelState.AddModelError("", "Tổng Nợ phải bằng tổng Có và khác 0.");
        }
        if (m.IntervalMonths < 1 || m.IntervalMonths > 12) ModelState.AddModelError("", "Chu kỳ 1–12 tháng.");
        if (!ModelState.IsValid) { m.Lines = lines.Any() ? lines : new List<RecurringJournalLine> { new(), new() }; await BagAsync(); return View(m); }

        RecurringJournal r;
        if (m.Id == 0) { r = new RecurringJournal(); Db.RecurringJournals.Add(r); }
        else { r = await Db.RecurringJournals.Include(x => x.Lines).FirstAsync(x => x.Id == m.Id); Db.RecurringJournalLines.RemoveRange(r.Lines); r.Lines.Clear(); }
        r.Name = m.Name.Trim(); r.Description = m.Description.Trim(); r.IntervalMonths = m.IntervalMonths; r.DayOfMonth = Math.Clamp(m.DayOfMonth, 1, 31); r.NextDate = m.NextDate.Date;
        r.EndDate = m.EndDate; r.AutoPost = m.AutoPost; r.IsActive = m.IsActive; r.IsAllocation = m.IsAllocation; r.SourceAccountId = m.IsAllocation ? m.SourceAccountId : null;
        foreach (var l in lines) r.Lines.Add(new RecurringJournalLine { AccountId = l.AccountId, PartnerId = l.PartnerId, Department = l.Department, Debit = m.IsAllocation ? 0 : l.Debit, Credit = m.IsAllocation ? 0 : l.Credit, Percent = m.IsAllocation ? l.Percent : 0 });
        await Db.SaveChangesAsync();
        Success($"Đã lưu mẫu {r.Name}.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Generate(DateTime? asOf)
    {
        var log = await _svc.GenerateDueAsync((asOf ?? DateTime.Today).Date);
        if (log.Count == 0) Success("Không có bút toán định kỳ nào đến hạn.");
        else Success("Đã chạy bút toán định kỳ: " + string.Join("; ", log));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await Db.RecurringJournals.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (r != null) { Db.RecurringJournalLines.RemoveRange(r.Lines); Db.RecurringJournals.Remove(r); await Db.SaveChangesAsync(); Success("Đã xóa mẫu (bút toán đã sinh giữ nguyên)."); }
        return RedirectToAction(nameof(Index));
    }
}
