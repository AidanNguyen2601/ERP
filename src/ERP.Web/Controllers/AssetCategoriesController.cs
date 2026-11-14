using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

/// <summary>Danh mục nhóm tài sản cố định (TK nguyên giá, hao mòn, chi phí khấu hao mặc định)</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class AssetCategoriesController : ErpControllerBase
{
    public AssetCategoriesController(ErpDbContext db) : base(db) { }

    public async Task<IActionResult> Index()
    {
        ViewBag.Counts = await Db.FixedAssets.GroupBy(a => a.CategoryId).Select(g => new { Id = g.Key ?? 0, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
        return View(await Db.AssetCategories.Include(c => c.AssetAccount).Include(c => c.DepreciationAccount).Include(c => c.ExpenseAccount)
            .OrderBy(c => c.Code).ToListAsync());
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create() { await LoadAsync(); return View("Form", new AssetCategory()); }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.AssetCategories.FindAsync(id);
        if (c == null) return NotFound();
        await LoadAsync(c);
        return View("Form", c);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(AssetCategory model)
    {
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (await Db.AssetCategories.AnyAsync(c => c.Code == model.Code && c.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã nhóm đã tồn tại");
        var accIds = new[] { model.AssetAccountId, model.DepreciationAccountId, model.ExpenseAccountId };
        var accs = await Db.Accounts.Where(a => accIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        bool Ok(int id, string prefix) => accs.TryGetValue(id, out var a) && a.IsPostable && a.Code.StartsWith(prefix);
        if (!Ok(model.AssetAccountId, "21")) ModelState.AddModelError(nameof(model.AssetAccountId), "Chọn TK chi tiết đầu 21x (211, 213)");
        if (!Ok(model.DepreciationAccountId, "214")) ModelState.AddModelError(nameof(model.DepreciationAccountId), "Chọn TK chi tiết đầu 214");
        if (!(Ok(model.ExpenseAccountId, "6") || Ok(model.ExpenseAccountId, "8"))) ModelState.AddModelError(nameof(model.ExpenseAccountId), "Chọn TK chi phí (6xx, 811)");
        var existing = model.Id == 0 ? null : await Db.AssetCategories.FindAsync(model.Id);
        if (model.Id != 0 && existing == null) return NotFound();
        if (!ModelState.IsValid) { await LoadAsync(model); return View("Form", model); }

        if (existing == null) Db.AssetCategories.Add(model);
        else
        {
            existing.Code = model.Code; existing.Name = model.Name; existing.DefaultLifeMonths = model.DefaultLifeMonths;
            existing.AssetAccountId = model.AssetAccountId; existing.DepreciationAccountId = model.DepreciationAccountId; existing.ExpenseAccountId = model.ExpenseAccountId;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu nhóm tài sản {model.Code}.");
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadAsync(AssetCategory? c = null)
    {
        ViewBag.AssetAccounts = await AccountSelectAsync(c?.AssetAccountId, a => a.Code.StartsWith("21") && !a.Code.StartsWith("214"));
        ViewBag.DepAccounts = await AccountSelectAsync(c?.DepreciationAccountId, a => a.Code.StartsWith("214"));
        ViewBag.ExpAccounts = await AccountSelectAsync(c?.ExpenseAccountId, a => a.Code.StartsWith("6") || a.Code.StartsWith("811"));
    }
}
