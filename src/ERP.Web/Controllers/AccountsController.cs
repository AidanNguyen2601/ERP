using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Hệ thống tài khoản kế toán</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class AccountsController : ErpControllerBase
{
    private readonly AccountingService _acc;
    public AccountsController(ErpDbContext db, AccountingService acc) : base(db) => _acc = acc;

    public async Task<IActionResult> Index(string? q)
    {
        var list = await Db.Accounts.OrderBy(a => a.Code).ToListAsync();
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(a => a.Code.StartsWith(q.Trim()) || a.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var bal = (await _acc.PostedLines().Select(l => new { l.AccountId, l.Debit, l.Credit }).ToListAsync())
            .GroupBy(x => x.AccountId).ToDictionary(g => g.Key, g => g.Sum(x => x.Debit - x.Credit));
        var all = await Db.Accounts.ToListAsync();
        ViewBag.Balances = list.ToDictionary(a => a.Id, a => all.Where(x => x.Code.StartsWith(a.Code)).Sum(x => bal.GetValueOrDefault(x.Id)));
        ViewBag.Q = q;
        return View(list);
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public IActionResult Create(string? parent) => View("Form", new Account { Code = parent ?? "", Type = (parent?.FirstOrDefault()) switch { '3' => AccountType.Liability, '4' => AccountType.Equity, '5' or '7' => AccountType.Revenue, '6' or '8' => AccountType.Expense, '9' => AccountType.Result, _ => AccountType.Asset } });

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(int id)
    {
        var a = await Db.Accounts.FindAsync(id);
        return a == null ? NotFound() : View("Form", a);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(Account model)
    {
        model.Code = model.Code?.Trim() ?? "";
        if (await Db.Accounts.AnyAsync(a => a.Code == model.Code && a.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Số hiệu TK đã tồn tại");
        var all = await Db.Accounts.ToListAsync();
        if (model.Code.Length > 3 && !all.Any(a => a.Id != model.Id && model.Code.StartsWith(a.Code) && a.Code.Length < model.Code.Length))
            ModelState.AddModelError(nameof(model.Code), "TK chi tiết phải có TK cấp trên (VD: 1121 thuộc 112)");
        if (!ModelState.IsValid) return View("Form", model);

        Account a;
        if (model.Id == 0)
        {
            // TK cấp trên trở thành TK tổng hợp → không được còn số dư / phát sinh trực tiếp
            var parent = all.Where(x => model.Code.StartsWith(x.Code) && x.Code.Length < model.Code.Length).OrderByDescending(x => x.Code.Length).FirstOrDefault();
            if (parent != null && parent.IsPostable)
            {
                if (await Db.JournalLines.AnyAsync(l => l.AccountId == parent.Id))
                { ModelState.AddModelError(nameof(model.Code), $"TK {parent.Code} đã phát sinh bút toán, không thể mở TK chi tiết."); return View("Form", model); }
                parent.IsPostable = false;
            }
            a = model; a.IsPostable = true;
            Db.Accounts.Add(a);
        }
        else
        {
            a = all.First(x => x.Id == model.Id);
            if (a.Code != model.Code && await Db.JournalLines.AnyAsync(l => l.AccountId == a.Id))
            { ModelState.AddModelError(nameof(model.Code), "TK đã phát sinh, không đổi số hiệu."); return View("Form", model); }
            a.Code = model.Code; a.Name = model.Name; a.Type = model.Type; a.TrackPartner = model.TrackPartner; a.IsActive = model.IsActive;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu tài khoản {a.Code}.");
        return RedirectToAction(nameof(Index));
    }
}
