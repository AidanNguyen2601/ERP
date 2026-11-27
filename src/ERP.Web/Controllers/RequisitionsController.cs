using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Đề nghị mua hàng (PR): lập theo kế hoạch / định mức / tồn tối thiểu, duyệt theo quy tắc, chuyển thành đơn mua hoặc yêu cầu báo giá</summary>
public class RequisitionsController : ErpControllerBase
{
    private readonly RequisitionService _svc;
    public RequisitionsController(ErpDbContext db, RequisitionService svc) : base(db) => _svc = svc;
    private bool CanWrite => User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Warehouse + "," + AppRoles.Production + "," + AppRoles.Sales);
    private bool CanBuy => User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant);

    public async Task<IActionResult> Index(RequestStatus? status)
    {
        var q = Db.PurchaseRequisitions.Include(p => p.Requester).Include(p => p.Lines).AsQueryable();
        if (status != null) q = q.Where(p => p.Status == status);
        ViewBag.Status = status; ViewBag.CanWrite = CanWrite; ViewBag.CanBuy = CanBuy;
        return View(await q.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(300).ToListAsync());
    }

    public async Task<IActionResult> Details(int id, [FromServices] WorkflowService wf)
    {
        var p = await Db.PurchaseRequisitions.Include(x => x.Requester).Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        ViewBag.Vendors = new SelectList(await Db.Partners.Where(v => v.IsVendor && v.IsActive && !v.IsBlocked).OrderBy(v => v.Code).Select(v => new { v.Id, Name = v.Code + " – " + v.Name + (v.Grade != null ? " [" + v.Grade + "]" : "") }).ToListAsync(), "Id", "Name");
        ViewBag.Whs = await WarehousesAsync();
        ViewBag.Rfqs = await Db.Rfqs.Where(r => r.PurchaseRequisitionId == id).ToListAsync();
        ViewBag.Orders = await Db.PurchaseOrders.Where(o => o.RequisitionId == id).ToListAsync();
        ViewBag.Approval = await wf.LatestAsync(ApprovalDocType.PurchaseRequisition, id);
        ViewBag.CanBuy = CanBuy; ViewBag.CanWrite = CanWrite;
        return View(p);
    }

    private async Task BagAsync(PurchaseRequisition p)
    {
        ViewBag.Depts = await Db.CatalogItems.Where(c => c.Type == CatalogType.Department).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
        ViewBag.LineOptions = new LineEditorOptions
        {
            Items = await ItemsAsync(i => i.IsStocked), PriceSource = "cost", ShowVat = false, PriceLabel = "Đơn giá dự kiến",
            Lines = p.Lines.Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = l.EstimatedPrice }).ToList()
        };
    }

    public async Task<IActionResult> Create()
    {
        if (!CanWrite) return Denied();
        var u = await Db.Users.FindAsync(Me);
        var p = new PurchaseRequisition { Department = u?.Department };
        await BagAsync(p);
        return View("Form", p);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanWrite) return Denied();
        var p = await Db.PurchaseRequisitions.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != RequestStatus.Draft) { Error("Chỉ sửa đề nghị nháp."); return RedirectToAction(nameof(Details), new { id }); }
        await BagAsync(p);
        return View("Form", p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PurchaseRequisition m, List<LineInput> lines, bool submit)
    {
        if (!CanWrite) return Denied();
        lines = lines.Where(l => l.ItemId != null && l.Quantity > 0).ToList();
        ModelState.Clear();
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng hàng với số lượng > 0.");
        if (m.RequiredDate < m.Date) ModelState.AddModelError("", "Ngày cần hàng không trước ngày đề nghị.");
        if (!ModelState.IsValid) { m.Lines = lines.Select(l => new PurchaseRequisitionLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, EstimatedPrice = l.UnitPrice }).ToList(); await BagAsync(m); return View("Form", m); }
        PurchaseRequisition p;
        if (m.Id == 0) { p = new PurchaseRequisition { RequesterId = Me, Number = await _svc.NextNumberAsync(m.Date), Source = "Thủ công" }; Db.PurchaseRequisitions.Add(p); }
        else
        {
            p = await Db.PurchaseRequisitions.Include(x => x.Lines).FirstAsync(x => x.Id == m.Id);
            if (p.Status != RequestStatus.Draft) throw new BusinessException("Đề nghị đã gửi duyệt, không sửa được.");
            Db.PurchaseRequisitionLines.RemoveRange(p.Lines.ToList()); p.Lines.Clear();
        }
        p.Date = m.Date.Date; p.RequiredDate = m.RequiredDate.Date; p.Department = m.Department; p.Purpose = m.Purpose;
        foreach (var l in lines) p.Lines.Add(new PurchaseRequisitionLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, EstimatedPrice = l.UnitPrice });
        await Db.SaveChangesAsync();
        if (submit) Success(await _svc.SubmitAsync(p)); else Success($"Đã lưu nháp đề nghị {p.Number}.");
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        if (!CanWrite) return Denied();
        var p = await Db.PurchaseRequisitions.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        Success(await _svc.SubmitAsync(p));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanWrite) return Denied();
        var p = await Db.PurchaseRequisitions.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        if (p.Lines.Any(l => l.OrderedQty > 0)) throw new BusinessException("Đề nghị đã có dòng được đặt hàng, không hủy được.");
        if (p.Status is RequestStatus.Converted or RequestStatus.Cancelled) throw new BusinessException("Đề nghị không hủy được.");
        p.Status = RequestStatus.Cancelled; await Db.SaveChangesAsync();
        Success($"Đã hủy đề nghị {p.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Tạo đề nghị mua từ cảnh báo tồn tối thiểu (dựa trên tồn kho, đơn mua và đề nghị đang mở)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FromLowStock()
    {
        if (!CanWrite) return Denied();
        var p = await _svc.FromLowStockAsync(DateTime.Today, Me);
        if (p == null) { Success("Không có mặt hàng nào dưới mức tồn tối thiểu cần đề nghị mua."); return RedirectToAction(nameof(Index)); }
        Success($"Đã tạo đề nghị {p.Number} gồm {p.Lines.Count} mặt hàng dưới mức tồn tối thiểu.");
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    /// <summary>Tạo đề nghị mua từ định mức sản xuất: nhu cầu NVL của các lệnh sản xuất chưa hoàn thành, trừ tồn kho và hàng đang về</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FromProduction()
    {
        if (!CanWrite) return Denied();
        var orders = await Db.ProductionOrders.Include(o => o.Bom).ThenInclude(b => b!.Lines).ThenInclude(l => l.Item)
            .Where(o => o.Status == ProductionStatus.Draft || o.Status == ProductionStatus.Released).ToListAsync();
        var need = new Dictionary<int, (Item Item, decimal Qty)>();
        foreach (var o in orders)
        {
            var ratio = o.Bom!.OutputQuantity == 0 ? 0 : o.Quantity / o.Bom.OutputQuantity;
            foreach (var l in o.Bom.Lines) { var q = Math.Ceiling(l.Quantity * (1 + l.ScrapPct / 100) * ratio); need[l.ItemId] = (l.Item!, need.GetValueOrDefault(l.ItemId).Qty + q); }
        }
        var onHand = (await Db.StockLevels.ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        var open = (await Db.PurchaseOrderLines.Where(l => l.PurchaseOrder!.Status != PoStatus.Cancelled && l.PurchaseOrder.Status != PoStatus.Draft && l.Quantity > l.ReceivedQty).Select(l => new { l.ItemId, Q = l.Quantity - l.ReceivedQty }).ToListAsync())
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Q));
        var pr = new PurchaseRequisition { Date = DateTime.Today, RequiredDate = DateTime.Today.AddDays(7), Number = await _svc.NextNumberAsync(DateTime.Today), Source = "Định mức sản xuất", Purpose = $"Vật tư thiếu cho {orders.Count} lệnh sản xuất theo định mức (BOM)", RequesterId = Me, Department = "Xưởng sản xuất" };
        foreach (var (id, (item, qty)) in need)
        {
            var lack = qty - onHand.GetValueOrDefault(id) - open.GetValueOrDefault(id);
            if (lack > 0) pr.Lines.Add(new PurchaseRequisitionLine { ItemId = id, Quantity = lack, EstimatedPrice = item.AvgCost, Note = $"Cần {qty:0.##}, tồn {onHand.GetValueOrDefault(id):0.##}" });
        }
        if (pr.Lines.Count == 0) { Success("Vật tư cho các lệnh sản xuất chưa chạy đã đủ (tồn kho + đơn mua đang về)."); return RedirectToAction(nameof(Index)); }
        Db.PurchaseRequisitions.Add(pr); await Db.SaveChangesAsync();
        Success($"Đã tạo đề nghị {pr.Number}: {pr.Lines.Count} vật tư thiếu theo định mức.");
        return RedirectToAction(nameof(Details), new { id = pr.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToPo(int id, int vendorId, int warehouseId, int[] lineId)
    {
        if (!CanBuy) return Denied();
        var p = await Db.PurchaseRequisitions.Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        var po = await _svc.ToPurchaseOrderAsync(p, vendorId, warehouseId, lineId.Length == 0 ? p.Lines.Select(l => l.Id) : lineId);
        Success($"Đã tạo đơn mua nháp {po.Number} từ đề nghị {p.Number}.");
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToRfq(int id, int[] vendorIds)
    {
        if (!CanBuy) return Denied();
        var p = await Db.PurchaseRequisitions.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        var rfq = await _svc.ToRfqAsync(p, vendorIds);
        Success($"Đã tạo yêu cầu báo giá {rfq.Number} gửi {vendorIds.Length} nhà cung cấp.");
        return RedirectToAction("Details", "Rfq", new { id = rfq.Id });
    }
}
