using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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

    public async Task<IActionResult> Index(ItemType? type, string? q, int page = 1)
    {
        var iq = Db.Items.AsQueryable();
        if (type.HasValue) iq = iq.Where(i => i.Type == type);
        if (!string.IsNullOrWhiteSpace(q)) iq = iq.Where(i => i.Code.Contains(q) || i.Name.Contains(q));
        var items = await iq.OrderBy(i => i.Type).ThenBy(i => i.Code).PageAsync(this, page);
        var ids = items.Select(i => i.Id).ToList();
        ViewBag.Stock = await Db.StockLevels.Where(s => ids.Contains(s.ItemId)).GroupBy(s => s.ItemId).Select(g => new { g.Key, Q = g.Sum(s => s.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        ViewBag.Type = type; ViewBag.Q = q; ViewBag.CanEdit = CanEdit;
        return View(items);
    }

    public async Task<IActionResult> Create(ItemType type = ItemType.Merchandise) { if (!CanEdit) return Denied(); await GroupsAsync(); ViewBag.Uoms = new List<UomConversion>(); return View("Form", new Item { Type = type, Code = await _codes.NextAsync(CodeService.Target(type)) ?? "" }); }

    private async Task GroupsAsync() { ViewBag.Groups = new SelectList(await Db.ItemGroups.OrderBy(g => g.Code).Select(g => new { g.Id, Name = g.Code + " – " + g.Name }).ToListAsync(), "Id", "Name"); }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var i = await Db.Items.FindAsync(id);
        if (i == null) return NotFound();
        await GroupsAsync(); ViewBag.Uoms = await Db.UomConversions.Where(u => u.ItemId == id).OrderBy(u => u.Unit).ToListAsync();
        return View("Form", i);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddUom(int itemId, string unit, decimal factor)
    {
        if (!CanEdit) return Denied();
        unit = (unit ?? "").Trim();
        if (unit == "" || factor <= 0) { Error("Nhập đơn vị và hệ số quy đổi > 0."); return RedirectToAction(nameof(Edit), new { id = itemId }); }
        var u = await Db.UomConversions.FirstOrDefaultAsync(x => x.ItemId == itemId && x.Unit == unit);
        if (u == null) Db.UomConversions.Add(new UomConversion { ItemId = itemId, Unit = unit, Factor = factor }); else u.Factor = factor;
        await Db.SaveChangesAsync();
        Success("Đã lưu quy đổi đơn vị tính.");
        return RedirectToAction(nameof(Edit), new { id = itemId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUom(int id, int itemId)
    {
        if (!CanEdit) return Denied();
        var u = await Db.UomConversions.FindAsync(id);
        if (u != null) { Db.UomConversions.Remove(u); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Edit), new { id = itemId });
    }

    // ---- Nhóm hàng
    public async Task<IActionResult> Groups() { ViewBag.Counts = (await Db.Items.Where(i => i.GroupId != null).GroupBy(i => i.GroupId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N); ViewBag.CanEdit = CanEdit; return View(await Db.ItemGroups.Include(g => g.Parent).OrderBy(g => g.Code).ToListAsync()); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGroup(int id, string code, string name, int? parentId)
    {
        if (!CanEdit) return Denied();
        code = (code ?? "").Trim().ToUpperInvariant(); name = (name ?? "").Trim();
        if (code == "" || name == "") { Error("Nhập mã và tên nhóm."); return RedirectToAction(nameof(Groups)); }
        if (await Db.ItemGroups.AnyAsync(g => g.Code == code && g.Id != id)) { Error("Mã nhóm đã tồn tại."); return RedirectToAction(nameof(Groups)); }
        if (parentId == id && id != 0) parentId = null;
        var g = id == 0 ? new ItemGroup() : await Db.ItemGroups.FindAsync(id);
        if (g == null) return NotFound();
        g.Code = code; g.Name = name; g.ParentId = parentId;
        if (g.Id == 0) Db.ItemGroups.Add(g);
        await Db.SaveChangesAsync();
        Success($"Đã lưu nhóm hàng {code}.");
        return RedirectToAction(nameof(Groups));
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
        if (model.CostMethod == CostMethod.Specific && model.TrackMode == TrackMode.None) ModelState.AddModelError(nameof(model.CostMethod), "Giá đích danh yêu cầu theo dõi hàng theo lô hoặc serial.");
        if (model.MaxStock > 0 && model.MaxStock < model.MinStock) ModelState.AddModelError(nameof(model.MaxStock), "Tồn tối đa phải lớn hơn tồn tối thiểu.");
        if (!string.IsNullOrWhiteSpace(model.Barcode) && await Db.Items.AnyAsync(i => i.Barcode == model.Barcode.Trim() && i.Id != model.Id)) ModelState.AddModelError(nameof(model.Barcode), "Mã vạch đã dùng cho hàng khác.");
        if (!ModelState.IsValid) { await GroupsAsync(); return View("Form", model); }
        if (existing == null) { model.AvgCost = 0; model.Barcode = string.IsNullOrWhiteSpace(model.Barcode) ? null : model.Barcode.Trim(); Db.Items.Add(model); }
        else
        {
            existing.Code = model.Code; existing.Name = model.Name; existing.Unit = model.Unit; existing.Type = model.Type; existing.SalePrice = model.SalePrice;
            existing.VatRate = model.VatRate; existing.MinStock = model.MinStock; existing.IsActive = model.IsActive;
            existing.GroupId = model.GroupId; existing.MaxStock = model.MaxStock; existing.TrackMode = model.TrackMode; existing.TrackExpiry = model.TrackExpiry; existing.ExpiryAlertDays = model.ExpiryAlertDays;
            existing.CostMethod = model.CostMethod; existing.Barcode = string.IsNullOrWhiteSpace(model.Barcode) ? null : model.Barcode.Trim();
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu {model.Code}.");
        return RedirectToAction(nameof(Index));
    }
}
