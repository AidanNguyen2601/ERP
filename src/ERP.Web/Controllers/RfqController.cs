using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Yêu cầu báo giá (RFQ): gửi nhiều nhà cung cấp, nhập báo giá, so sánh giá theo từng dòng, chọn nhà cung cấp và lập đơn mua</summary>
[Authorize(Roles = AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class RfqController : ErpControllerBase
{
    private readonly RequisitionService _svc;
    public RfqController(ErpDbContext db, RequisitionService svc) : base(db) => _svc = svc;
    private bool CanBuy => User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant);

    public async Task<IActionResult> Index() =>
        View(await Db.Rfqs.Include(r => r.PurchaseRequisition).Include(r => r.Vendors).Include(r => r.Lines).Include(r => r.AwardedPartner).OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(200).ToListAsync());

    public async Task<IActionResult> Details(int id)
    {
        var r = await Db.Rfqs.Include(x => x.PurchaseRequisition).Include(x => x.Lines).ThenInclude(l => l.Item).Include(x => x.Vendors).ThenInclude(v => v.Partner)
            .Include(x => x.Quotes).Include(x => x.AwardedPartner).FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();
        ViewBag.Whs = await WarehousesAsync(); ViewBag.CanBuy = CanBuy;
        ViewBag.Order = r.PurchaseOrderId == null ? null : await Db.PurchaseOrders.FindAsync(r.PurchaseOrderId);
        return View(r);
    }

    /// <summary>Tạo RFQ trực tiếp (không qua đề nghị mua)</summary>
    public async Task<IActionResult> Create()
    {
        if (!CanBuy) return Denied();
        ViewBag.Items = await ItemsAsync(i => i.IsStocked);
        ViewBag.Vendors = await Db.Partners.Where(v => v.IsVendor && v.IsActive && !v.IsBlocked).OrderBy(v => v.Code).ToListAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? note, DateTime dueDate, int[] itemId, decimal[] qty, int[] vendorIds)
    {
        if (!CanBuy) return Denied();
        var rfq = new Rfq { Date = DateTime.Today, DueDate = dueDate, Note = note, Status = RfqStatus.Sent, CreatedById = Me, Number = await DocNumbers.NextAsync(Db.Rfqs.Select(x => x.Number), "RFQ", DateTime.Today) };
        for (var i = 0; i < itemId.Length; i++) if (itemId[i] > 0 && qty.ElementAtOrDefault(i) > 0) rfq.Lines.Add(new RfqLine { ItemId = itemId[i], Quantity = qty[i] });
        foreach (var v in vendorIds.Distinct()) rfq.Vendors.Add(new RfqVendor { PartnerId = v });
        if (!rfq.Lines.Any() || !rfq.Vendors.Any()) { Error("Chọn ít nhất một mặt hàng (số lượng > 0) và một nhà cung cấp."); return RedirectToAction(nameof(Create)); }
        Db.Rfqs.Add(rfq); await Db.SaveChangesAsync();
        Success($"Đã tạo yêu cầu báo giá {rfq.Number}.");
        return RedirectToAction(nameof(Details), new { id = rfq.Id });
    }

    /// <summary>Nhập báo giá của các nhà cung cấp: price[lineId_partnerId]</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveQuotes(int id, IFormCollection form)
    {
        if (!CanBuy) return Denied();
        var r = await Db.Rfqs.Include(x => x.Lines).Include(x => x.Vendors).Include(x => x.Quotes).FirstOrDefaultAsync(x => x.Id == id); if (r == null) return NotFound();
        if (r.Status == RfqStatus.Awarded) throw new BusinessException("RFQ đã chọn nhà cung cấp, không sửa báo giá.");
        foreach (var v in r.Vendors)
        {
            int.TryParse(form[$"lead_{v.PartnerId}"], out var lead); v.LeadDays = Math.Max(0, lead); v.Terms = form[$"terms_{v.PartnerId}"].ToString();
            var any = false;
            foreach (var l in r.Lines)
            {
                var ok = decimal.TryParse(form[$"price_{l.Id}_{v.PartnerId}"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price) && price > 0;
                var q = r.Quotes.FirstOrDefault(x => x.RfqLineId == l.Id && x.PartnerId == v.PartnerId);
                if (ok) { any = true; if (q == null) Db.RfqQuotes.Add(new RfqQuote { RfqId = r.Id, RfqLineId = l.Id, PartnerId = v.PartnerId, UnitPrice = price }); else q.UnitPrice = price; }
                else if (q != null) Db.RfqQuotes.Remove(q);
            }
            v.Responded = any;
        }
        await Db.SaveChangesAsync();
        Success("Đã lưu báo giá.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Award(int id, int vendorId, int warehouseId)
    {
        if (!CanBuy) return Denied();
        var r = await Db.Rfqs.Include(x => x.Lines).Include(x => x.Vendors).Include(x => x.Quotes).FirstOrDefaultAsync(x => x.Id == id); if (r == null) return NotFound();
        var po = await _svc.AwardAsync(r, vendorId, warehouseId);
        Success($"Đã chọn nhà cung cấp và tạo đơn mua nháp {po.Number}.");
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanBuy) return Denied();
        var r = await Db.Rfqs.FindAsync(id); if (r == null) return NotFound();
        if (r.Status == RfqStatus.Awarded) throw new BusinessException("RFQ đã lập đơn mua, không hủy được.");
        r.Status = RfqStatus.Cancelled; await Db.SaveChangesAsync();
        Success("Đã hủy RFQ.");
        return RedirectToAction(nameof(Index));
    }
}
