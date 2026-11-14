using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Chứng từ kế toán: xem mọi bút toán (tự động từ các phân hệ) và lập bút toán thủ công</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class JournalsController : ErpControllerBase
{
    private readonly AccountingService _acc;
    public JournalsController(ErpDbContext db, AccountingService acc) : base(db) => _acc = acc;

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, JournalSource? source, DocStatus? status, string? q, int page = 1)
    {
        var (f, t) = Range(from ?? DateTime.Today.AddMonths(-3).AddDays(1 - DateTime.Today.Day), to);
        var query = Db.JournalEntries.Include(j => j.Lines).Include(j => j.CreatedBy).Where(j => j.Date >= f && j.Date <= t);
        if (source.HasValue) query = query.Where(j => j.Source == source);
        if (status.HasValue) query = query.Where(j => j.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(j => j.Number.Contains(q) || j.Description.Contains(q) || (j.SourceRef != null && j.SourceRef.Contains(q)));
        var total = await query.CountAsync();
        const int size = 30;
        var items = await query.OrderByDescending(j => j.Date).ThenByDescending(j => j.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        ViewBag.From = f; ViewBag.To = t; ViewBag.Source = source; ViewBag.Status = status; ViewBag.Q = q;
        return View(new PagedList<JournalEntry>(items, total, page, size));
    }

    public async Task<IActionResult> Details(int id)
    {
        var j = await Db.JournalEntries.Include(x => x.Lines).ThenInclude(l => l.Account).Include(x => x.Lines).ThenInclude(l => l.Partner)
            .Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id);
        if (j == null) return NotFound();
        ViewBag.Reversal = await Db.JournalEntries.FirstOrDefaultAsync(x => x.ReversalOfId == id);
        ViewBag.Origin = j.ReversalOfId.HasValue ? await Db.JournalEntries.FindAsync(j.ReversalOfId) : null;
        return View(j);
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create()
    {
        await LoadAsync();
        return View("Form", new JournalForm { Lines = { new LineInput(), new LineInput() } });
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(int id)
    {
        var j = await Db.JournalEntries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (j == null) return NotFound();
        if (j.Status != DocStatus.Draft || j.Source != JournalSource.Manual) { Error("Chỉ sửa được chứng từ thủ công đang nháp."); return RedirectToAction(nameof(Details), new { id }); }
        await LoadAsync();
        return View("Form", new JournalForm
        {
            Id = j.Id, Date = j.Date, Description = j.Description, Number = j.Number,
            Lines = j.Lines.OrderBy(l => l.Id).Select(l => new LineInput { Id = l.Id, AccountId = l.AccountId, PartnerId = l.PartnerId, Debit = l.Debit, Credit = l.Credit, Description = l.Description }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(JournalForm form, bool post)
    {
        form.Lines = form.Lines.Where(l => l.AccountId != null && (l.Debit != 0 || l.Credit != 0)).ToList();
        if (form.Lines.Count < 2) ModelState.AddModelError("", "Nhập ít nhất 2 dòng có tài khoản và số tiền.");
        if (!ModelState.IsValid) { await LoadAsync(); if (form.Lines.Count < 2) form.Lines.AddRange(Enumerable.Range(0, 2 - form.Lines.Count).Select(_ => new LineInput())); return View("Form", form); }

        JournalEntry j;
        if (form.Id == 0)
        {
            j = new JournalEntry { Source = JournalSource.Manual, CreatedById = Me, Number = await DocNumbers.NextAsync(Db.JournalEntries.Select(x => x.Number), "PKT", form.Date) };
            Db.JournalEntries.Add(j);
        }
        else
        {
            j = await Db.JournalEntries.Include(x => x.Lines).FirstAsync(x => x.Id == form.Id);
            if (j.Status != DocStatus.Draft) throw new BusinessException("Chứng từ đã ghi sổ.");
            Db.JournalLines.RemoveRange(j.Lines.ToList());
            j.Lines.Clear();
        }
        j.Date = form.Date.Date; j.Description = form.Description.Trim();
        foreach (var l in form.Lines)
            j.Lines.Add(new JournalLine { AccountId = l.AccountId!.Value, PartnerId = l.PartnerId, Debit = Math.Round(l.Debit, 0), Credit = Math.Round(l.Credit, 0), Description = l.Description });
        await Db.SaveChangesAsync();
        if (post)
        {
            try { await _acc.PostEntryAsync(j); }
            catch (BusinessException ex)
            {
                // Giữ nguyên dữ liệu đã nhập (đã lưu nháp) để người dùng sửa tiếp
                ModelState.AddModelError("", ex.Message + " Chứng từ đã được lưu nháp.");
                form.Id = j.Id; form.Number = j.Number; ModelState.Remove(nameof(form.Id));
                await LoadAsync();
                return View("Form", form);
            }
            Success($"Đã ghi sổ chứng từ {j.Number}.");
        }
        else Success($"Đã lưu nháp chứng từ {j.Number}.");
        return RedirectToAction(nameof(Details), new { id = j.Id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Post(int id)
    {
        var j = await Db.JournalEntries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (j == null) return NotFound();
        if (j.Status != DocStatus.Draft) throw new BusinessException("Chứng từ không ở trạng thái nháp.");
        await _acc.PostEntryAsync(j);
        Success($"Đã ghi sổ chứng từ {j.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Delete(int id)
    {
        var j = await Db.JournalEntries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (j == null) return NotFound();
        if (j.Status != DocStatus.Draft) throw new BusinessException("Chỉ xóa chứng từ nháp; chứng từ đã ghi sổ dùng bút toán đảo.");
        Db.JournalLines.RemoveRange(j.Lines.ToList());
        Db.JournalEntries.Remove(j);
        await Db.SaveChangesAsync();
        Success($"Đã xóa chứng từ nháp {j.Number}.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Bút toán đảo cho chứng từ thủ công đã ghi sổ (chứng từ tự động phải hủy từ phân hệ gốc)</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Reverse(int id, DateTime? date, string? reason)
    {
        var j = await Db.JournalEntries.FindAsync(id);
        if (j == null) return NotFound();
        if (j.Source is not (JournalSource.Manual or JournalSource.Opening)) throw new BusinessException("Bút toán tự động: hãy hủy chứng từ gốc ở phân hệ tương ứng.");
        if (j.ReversalOfId != null || await Db.JournalEntries.AnyAsync(x => x.ReversalOfId == id)) throw new BusinessException("Bút toán đã được đảo trước đó.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do đảo bút toán.");
        var rev = await _acc.ReverseAsync(id, date ?? DateTime.Today, reason);
        Success($"Đã lập bút toán đảo {rev!.Number}.");
        return RedirectToAction(nameof(Details), new { id = rev.Id });
    }

    private async Task LoadAsync()
    {
        ViewBag.Accounts = await PostableAccountsAsync();
        ViewBag.Partners = await Db.Partners.Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync();
    }
}
