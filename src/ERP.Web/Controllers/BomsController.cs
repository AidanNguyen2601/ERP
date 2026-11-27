using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Định mức nguyên vật liệu (BOM nhiều cấp, nhiều phiên bản), quy trình sản xuất (routing) và giá thành định mức</summary>
public class BomsController : ErpControllerBase
{
    private readonly BomService _boms;
    public BomsController(ErpDbContext db, BomService boms) : base(db) => _boms = boms;
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam);

    public async Task<IActionResult> Index(bool all = false)
    {
        ViewBag.CanEdit = CanEdit; ViewBag.All = all;
        ViewBag.Centers = await _boms.CentersAsync();
        var q = Db.Boms.Include(b => b.FinishedItem).Include(b => b.Lines).ThenInclude(l => l.Item).Include(b => b.Operations).AsQueryable();
        if (!all) q = q.Where(b => b.IsActive);
        return View(await q.OrderBy(b => b.FinishedItem!.Code).ThenByDescending(b => b.VersionNo).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var b = await Db.Boms.Include(x => x.FinishedItem).Include(x => x.Lines).ThenInclude(l => l.Item).Include(x => x.Operations).ThenInclude(o => o.WorkCenter).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        ViewBag.CanEdit = CanEdit;
        ViewBag.Orders = await Db.ProductionOrders.Where(p => p.BomId == id).OrderByDescending(p => p.StartDate).Take(10).ToListAsync();
        ViewBag.Versions = await Db.Boms.Where(x => x.FinishedItemId == b.FinishedItemId).OrderBy(x => x.VersionNo).ToListAsync();
        ViewBag.Centers = await _boms.CentersAsync();
        // Các bán thành phẩm trong định mức: định mức đang áp dụng của chúng (cây nhiều cấp)
        var semiIds = b.Lines.Where(l => l.Item?.Type == ItemType.SemiFinished).Select(l => l.ItemId).ToList();
        ViewBag.SubBoms = (await Db.Boms.Include(x => x.Lines).ThenInclude(l => l.Item).Where(x => x.IsActive && semiIds.Contains(x.FinishedItemId!.Value)).ToListAsync())
            .GroupBy(x => x.FinishedItemId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.VersionNo).First());
        return View(b);
    }

    public async Task<IActionResult> Create(int? itemId)
    {
        if (!CanEdit) return Denied();
        var f = new BomForm { FinishedItemId = itemId, Lines = { new LineInput { Quantity = 1 } } };
        await LoadAsync(f);
        return View("Form", f);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var b = await Db.Boms.Include(x => x.Lines).Include(x => x.Operations).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        var f = ToForm(b);
        await LoadAsync(f);
        return View("Form", f);
    }

    private static BomForm ToForm(Bom b) => new()
    {
        Id = b.Id, Code = b.Code, FinishedItemId = b.FinishedItemId, OutputQuantity = b.OutputQuantity, LaborCostPerUnit = b.LaborCostPerUnit, OverheadPerUnit = b.OverheadPerUnit,
        IsActive = b.IsActive, Note = b.Note, VersionNo = b.VersionNo, EffectiveFrom = b.EffectiveFrom, EffectiveTo = b.EffectiveTo,
        Lines = b.Lines.Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Quantity = l.Quantity, ScrapPct = l.ScrapPct }).ToList(),
        Ops = b.Operations.OrderBy(o => o.Seq).Select(o => new BomOpInput { Seq = o.Seq, Name = o.Name, WorkCenterId = o.WorkCenterId, SetupMinutes = o.SetupMinutes, RunMinutesPerUnit = o.RunMinutesPerUnit, Workers = o.Workers, Note = o.Note }).ToList()
    };

    /// <summary>Tạo phiên bản mới từ định mức hiện có (sao chép NVL và routing); phiên bản cũ có thể ngưng áp dụng từ ngày bắt đầu hiệu lực của bản mới</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> NewVersion(int id, DateTime? effectiveFrom, bool retireOld)
    {
        if (!CanEdit) return Denied();
        var src = await Db.Boms.Include(x => x.Lines).Include(x => x.Operations).FirstOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Không tìm thấy định mức.");
        var same = await Db.Boms.Where(x => x.FinishedItemId == src.FinishedItemId).ToListAsync();
        var ver = same.Max(x => x.VersionNo) + 1;
        var from = (effectiveFrom ?? DateTime.Today).Date;
        var baseCode = System.Text.RegularExpressions.Regex.Replace(src.Code, @"-V\d+$", "");
        var code = $"{baseCode}-V{ver}";
        while (await Db.Boms.AnyAsync(x => x.Code == code)) code += "x";
        var nb = new Bom
        {
            Code = code, FinishedItemId = src.FinishedItemId, OutputQuantity = src.OutputQuantity, LaborCostPerUnit = src.LaborCostPerUnit, OverheadPerUnit = src.OverheadPerUnit,
            IsActive = true, Note = $"Phiên bản {ver} – sao chép từ {src.Code}", VersionNo = ver, EffectiveFrom = from
        };
        foreach (var l in src.Lines) nb.Lines.Add(new BomLine { ItemId = l.ItemId, Quantity = l.Quantity, ScrapPct = l.ScrapPct });
        foreach (var o in src.Operations) nb.Operations.Add(new BomOperation { Seq = o.Seq, Name = o.Name, WorkCenterId = o.WorkCenterId, SetupMinutes = o.SetupMinutes, RunMinutesPerUnit = o.RunMinutesPerUnit, Workers = o.Workers, Note = o.Note });
        Db.Boms.Add(nb);
        if (retireOld)
            foreach (var o in same.Where(x => x.IsActive && (x.EffectiveTo == null || x.EffectiveTo >= from))) o.EffectiveTo = from.AddDays(-1);
        await Db.SaveChangesAsync();
        Success($"Đã tạo phiên bản {ver} ({nb.Code}) hiệu lực từ {from.ToDate()}. Chỉnh sửa định mức rồi lưu.");
        return RedirectToAction(nameof(Edit), new { id = nb.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(BomForm form)
    {
        if (!CanEdit) return Denied();
        var lines = form.Lines.Where(l => l.ItemId != null).ToList();
        var ops = form.Ops.Where(o => !string.IsNullOrWhiteSpace(o.Name)).ToList();
        form.Code = form.Code.Trim().ToUpperInvariant();
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một nguyên vật liệu.");
        if (lines.Any(l => l.Quantity <= 0 || l.ScrapPct < 0)) ModelState.AddModelError("", "Định mức phải > 0, hao hụt không âm.");
        if (lines.Any(l => l.ItemId == form.FinishedItemId)) ModelState.AddModelError("", "Thành phẩm không thể là nguyên liệu của chính nó.");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) ModelState.AddModelError("", "Mỗi nguyên liệu chỉ một dòng.");
        if (ops.Any(o => o.SetupMinutes < 0 || o.RunMinutesPerUnit < 0 || o.Workers < 1)) ModelState.AddModelError("", "Thời gian công đoạn không âm, số công nhân ≥ 1.");
        if (form.EffectiveFrom != null && form.EffectiveTo != null && form.EffectiveTo < form.EffectiveFrom) ModelState.AddModelError(nameof(form.EffectiveTo), "Ngày kết thúc không trước ngày bắt đầu.");
        if (form.VersionNo < 1) form.VersionNo = 1;
        if (await Db.Boms.AnyAsync(b => b.Code == form.Code && b.Id != form.Id)) ModelState.AddModelError(nameof(form.Code), "Mã định mức đã tồn tại");
        if (form.FinishedItemId != null && await Db.Boms.AnyAsync(b => b.FinishedItemId == form.FinishedItemId && b.VersionNo == form.VersionNo && b.Id != form.Id))
            ModelState.AddModelError(nameof(form.VersionNo), "Phiên bản này đã có – dùng \"Tạo phiên bản mới\".");
        if (form.FinishedItemId != null && lines.Any() && await _boms.HasCycleAsync(form.FinishedItemId.Value, lines.Select(l => l.ItemId!.Value), form.Id == 0 ? null : form.Id))
            ModelState.AddModelError("", "Định mức bị lặp vòng: bán thành phẩm trong định mức lại chứa chính thành phẩm này.");
        if (!ModelState.IsValid) { await LoadAsync(form); return View("Form", form); }

        Bom b;
        if (form.Id == 0) { b = new Bom(); Db.Boms.Add(b); }
        else
        {
            b = await Db.Boms.Include(x => x.Lines).Include(x => x.Operations).FirstAsync(x => x.Id == form.Id);
            Db.BomLines.RemoveRange(b.Lines.ToList()); b.Lines.Clear();
            Db.BomOperations.RemoveRange(b.Operations.ToList()); b.Operations.Clear();
        }
        b.Code = form.Code; b.FinishedItemId = form.FinishedItemId; b.OutputQuantity = form.OutputQuantity; b.IsActive = form.IsActive; b.Note = form.Note;
        b.VersionNo = form.VersionNo; b.EffectiveFrom = form.EffectiveFrom; b.EffectiveTo = form.EffectiveTo;
        b.LaborCostPerUnit = form.LaborCostPerUnit; b.OverheadPerUnit = form.OverheadPerUnit;
        foreach (var l in lines) b.Lines.Add(new BomLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, ScrapPct = l.ScrapPct });
        var seq = 0;
        foreach (var o in ops.OrderBy(x => x.Seq == 0 ? int.MaxValue : x.Seq))
            b.Operations.Add(new BomOperation { Seq = ++seq * 10, Name = o.Name!.Trim(), WorkCenterId = o.WorkCenterId, SetupMinutes = o.SetupMinutes, RunMinutesPerUnit = o.RunMinutesPerUnit, Workers = Math.Max(1, o.Workers), Note = o.Note });
        if (form.AutoFromRouting && b.Operations.Any())
        {
            var (lab, ohd, _) = BomService.RoutingPerUnit(b, await _boms.CentersAsync());
            b.LaborCostPerUnit = lab; b.OverheadPerUnit = ohd;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu định mức {b.Code}.");
        return RedirectToAction(nameof(Details), new { id = b.Id });
    }

    private async Task LoadAsync(BomForm f)
    {
        ViewBag.Finished = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await Db.Items.Where(i => i.Type == ItemType.FinishedGood || i.Type == ItemType.SemiFinished).OrderBy(i => i.Code)
            .Select(i => new { i.Id, Name = i.Code + " – " + i.Name + (i.Type == ItemType.SemiFinished ? " (bán thành phẩm)" : "") }).ToListAsync(), "Id", "Name", f.FinishedItemId);
        ViewBag.LineOptions = new LineEditorOptions { Items = await ItemsAsync(i => i.Type == ItemType.Material || i.Type == ItemType.SemiFinished), ShowPrice = false, ShowVat = false, ShowScrap = true, QtyLabel = "Định mức", Lines = f.Lines };
        ViewBag.Centers = await Db.WorkCenters.Where(w => w.IsActive).OrderBy(w => w.Code).ToListAsync();
    }
}
