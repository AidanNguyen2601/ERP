using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

/// <summary>Danh mục dùng chung: đơn vị tính, phòng ban / bộ phận</summary>
public class CatalogsController : ErpControllerBase
{
    public CatalogsController(ErpDbContext db) : base(db) { }
    private bool CanEdit => User.InAny(AppRoles.Accounting);

    public async Task<IActionResult> Index(CatalogType type = CatalogType.Unit, string? q = null)
    {
        var list = await Db.CatalogItems.Where(c => c.Type == type).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        if (!string.IsNullOrWhiteSpace(q))
            list = list.Where(c => c.Code.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || c.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        ViewBag.Type = type; ViewBag.Q = q; ViewBag.CanEdit = CanEdit;
        ViewBag.Usage = await UsageAsync(type);
        return View(list);
    }

    public async Task<IActionResult> Create(CatalogType type = CatalogType.Unit)
    {
        if (!CanEdit) return Denied();
        var next = (await Db.CatalogItems.Where(c => c.Type == type).MaxAsync(c => (int?)c.SortOrder) ?? 0) + 10;
        return View("Form", new CatalogItem { Type = type, SortOrder = next });
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var c = await Db.CatalogItems.FindAsync(id);
        return c == null ? NotFound() : View("Form", c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CatalogItem model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        model.Name = (model.Name ?? "").Trim();
        if (await Db.CatalogItems.AnyAsync(c => c.Type == model.Type && c.Code == model.Code && c.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), "Mã đã tồn tại trong danh mục này");
        if (await Db.CatalogItems.AnyAsync(c => c.Type == model.Type && c.Name == model.Name && c.Id != model.Id))
            ModelState.AddModelError(nameof(model.Name), "Tên đã tồn tại trong danh mục này");
        var existing = model.Id == 0 ? null : await Db.CatalogItems.FindAsync(model.Id);
        if (model.Id != 0 && existing == null) return NotFound();
        if (!ModelState.IsValid) return View("Form", model);

        if (existing == null) Db.CatalogItems.Add(model);
        else
        {
            // Đổi tên: cập nhật luôn các bản ghi đang dùng tên cũ để dữ liệu nhất quán
            if (existing.Name != model.Name) await RenameUsagesAsync(existing.Type, existing.Name, model.Name);
            existing.Code = model.Code; existing.Name = model.Name; existing.SortOrder = model.SortOrder;
            existing.Note = model.Note; existing.IsActive = model.IsActive;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu {model.Type.GetDisplayName().ToLowerInvariant()} \"{model.Name}\".");
        return RedirectToAction(nameof(Index), new { type = model.Type });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!CanEdit) return Denied();
        var c = await Db.CatalogItems.FindAsync(id);
        if (c == null) return NotFound();
        var used = (await UsageAsync(c.Type)).GetValueOrDefault(c.Name);
        if (used > 0)
        {
            Error($"\"{c.Name}\" đang được dùng ở {used} bản ghi – chuyển sang ngưng sử dụng thay vì xóa.");
            return RedirectToAction(nameof(Index), new { type = c.Type });
        }
        Db.CatalogItems.Remove(c);
        await Db.SaveChangesAsync();
        Success($"Đã xóa \"{c.Name}\".");
        return RedirectToAction(nameof(Index), new { type = c.Type });
    }

    /// <summary>Số bản ghi đang dùng từng giá trị (theo tên)</summary>
    private async Task<Dictionary<string, int>> UsageAsync(CatalogType type)
    {
        IEnumerable<string> values = type switch
        {
            CatalogType.Unit => (await Db.Items.Select(i => i.Unit).ToListAsync())
                .Concat(await Db.InvoiceLines.Where(l => l.Unit != null).Select(l => l.Unit!).ToListAsync()),
            CatalogType.Department => (await Db.Users.Select(u => u.Department).ToListAsync())
                .Concat(await Db.FixedAssets.Where(a => a.Department != null).Select(a => a.Department!).ToListAsync()),
            _ => Array.Empty<string>()
        };
        return values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task RenameUsagesAsync(CatalogType type, string oldName, string newName)
    {
        switch (type)
        {
            case CatalogType.Unit:
                await Db.Items.Where(i => i.Unit == oldName).ExecuteUpdateAsync(s => s.SetProperty(i => i.Unit, newName));
                break;
            case CatalogType.Department:
                await Db.Users.Where(u => u.Department == oldName).ExecuteUpdateAsync(s => s.SetProperty(u => u.Department, newName));
                await Db.FixedAssets.Where(a => a.Department == oldName).ExecuteUpdateAsync(s => s.SetProperty(a => a.Department, newName));
                break;
        }
    }
}
