using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Phiếu kiểm tra chất lượng (KCS)</summary>
public class QcInspectionsController : ErpControllerBase
{
    private readonly QualityService _qc;
    public QcInspectionsController(ErpDbContext db, QualityService qc) : base(db) => _qc = qc;
    private bool CanWrite => User.InAny(AppRoles.QcInspectors);

    public async Task<IActionResult> Index(QcStage? stage, QcResult? result, QcStatus? status, DateTime? from, DateTime? to, string? q)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-3)).Date;
        var query = Db.QcInspections.Include(i => i.Item).Include(i => i.Partner).Include(i => i.Inspector).Where(i => i.Date >= f && i.Date <= t);
        if (stage.HasValue) query = query.Where(i => i.Stage == stage);
        if (result.HasValue) query = query.Where(i => i.Result == result);
        if (status.HasValue) query = query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(i => i.Number.Contains(q) || i.Item!.Code.Contains(q) || i.Item.Name.Contains(q) || (i.OriginRef != null && i.OriginRef.Contains(q)));
        ViewBag.Stage = stage; ViewBag.Result = result; ViewBag.Status = status; ViewBag.From = f; ViewBag.To = t; ViewBag.Q = q; ViewBag.CanWrite = CanWrite;
        return View(await query.OrderByDescending(i => i.Date).ThenByDescending(i => i.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var i = await Load(id);
        if (i == null) return NotFound();
        ViewBag.Ncrs = await Db.QcNonconformances.Where(n => n.InspectionId == id).ToListAsync();
        ViewBag.CanWrite = CanWrite;
        return View(i);
    }

    /// <summary>
    /// Lập phiếu: từ đơn mua (poId + itemId), lệnh sản xuất (productionId), đơn bán (soId + itemId) hoặc thủ công (stage + itemId + lotQty).
    /// </summary>
    public async Task<IActionResult> Create(QcStage stage = QcStage.Incoming, int? poId = null, int? productionId = null, int? soId = null,
        int? itemId = null, decimal? lotQty = null, int? warehouseId = null, int? partnerId = null, int? qcPlanId = null)
    {
        if (!CanWrite) return Denied();
        QcInspection? draft = null;
        if (poId.HasValue)
        {
            var po = await Db.PurchaseOrders.Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == poId);
            if (po == null) return NotFound();
            var line = po.Lines.FirstOrDefault(l => l.ItemId == itemId) ?? po.Lines.FirstOrDefault(l => l.ReceivedQty > 0) ?? po.Lines.First();
            var done = await InspectedAsync(DocOrigin.Purchase, po.Id, line.ItemId);
            var basis = line.ReceivedQty > 0 ? line.ReceivedQty : line.Quantity;
            draft = await _qc.DraftAsync(QcStage.Incoming, line.Item!, lotQty ?? Math.Max(basis - done, 0), DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, qcPlanId);
        }
        else if (productionId.HasValue)
        {
            var mo = await Db.ProductionOrders.Include(p => p.Item).FirstOrDefaultAsync(p => p.Id == productionId);
            if (mo == null) return NotFound();
            var done = await InspectedAsync(DocOrigin.Production, mo.Id, mo.ItemId);
            var basis = mo.CompletedQty > 0 ? mo.CompletedQty : mo.Quantity;
            var st = mo.Status is ProductionStatus.Completed or ProductionStatus.Closed ? QcStage.Final : QcStage.InProcess;
            draft = await _qc.DraftAsync(st, mo.Item!, lotQty ?? Math.Max(basis - done, 0), DocOrigin.Production, mo.Number, mo.Id, null,
                mo.Status is ProductionStatus.Completed or ProductionStatus.Closed ? mo.FinishedWarehouseId : mo.MaterialWarehouseId, qcPlanId);
        }
        else if (soId.HasValue)
        {
            var so = await Db.SalesOrders.Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == soId);
            if (so == null) return NotFound();
            var line = so.Lines.FirstOrDefault(l => l.ItemId == itemId) ?? so.Lines.First();
            var done = await InspectedAsync(DocOrigin.Sales, so.Id, line.ItemId);
            draft = await _qc.DraftAsync(QcStage.Outgoing, line.Item!, lotQty ?? Math.Max(line.Quantity - done, 0), DocOrigin.Sales, so.Number, so.Id, so.CustomerId, so.WarehouseId, qcPlanId);
        }
        else if (itemId.HasValue)
        {
            var item = await Db.Items.FindAsync(itemId);
            if (item == null) return NotFound();
            draft = await _qc.DraftAsync(stage, item, lotQty ?? 0, DocOrigin.Manual, null, null, partnerId, warehouseId, qcPlanId);
        }
        draft ??= new QcInspection { Stage = stage, LotQty = lotQty ?? 0 };
        if (draft.LotQty == 0 && draft.ItemId != null) TempData["Warning"] = "Chứng từ gốc không còn số lượng chưa kiểm tra — nhập số lượng lô cần kiểm tra.";
        await LoadAsync(draft);
        return View("Form", draft);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanWrite) return Denied();
        var i = await Load(id);
        if (i == null) return NotFound();
        if (i.Status != QcStatus.Draft) { Error("Phiếu đã kết luận, không sửa được."); return RedirectToAction(nameof(Details), new { id }); }
        await LoadAsync(i);
        return View("Form", i);
    }

    /// <summary>Lưu nháp (submit=draft) hoặc kết luận (submit=complete)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(QcInspection model, string submit = "draft")
    {
        if (!CanWrite) return Denied();
        var existing = model.Id == 0 ? null : await Db.QcInspections.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == model.Id);
        if (model.Id != 0 && existing == null) return NotFound();
        if (existing != null && existing.Status != QcStatus.Draft) { Error("Phiếu đã kết luận."); return RedirectToAction(nameof(Details), new { id = model.Id }); }
        foreach (var k in ModelState.Keys.Where(k => k.StartsWith("Lines[")).ToList()) ModelState.Remove(k);
        ModelState.Remove(nameof(model.Number)); // số phiếu do hệ thống cấp
        if (!ModelState.IsValid) { await ReloadForm(model); return View("Form", model); }

        var i = existing ?? new QcInspection { Number = await _qc.NextInspectionNumberAsync(model.Date), InspectorId = Me, CreatedAt = DateTime.Now };
        i.Date = model.Date.Date; i.Stage = model.Stage; i.ItemId = model.ItemId; i.WarehouseId = model.WarehouseId; i.PartnerId = model.PartnerId;
        i.Origin = model.Origin; i.OriginRef = model.OriginRef; i.OriginId = model.OriginId; i.QcPlanId = model.QcPlanId;
        i.LotQty = model.LotQty; i.SampleQty = model.SampleQty; i.AcceptedQty = model.AcceptedQty; i.RejectedQty = model.RejectedQty;
        i.Result = model.Result; i.Note = model.Note;
        if (existing != null) Db.QcInspectionLines.RemoveRange(existing.Lines.ToList());
        i.Lines = model.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Name)).Select((l, n) =>
        {
            var x = new QcInspectionLine
            {
                Seq = n + 1, Name = l.Name, Method = l.Method, Standard = l.Standard, Type = l.Type, MinValue = l.MinValue, MaxValue = l.MaxValue,
                Unit = l.Unit, IsCritical = l.IsCritical, MeasuredValue = l.MeasuredValue, Passed = l.Passed, Note = l.Note
            };
            QualityService.Evaluate(x);
            return x;
        }).ToList();
        if (existing == null) Db.QcInspections.Add(i);
        await Db.SaveChangesAsync();

        if (submit == "complete")
        {
            try
            {
                var ncr = await _qc.CompleteAsync(i);
                Success($"Đã kết luận phiếu {i.Number}: {i.Result.GetDisplayName()}." + (ncr != null ? $" Đã lập {ncr.Number} cho {ncr.Quantity.ToQty()} không đạt." : ""));
                return ncr != null ? RedirectToAction("Details", "QcNcrs", new { id = ncr.Id }) : RedirectToAction(nameof(Details), new { id = i.Id });
            }
            catch (BusinessException ex)
            {
                // Giữ bản nháp đã lưu, báo lỗi kết luận
                Error(ex.Message);
                return RedirectToAction(nameof(Edit), new { id = i.Id });
            }
        }
        Success($"Đã lưu nháp phiếu {i.Number}.");
        return RedirectToAction(nameof(Edit), new { id = i.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanWrite) return Denied();
        var i = await Db.QcInspections.FindAsync(id);
        if (i == null) return NotFound();
        if (i.Status != QcStatus.Draft) { Error("Chỉ hủy được phiếu nháp."); return RedirectToAction(nameof(Details), new { id }); }
        i.Status = QcStatus.Cancelled;
        await Db.SaveChangesAsync();
        Success($"Đã hủy phiếu {i.Number}.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<QcInspection?> Load(int id)
    {
        var r = await Db.QcInspections.Include(i => i.Item).Include(i => i.Partner).Include(i => i.Warehouse)
            .Include(i => i.QcPlan).Include(i => i.Inspector).Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
        if (r != null) r.Lines = r.Lines.OrderBy(l => l.Seq).ToList();
        return r;
    }

    /// <summary>Số lượng đã kiểm tra (phiếu chưa hủy) của một chứng từ gốc × mặt hàng</summary>
    private Task<decimal> InspectedAsync(DocOrigin origin, int originId, int itemId) =>
        Db.QcInspections.Where(i => i.Origin == origin && i.OriginId == originId && i.ItemId == itemId && i.Status != QcStatus.Cancelled).SumAsync(i => i.LotQty);

    private async Task ReloadForm(QcInspection m)
    {
        if (m.ItemId.HasValue) m.Item = await Db.Items.FindAsync(m.ItemId);
        if (m.QcPlanId.HasValue) m.QcPlan = await Db.QcPlans.FindAsync(m.QcPlanId);
        await LoadAsync(m);
    }

    private async Task LoadAsync(QcInspection m)
    {
        ViewBag.Items = new SelectList(await Db.Items.Where(i => (i.IsActive && i.Type != ItemType.Service) || i.Id == m.ItemId).OrderBy(i => i.Code)
            .Select(i => new { i.Id, Name = i.Code + " – " + i.Name }).ToListAsync(), "Id", "Name", m.ItemId);
        ViewBag.Whs = await WarehousesAsync(m.WarehouseId);
        ViewBag.Partners = await PartnersAsync(m.PartnerId);
        ViewBag.Plans = new SelectList(await Db.QcPlans.Where(p => (p.IsActive && p.Stage == m.Stage) || p.Id == m.QcPlanId).OrderBy(p => p.Code)
            .Select(p => new { p.Id, Name = p.Code + " – " + p.Name }).ToListAsync(), "Id", "Name", m.QcPlanId);
    }
}
