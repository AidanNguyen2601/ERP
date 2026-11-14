using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Kế toán tài sản cố định: sổ TSCĐ, ghi tăng, trích khấu hao hàng tháng, thanh lý</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class AssetsController : ErpControllerBase
{
    private readonly AssetService _assets;
    public AssetsController(ErpDbContext db, AssetService assets) : base(db) => _assets = assets;

    public async Task<IActionResult> Index(int? categoryId, AssetStatus? status)
    {
        var q = Db.FixedAssets.Include(a => a.Category).AsQueryable();
        if (categoryId.HasValue) q = q.Where(a => a.CategoryId == categoryId);
        if (status.HasValue) q = q.Where(a => a.Status == status);
        ViewBag.Categories = await Db.AssetCategories.Include(c => c.AssetAccount).Include(c => c.ExpenseAccount).OrderBy(c => c.Code).ToListAsync();
        ViewBag.CategoryId = categoryId; ViewBag.Status = status;
        ViewBag.Runs = await Db.DepreciationRuns.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(12).ToListAsync();
        var last = await Db.DepreciationRuns.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).FirstOrDefaultAsync();
        var next = last == null ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) : new DateTime(last.Year, last.Month, 1).AddMonths(1);
        ViewBag.NextRun = next;
        var all = await Db.FixedAssets.ToListAsync();
        ViewBag.Preview = all.Sum(a => AssetService.DepreciationFor(a, next.Year, next.Month));
        return View(await q.OrderBy(a => a.Code).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await Db.FixedAssets.Include(x => x.Category).ThenInclude(c => c!.AssetAccount).Include(x => x.Category).ThenInclude(c => c!.ExpenseAccount).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        ViewBag.History = await Db.DepreciationLines.Include(l => l.DepreciationRun).Where(l => l.FixedAssetId == id)
            .OrderByDescending(l => l.DepreciationRun!.Year).ThenByDescending(l => l.DepreciationRun!.Month).ToListAsync();
        ViewBag.Entries = await Db.JournalEntries.Where(j => j.Source == JournalSource.FixedAsset && j.SourceId == id).OrderBy(j => j.Date).ToListAsync();
        ViewBag.CashAccounts = await CashAccountsAsync();
        return View(a);
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create()
    {
        await LoadAsync(null);
        return View("Form", new FixedAsset { Code = await NextCodeAsync() });
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(int id)
    {
        var a = await Db.FixedAssets.FindAsync(id);
        if (a == null) return NotFound();
        await LoadAsync(a.CategoryId);
        return View("Form", a);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(FixedAsset model, int? counterAccountId, int? partnerId)
    {
        if (await Db.FixedAssets.AnyAsync(a => a.Code == model.Code && a.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã tài sản đã tồn tại");
        if (model.DepreciationStartDate < model.AcquisitionDate) ModelState.AddModelError(nameof(model.DepreciationStartDate), "Ngày khấu hao không trước ngày mua");
        if (model.AccumulatedDepreciation < 0 || model.AccumulatedDepreciation > model.OriginalCost) ModelState.AddModelError(nameof(model.AccumulatedDepreciation), "Hao mòn lũy kế từ 0 đến nguyên giá");
        if (counterAccountId.HasValue && model.AccumulatedDepreciation > 0) ModelState.AddModelError("", "Tài sản mua mới (hạch toán tăng) không có hao mòn lũy kế ban đầu.");
        if (counterAccountId.HasValue && (await Db.Accounts.FindAsync(counterAccountId))?.TrackPartner == true && partnerId == null) ModelState.AddModelError("", "TK đối ứng công nợ: chọn nhà cung cấp.");
        if (!ModelState.IsValid) { await LoadAsync(model.CategoryId); return View("Form", model); }

        if (model.Id == 0)
        {
            await _assets.RegisterAsync(model, counterAccountId, partnerId);
            Success($"Đã ghi tăng tài sản {model.Code}" + (counterAccountId.HasValue ? " và hạch toán nguyên giá." : " (không hạch toán – tài sản đầu kỳ)."));
        }
        else
        {
            var a = await Db.FixedAssets.FindAsync(model.Id);
            if (a == null) return NotFound();
            var hasRuns = await Db.DepreciationLines.AnyAsync(l => l.FixedAssetId == a.Id);
            a.Name = model.Name; a.Department = model.Department; a.SerialNo = model.SerialNo; a.Note = model.Note;
            if (!hasRuns)
            {
                a.CategoryId = model.CategoryId; a.AcquisitionDate = model.AcquisitionDate; a.DepreciationStartDate = model.DepreciationStartDate;
                a.OriginalCost = model.OriginalCost; a.AccumulatedDepreciation = model.AccumulatedDepreciation;
            }
            a.UsefulLifeMonths = model.UsefulLifeMonths;
            await Db.SaveChangesAsync();
            Success($"Đã cập nhật {a.Code}." + (hasRuns ? " (Nguyên giá, nhóm, ngày khấu hao bị khóa vì đã trích khấu hao.)" : ""));
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> RunDepreciation(int year, int month)
    {
        var run = await _assets.RunDepreciationAsync(year, month);
        Success($"Đã trích khấu hao tháng {month:00}/{year}: {run.Lines.Count} tài sản, tổng {run.Total:N0} đ.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Liquidate(int id, DateTime date, decimal proceeds, int? cashAccountId)
    {
        var a = await Db.FixedAssets.FindAsync(id);
        if (a == null) return NotFound();
        await _assets.DisposeAsync(a, date, proceeds, cashAccountId);
        Success($"Đã thanh lý {a.Code}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<string> NextCodeAsync()
    {
        var codes = await Db.FixedAssets.Select(a => a.Code).ToListAsync();
        var max = codes.Select(c => int.TryParse(c.Replace("TS", ""), out var k) ? k : 0).DefaultIfEmpty(0).Max();
        return $"TS{max + 1:000}";
    }

    private async Task LoadAsync(int? categoryId)
    {
        ViewBag.Categories = new SelectList(await Db.AssetCategories.OrderBy(c => c.Code).Select(c => new { c.Id, Name = c.Code + " – " + c.Name + " (" + c.DefaultLifeMonths + " tháng)" }).ToListAsync(), "Id", "Name", categoryId);
        ViewBag.Counter = await AccountSelectAsync(null, a => a.Code.StartsWith("331") || a.Code.StartsWith("112") || a.Code.StartsWith("111") || a.Code.StartsWith("241") || a.Code.StartsWith("411") || a.Code.StartsWith("3411"));
        ViewBag.Vendors = await VendorsAsync();
    }
}
