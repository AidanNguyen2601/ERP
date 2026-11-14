using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Định mức nguyên vật liệu (BOM) và giá thành định mức</summary>
public class BomsController : ErpControllerBase
{
    public BomsController(ErpDbContext db) : base(db) { }
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam);

    public async Task<IActionResult> Index()
    {
        ViewBag.CanEdit = CanEdit;
        return View(await Db.Boms.Include(b => b.FinishedItem).Include(b => b.Lines).ThenInclude(l => l.Item).OrderBy(b => b.Code).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var b = await Db.Boms.Include(x => x.FinishedItem).Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        ViewBag.CanEdit = CanEdit;
        ViewBag.Orders = await Db.ProductionOrders.Where(p => p.BomId == id).OrderByDescending(p => p.StartDate).Take(10).ToListAsync();
        return View(b);
    }

    public async Task<IActionResult> Create()
    {
        if (!CanEdit) return Denied();
        var f = new BomForm { Lines = { new LineInput { Quantity = 1 } } };
        await LoadAsync(f);
        return View("Form", f);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var b = await Db.Boms.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        var f = new BomForm
        {
            Id = b.Id, Code = b.Code, FinishedItemId = b.FinishedItemId, OutputQuantity = b.OutputQuantity, LaborCostPerUnit = b.LaborCostPerUnit, OverheadPerUnit = b.OverheadPerUnit,
            IsActive = b.IsActive, Note = b.Note, Lines = b.Lines.Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Quantity = l.Quantity, ScrapPct = l.ScrapPct }).ToList()
        };
        await LoadAsync(f);
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(BomForm form)
    {
        if (!CanEdit) return Denied();
        var lines = form.Lines.Where(l => l.ItemId != null).ToList();
        form.Code = form.Code.Trim().ToUpperInvariant();
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một nguyên vật liệu.");
        if (lines.Any(l => l.Quantity <= 0 || l.ScrapPct < 0)) ModelState.AddModelError("", "Định mức phải > 0, hao hụt không âm.");
        if (lines.Any(l => l.ItemId == form.FinishedItemId)) ModelState.AddModelError("", "Thành phẩm không thể là nguyên liệu của chính nó.");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) ModelState.AddModelError("", "Mỗi nguyên liệu chỉ một dòng.");
        if (await Db.Boms.AnyAsync(b => b.Code == form.Code && b.Id != form.Id)) ModelState.AddModelError(nameof(form.Code), "Mã định mức đã tồn tại");
        if (!ModelState.IsValid) { await LoadAsync(form); return View("Form", form); }

        Bom b;
        if (form.Id == 0) { b = new Bom(); Db.Boms.Add(b); }
        else { b = await Db.Boms.Include(x => x.Lines).FirstAsync(x => x.Id == form.Id); Db.BomLines.RemoveRange(b.Lines.ToList()); b.Lines.Clear(); }
        b.Code = form.Code; b.FinishedItemId = form.FinishedItemId; b.OutputQuantity = form.OutputQuantity; b.LaborCostPerUnit = form.LaborCostPerUnit;
        b.OverheadPerUnit = form.OverheadPerUnit; b.IsActive = form.IsActive; b.Note = form.Note;
        foreach (var l in lines) b.Lines.Add(new BomLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, ScrapPct = l.ScrapPct });
        await Db.SaveChangesAsync();
        Success($"Đã lưu định mức {b.Code}.");
        return RedirectToAction(nameof(Details), new { id = b.Id });
    }

    private async Task LoadAsync(BomForm f)
    {
        ViewBag.Finished = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await Db.Items.Where(i => i.Type == ItemType.FinishedGood).OrderBy(i => i.Code)
            .Select(i => new { i.Id, Name = i.Code + " – " + i.Name }).ToListAsync(), "Id", "Name", f.FinishedItemId);
        ViewBag.LineOptions = new LineEditorOptions { Items = await ItemsAsync(i => i.Type == ItemType.Material), ShowPrice = false, ShowVat = false, ShowScrap = true, QtyLabel = "Định mức", Lines = f.Lines };
    }
}
