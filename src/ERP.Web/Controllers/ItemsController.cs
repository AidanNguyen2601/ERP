using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Danh mục hàng hóa, vật tư, thành phẩm, dịch vụ</summary>
public class ItemsController : ErpControllerBase
{
    private readonly CodeService _codes;
    public ItemsController(ErpDbContext db, CodeService codes) : base(db) => _codes = codes;
    private bool CanEdit => User.InAny(AppRoles.Accounting + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing + "," + AppRoles.Production);

    public async Task<IActionResult> Index(ItemType? type, string? q)
    {
        var items = await Db.Items.OrderBy(i => i.Type).ThenBy(i => i.Code).ToListAsync();
        if (type.HasValue) items = items.Where(i => i.Type == type).ToList();
        if (!string.IsNullOrWhiteSpace(q)) items = items.Where(i => i.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ViewBag.Stock = (await Db.StockLevels.ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        ViewBag.Type = type; ViewBag.Q = q; ViewBag.CanEdit = CanEdit;
        return View(items);
    }

    public async Task<IActionResult> Create(ItemType type = ItemType.Merchandise) => CanEdit ? View("Form", new Item { Type = type, Code = await _codes.NextAsync(CodeService.Target(type)) ?? "" }) : Denied();

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var i = await Db.Items.FindAsync(id);
        return i == null ? NotFound() : View("Form", i);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Item model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (model.Code == "") { model.Code = await _codes.NextAsync(CodeService.Target(model.Type)) ?? ""; ModelState.Remove(nameof(model.Code)); }
        if (model.Code == "") ModelState.AddModelError(nameof(model.Code), "Nhập mã hàng");
        else if (model.Id == 0 && await _codes.ValidateAsync(CodeService.Target(model.Type), model.Code) is { } ruleErr) ModelState.AddModelError(nameof(model.Code), ruleErr);
        if (await Db.Items.AnyAsync(i => i.Code == model.Code && i.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã hàng đã tồn tại");
        if (model.VatRate is not (0 or 5 or 8 or 10)) ModelState.AddModelError(nameof(model.VatRate), "Thuế suất 0, 5, 8 hoặc 10%");
        Item? existing = model.Id == 0 ? null : await Db.Items.FindAsync(model.Id);
        if (existing != null && existing.Type != model.Type && await Db.StockMoves.AnyAsync(m => m.ItemId == model.Id))
            ModelState.AddModelError(nameof(model.Type), "Hàng đã phát sinh nhập xuất, không đổi loại (ảnh hưởng TK kho).");
        if (!ModelState.IsValid) return View("Form", model);
        if (existing == null) { model.AvgCost = 0; Db.Items.Add(model); }
        else
        {
            existing.Code = model.Code; existing.Name = model.Name; existing.Unit = model.Unit; existing.Type = model.Type; existing.SalePrice = model.SalePrice;
            existing.VatRate = model.VatRate; existing.MinStock = model.MinStock; existing.IsActive = model.IsActive;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu {model.Code}.");
        return RedirectToAction(nameof(Index));
    }
}
