using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Công cụ dụng cụ (ghi tăng, phân bổ hằng tháng, báo hỏng) và điều chuyển tài sản cố định</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class ToolsController : ErpControllerBase
{
    private readonly ToolService _svc; private readonly CodeService _codes;
    public ToolsController(ErpDbContext db, ToolService svc, CodeService codes) : base(db) { _svc = svc; _codes = codes; }

    public async Task<IActionResult> Index(bool all = false)
    {
        var q = Db.ToolItems.Include(t => t.ExpenseAccount).AsQueryable();
        if (!all) q = q.Where(t => t.Status == ToolStatus.InUse);
        ViewBag.Runs = await Db.ToolAllocationRuns.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(24).ToListAsync();
        ViewBag.All = all;
        return View(await q.OrderBy(t => t.Code).ToListAsync());
    }

    private async Task BagAsync()
    {
        ViewBag.Expense = await AccountSelectAsync(null, a => new[] { "627", "641", "642", "154" }.Any(p => a.Code.StartsWith(p)));
        ViewBag.Counter = await AccountSelectAsync(null, a => new[] { "331", "111", "112", "153", "141" }.Any(p => a.Code.StartsWith(p)));
        ViewBag.Partners = await VendorsAsync();
        ViewBag.Depts = await Db.CatalogItems.Where(c => c.Type == CatalogType.Department).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create()
    {
        await BagAsync();
        return View(new ToolItem { Code = await _codes.NextAsync("tool") ?? "", ExpenseAccountId = (await Db.Accounts.FirstOrDefaultAsync(a => a.Code.StartsWith("642") && a.IsPostable))?.Id ?? 0 });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create(ToolItem m, int? counterAccountId, int? partnerId)
    {
        m.Code = string.IsNullOrWhiteSpace(m.Code) ? await _codes.NextAsync("tool") ?? "" : m.Code.Trim().ToUpperInvariant();
        ModelState.Remove(nameof(m.Code)); ModelState.Remove(nameof(m.ExpenseAccount));
        if (m.Code == "" || await Db.ToolItems.AnyAsync(t => t.Code == m.Code)) ModelState.AddModelError(nameof(m.Code), "Mã trống hoặc đã tồn tại");
        if (m.Cost <= 0) ModelState.AddModelError(nameof(m.Cost), "Nhập giá trị");
        if (m.Months < 1 || m.Months > 36) ModelState.AddModelError(nameof(m.Months), "Phân bổ 1–36 tháng (CCDC không quá 3 năm)");
        if (!ModelState.IsValid) { await BagAsync(); return View(m); }
        await _svc.RegisterAsync(m, counterAccountId, partnerId);
        Success($"Đã ghi tăng CCDC {m.Code}" + (counterAccountId != null ? " và hạch toán Nợ 242." : " (số dư đầu kỳ, không hạch toán)."));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Run(int year, int month)
    {
        var r = await _svc.RunAsync(year, month);
        Success($"Đã phân bổ CCDC tháng {month:00}/{year}: {r.Total:N0} đ.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Retire(int id, DateTime date, string reason)
    {
        var t = await Db.ToolItems.FindAsync(id); if (t == null) return NotFound();
        await _svc.DisposeAsync(t, date, string.IsNullOrWhiteSpace(reason) ? "Hỏng" : reason);
        Success($"Đã báo hỏng {t.Code}.");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ điều chuyển TSCĐ
    public async Task<IActionResult> Transfers()
    {
        ViewBag.Assets = new SelectList(await Db.FixedAssets.Where(a => a.Status != AssetStatus.Disposed).OrderBy(a => a.Code).Select(a => new { a.Id, Name = a.Code + " – " + a.Name + " (" + (a.Department ?? "—") + ")" }).ToListAsync(), "Id", "Name");
        ViewBag.Branches = new SelectList(await Db.Branches.OrderBy(b => b.Code).ToListAsync(), "Id", "Name");
        ViewBag.Depts = await Db.CatalogItems.Where(c => c.Type == CatalogType.Department).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
        ViewBag.BranchNames = await Db.Branches.ToDictionaryAsync(b => b.Id, b => b.Name);
        return View(await Db.AssetTransfers.Include(t => t.FixedAsset).OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).Take(200).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Transfer(int assetId, DateTime date, string? toDept, int? toBranch, string? note)
    {
        var a = await Db.FixedAssets.FindAsync(assetId); if (a == null) return NotFound();
        await _svc.TransferAssetAsync(a, date, string.IsNullOrWhiteSpace(toDept) ? a.Department : toDept.Trim(), toBranch, note);
        Success($"Đã điều chuyển {a.Code} sang {a.Department}.");
        return RedirectToAction(nameof(Transfers));
    }
}
