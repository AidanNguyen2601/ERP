using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

/// <summary>Kế hoạch kiểm tra chất lượng: bộ tiêu chí theo công đoạn × mặt hàng / loại hàng</summary>
public class QcPlansController : ErpControllerBase
{
    public QcPlansController(ErpDbContext db) : base(db) { }
    private bool CanEdit => User.InAny(AppRoles.QcManagers);

    public async Task<IActionResult> Index(QcStage? stage)
    {
        var q = Db.QcPlans.Include(p => p.Item).Include(p => p.Lines).AsQueryable();
        if (stage.HasValue) q = q.Where(p => p.Stage == stage);
        ViewBag.Stage = stage; ViewBag.CanEdit = CanEdit;
        ViewBag.Usage = await Db.QcInspections.Where(i => i.QcPlanId != null).GroupBy(i => i.QcPlanId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return View(await q.OrderBy(p => p.Stage).ThenBy(p => p.Code).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await Db.QcPlans.Include(x => x.Item).Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        ViewBag.CanEdit = CanEdit;
        return View(p);
    }

    public async Task<IActionResult> Create(QcStage stage = QcStage.Incoming)
    {
        if (!CanEdit) return Denied();
        await LoadAsync();
        return View("Form", new QcPlan { Stage = stage, Lines = { new QcPlanLine { Name = "" } } });
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var p = await Db.QcPlans.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        p.Lines = p.Lines.OrderBy(l => l.Seq).ToList();
        await LoadAsync(p.ItemId);
        return View("Form", p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(QcPlan model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        var lines = model.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList();
        // Dòng trống bị bỏ qua: xóa lỗi validate của chúng
        foreach (var k in ModelState.Keys.Where(k => k.StartsWith("Lines[")).ToList()) ModelState.Remove(k);
        if (await Db.QcPlans.AnyAsync(p => p.Code == model.Code && p.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã kế hoạch đã tồn tại");
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một tiêu chí kiểm tra.");
        if (model.ItemId.HasValue) model.ItemType = null;
        if (model.SamplingMethod == QcSamplingMethod.Aql) { model.AqlValue ??= 1.0m; if (!Services.QualityService.AqlOptions.Contains(model.AqlValue.Value)) ModelState.AddModelError(nameof(model.AqlValue), "AQL chọn 0,65 / 1,0 / 1,5 / 2,5 / 4,0."); }
        if (model.SamplingMethod == QcSamplingMethod.Fixed && model.FixedSampleQty <= 0) ModelState.AddModelError(nameof(model.FixedSampleQty), "Nhập số lượng mẫu cố định > 0.");
        if (model.Stage != QcStage.InProcess) model.OperationName = null;
        foreach (var l in lines)
        {
            l.Name = l.Name.Trim();
            if (l.Type == QcCriterionType.Measure)
            {
                if (l.MinValue == null && l.MaxValue == null) ModelState.AddModelError("", $"Tiêu chí \"{l.Name}\": nhập giới hạn dưới và / hoặc trên.");
                if (l.MinValue > l.MaxValue) ModelState.AddModelError("", $"Tiêu chí \"{l.Name}\": giới hạn dưới lớn hơn giới hạn trên.");
                if (string.IsNullOrWhiteSpace(l.Standard))
                    l.Standard = l.MinValue.HasValue && l.MaxValue.HasValue ? $"{l.MinValue.Value.ToQty()} – {l.MaxValue.Value.ToQty()} {l.Unit}".Trim()
                        : l.MinValue.HasValue ? $"≥ {l.MinValue.Value.ToQty()} {l.Unit}".Trim() : $"≤ {l.MaxValue!.Value.ToQty()} {l.Unit}".Trim();
            }
            else { l.MinValue = null; l.MaxValue = null; }
        }
        QcPlan? existing = model.Id == 0 ? null : await Db.QcPlans.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == model.Id);
        if (model.Id != 0 && existing == null) return NotFound();
        if (!ModelState.IsValid) { model.Lines = lines.Any() ? lines : new List<QcPlanLine> { new() }; await LoadAsync(model.ItemId); return View("Form", model); }

        var target = existing ?? new QcPlan();
        target.Code = model.Code; target.Name = model.Name.Trim(); target.Stage = model.Stage; target.ItemId = model.ItemId; target.ItemType = model.ItemType;
        target.SamplePct = model.SamplePct; target.Note = model.Note; target.IsActive = model.IsActive;
        target.SamplingMethod = model.SamplingMethod; target.AqlValue = model.SamplingMethod == QcSamplingMethod.Aql ? model.AqlValue : null; target.FixedSampleQty = model.FixedSampleQty;
        target.MaxDefectPct = model.MaxDefectPct; target.OperationName = string.IsNullOrWhiteSpace(model.OperationName) ? null : model.OperationName.Trim();
        // Phiếu kiểm tra đã lập giữ bản sao tiêu chí riêng nên thay toàn bộ dòng kế hoạch không ảnh hưởng lịch sử
        if (existing != null) Db.QcPlanLines.RemoveRange(existing.Lines.ToList());
        target.Lines = lines.Select((l, i) => new QcPlanLine
        {
            Seq = i + 1, Name = l.Name, Method = l.Method, Standard = l.Standard, Type = l.Type, MinValue = l.MinValue, MaxValue = l.MaxValue, Unit = l.Unit, IsCritical = l.IsCritical
        }).ToList();
        if (existing == null) Db.QcPlans.Add(target);
        await Db.SaveChangesAsync();
        Success($"Đã lưu kế hoạch kiểm tra {target.Code} ({target.Lines.Count} tiêu chí).");
        return RedirectToAction(nameof(Details), new { id = target.Id });
    }

    private async Task LoadAsync(int? itemId = null)
    {
        ViewBag.Items = new SelectList(await Db.Items.Where(i => (i.IsActive && i.Type != ItemType.Service) || i.Id == itemId).OrderBy(i => i.Code)
            .Select(i => new { i.Id, Name = i.Code + " – " + i.Name }).ToListAsync(), "Id", "Name", itemId);
    }
}
