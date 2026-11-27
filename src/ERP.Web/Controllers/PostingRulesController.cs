using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Cấu hình tài khoản hạch toán cho từng loại giao dịch tự động</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class PostingRulesController : ErpControllerBase
{
    private readonly AccountingService _acc;
    public PostingRulesController(ErpDbContext db, AccountingService acc) : base(db) => _acc = acc;

    public record RuleRow(PostingLineDef Def, Account? Default, Account Effective, PostingRule? Saved, List<Account> Options);
    public record TxRow(PostingTxDef Tx, List<(PostingLineDef Def, RuleRow? Rule)> Lines)
    {
        public int ChangedCount => Lines.Count(l => l.Rule != null && l.Rule.Default != null && l.Rule.Effective.Id != l.Rule.Default.Id);
    }

    public async Task<IActionResult> Index(string? module)
    {
        var accounts = await Db.Accounts.Where(a => a.IsPostable && a.IsActive).OrderBy(a => a.Code).ToListAsync();
        var byCode = await Db.Accounts.ToDictionaryAsync(a => a.Code);
        var saved = await Db.PostingRules.Include(r => r.Account).ToDictionaryAsync(r => r.Key);
        var rows = new List<TxRow>();
        foreach (var tx in PostingCatalog.Transactions.Where(t => string.IsNullOrEmpty(module) || t.Module == module))
        {
            var lines = new List<(PostingLineDef, RuleRow?)>();
            foreach (var l in tx.Lines)
            {
                if (!l.Configurable) { lines.Add((l, null)); continue; }
                lines.Add((l, new RuleRow(l, byCode.GetValueOrDefault(l.DefaultCode!), await _acc.RuleAccountAsync(l.Key!), saved.GetValueOrDefault(l.Key!),
                    accounts.Where(a => PostingCatalog.Allows(l, a.Code)).ToList())));
            }
            rows.Add(new TxRow(tx, lines));
        }
        ViewBag.Modules = PostingCatalog.Transactions.Select(t => t.Module).Distinct().ToList();
        ViewBag.Module = module;
        ViewBag.CanEdit = User.CanAccounting();
        return View(rows);
    }

    /// <summary>Lưu cấu hình một giao dịch. accounts[key] = Id tài khoản; chọn lại tài khoản mặc định = xóa cấu hình riêng.</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(string tx, Dictionary<string, int?> accounts, string? module)
    {
        var def = PostingCatalog.Transactions.FirstOrDefault(t => t.Code == tx);
        if (def == null) return NotFound();
        var byId = await Db.Accounts.ToDictionaryAsync(a => a.Id);
        var byCode = byId.Values.ToDictionary(a => a.Code);
        var saved = await Db.PostingRules.ToDictionaryAsync(r => r.Key);
        var changes = new List<string>();
        foreach (var l in def.Lines.Where(x => x.Configurable))
        {
            var key = l.Key!;
            if (!accounts.TryGetValue(key, out var id) || id == null) continue;
            if (!byId.TryGetValue(id.Value, out var acc)) { Error("Tài khoản không tồn tại."); return Back(module); }
            if (!acc.IsPostable || !acc.IsActive) { Error($"{l.Title}: TK {acc.Code} là tài khoản tổng hợp hoặc ngưng sử dụng."); return Back(module); }
            if (!PostingCatalog.Allows(l, acc.Code)) { Error($"{l.Title}: chỉ được chọn TK đầu {string.Join(", ", l.Prefixes!)}."); return Back(module); }

            var isDefault = byCode.TryGetValue(l.DefaultCode!, out var d) && d.Id == acc.Id;
            saved.TryGetValue(key, out var rule);
            if (isDefault)
            {
                if (rule != null) { Db.PostingRules.Remove(rule); changes.Add($"{l.Title} → {acc.Code} (mặc định)"); }
                continue;
            }
            if (rule == null) { rule = new PostingRule { Key = key }; Db.PostingRules.Add(rule); }
            else if (rule.AccountId == acc.Id) continue;
            rule.AccountId = acc.Id; rule.UpdatedBy = User.GetFullName(); rule.UpdatedAt = DateTime.Now;
            changes.Add($"{l.Title} → {acc.Code}");
        }
        await Db.SaveChangesAsync();
        _acc.ResetRuleCache();
        if (changes.Any()) Success($"Đã lưu cấu hình \"{def.Title}\": {string.Join("; ", changes)}. Áp dụng cho chứng từ ghi sổ từ bây giờ.");
        else TempData["Warning"] = "Không có thay đổi.";
        return Back(module, tx);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Reset(string tx, string? module)
    {
        var def = PostingCatalog.Transactions.FirstOrDefault(t => t.Code == tx);
        if (def == null) return NotFound();
        var keys = def.Lines.Where(l => l.Configurable).Select(l => l.Key!).ToList();
        var rules = await Db.PostingRules.Where(r => keys.Contains(r.Key)).ToListAsync();
        Db.PostingRules.RemoveRange(rules);
        await Db.SaveChangesAsync();
        Success($"Đã khôi phục tài khoản mặc định cho \"{def.Title}\".");
        return Back(module, tx);
    }

    private IActionResult Back(string? module, string? tx = null) =>
        Redirect(Url.Action(nameof(Index), new { module }) + (tx != null ? "#tx-" + tx : ""));
}
