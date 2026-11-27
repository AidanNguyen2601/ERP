using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Báo giá bán hàng và phương án kinh doanh: dự toán giá vốn, lợi nhuận; báo giá lợi nhuận thấp phải được Ban Giám đốc duyệt; chuyển thành đơn bán</summary>
[Authorize(Roles = AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class QuotesController : ErpControllerBase
{
    private readonly QuoteService _svc; private readonly PricingService _pricing;
    public QuotesController(ErpDbContext db, QuoteService svc, PricingService pricing) : base(db) { _svc = svc; _pricing = pricing; }
    private bool CanEdit => User.InAny(AppRoles.SalesTeam);

    public async Task<IActionResult> Index(QuoteStatus? status)
    {
        var q = Db.SalesQuotes.Include(x => x.Customer).Include(x => x.Salesperson).AsQueryable();
        if (status != null) q = q.Where(x => x.Status == status);
        ViewBag.Status = status; ViewBag.CanEdit = CanEdit; ViewBag.Min = await _svc.MinMarginAsync();
        return View(await q.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id).Take(300).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var q = await Db.SalesQuotes.Include(x => x.Customer).Include(x => x.Salesperson).Include(x => x.Warehouse).Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (q == null) return NotFound();
        ViewBag.Whs = await WarehousesAsync(q.WarehouseId); ViewBag.Min = await _svc.MinMarginAsync(); ViewBag.CanEdit = CanEdit; ViewBag.Company = HttpContext.RequestServices.GetRequiredService<IOptions<ErpSettings>>().Value;
        ViewBag.Order = q.SalesOrderId == null ? null : await Db.SalesOrders.FindAsync(q.SalesOrderId);
        return View(q);
    }

    private async Task BagAsync(SalesQuote q)
    {
        ViewBag.Customers = await CustomersAsync(q.CustomerId); ViewBag.Whs = await WarehousesAsync(q.WarehouseId);
        ViewBag.Items = await ItemsAsync(i => i.Type != ItemType.Material);
    }

    public async Task<IActionResult> Create()
    {
        if (!CanEdit) return Denied();
        var q = new SalesQuote { SalespersonId = Me, Lines = { new SalesQuoteLine { Quantity = 1, VatRate = 10 } } };
        await BagAsync(q); return View("Form", q);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var q = await Db.SalesQuotes.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id); if (q == null) return NotFound();
        if (q.Status != QuoteStatus.Draft) { Error("Chỉ sửa báo giá nháp."); return RedirectToAction(nameof(Details), new { id }); }
        await BagAsync(q); return View("Form", q);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SalesQuote m, List<SalesQuoteLine> lines, bool submit, bool useList)
    {
        if (!CanEdit) return Denied();
        lines = lines.Where(l => l.ItemId > 0 && l.Quantity > 0).ToList();
        ModelState.Clear();
        if (m.CustomerId == null) ModelState.AddModelError("", "Chọn khách hàng.");
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng hàng.");
        if (m.ValidUntil < m.Date) ModelState.AddModelError("", "Hiệu lực không trước ngày báo giá.");
        if (!ModelState.IsValid) { m.Lines = lines.Any() ? lines : new List<SalesQuoteLine> { new() { Quantity = 1, VatRate = 10 } }; await BagAsync(m); return View("Form", m); }
        SalesQuote q;
        if (m.Id == 0) { q = new SalesQuote { Number = await DocNumbers.NextAsync(Db.SalesQuotes.Select(x => x.Number), "BG", m.Date) }; Db.SalesQuotes.Add(q); }
        else { q = await Db.SalesQuotes.Include(x => x.Lines).FirstAsync(x => x.Id == m.Id); if (q.Status != QuoteStatus.Draft) throw new BusinessException("Báo giá đã gửi duyệt, không sửa được."); Db.SalesQuoteLines.RemoveRange(q.Lines); q.Lines.Clear(); }
        q.Date = m.Date.Date; q.ValidUntil = m.ValidUntil.Date; q.CustomerId = m.CustomerId; q.WarehouseId = m.WarehouseId; q.OtherCost = Math.Max(0, m.OtherCost); q.Note = m.Note; q.SalespersonId = m.SalespersonId ?? Me;
        var items = (await Db.Items.Where(i => lines.Select(x => x.ItemId).Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id);
        foreach (var l in lines)
        {
            var it = items[l.ItemId]; var price = l.UnitPrice; var disc = l.DiscountPct;
            if (useList || price <= 0) { var r = await _pricing.GetAsync(l.ItemId, q.CustomerId, l.Quantity, q.Date); price = r.Price; disc = r.DiscountPct; }
            q.Lines.Add(new SalesQuoteLine { ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = price, DiscountPct = Math.Clamp(disc, 0, 100), VatRate = it.VatRate, UnitCost = l.UnitCost > 0 ? l.UnitCost : it.AvgCost });
        }
        QuoteService.Recalc(q);
        await Db.SaveChangesAsync();
        if (submit) Success(await _svc.SubmitAsync(q)); else Success($"Đã lưu nháp báo giá {q.Number} – lợi nhuận dự kiến {q.MarginPct:0.#}%.");
        return RedirectToAction(nameof(Details), new { id = q.Id });
    }

    private async Task<SalesQuote?> LoadAsync(int id) => await Db.SalesQuotes.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id) { if (!CanEdit) return Denied(); var q = await LoadAsync(id); if (q == null) return NotFound(); Success(await _svc.SubmitAsync(q)); return RedirectToAction(nameof(Details), new { id }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, bool approve)
    {
        var q = await LoadAsync(id); if (q == null) return NotFound();
        await _svc.ApproveAsync(q, approve);
        Success(approve ? "Đã duyệt báo giá." : "Đã từ chối, báo giá trở về nháp.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int id)
    {
        if (!CanEdit) return Denied();
        var q = await LoadAsync(id); if (q == null) return NotFound();
        if (q.Status != QuoteStatus.Approved) throw new BusinessException("Chỉ gửi khách báo giá đã được duyệt.");
        q.Status = QuoteStatus.Sent; await Db.SaveChangesAsync();
        Success("Đã đánh dấu gửi báo giá cho khách hàng (in báo giá để gửi).");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id)
    {
        if (!CanEdit) return Denied();
        var q = await LoadAsync(id); if (q == null) return NotFound();
        if (q.Status is QuoteStatus.Converted or QuoteStatus.Cancelled) throw new BusinessException("Báo giá đã chốt.");
        q.Status = QuoteStatus.Rejected; await Db.SaveChangesAsync();
        Success("Đã ghi nhận khách hàng từ chối báo giá.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToOrder(int id, int warehouseId)
    {
        if (!CanEdit) return Denied();
        var q = await LoadAsync(id); if (q == null) return NotFound();
        var so = await _svc.ToOrderAsync(q, warehouseId);
        Success($"Đã lập đơn bán nháp {so.Number} từ báo giá {q.Number}.");
        return RedirectToAction("Details", "SalesOrders", new { id = so.Id });
    }

    public async Task<IActionResult> Print(int id)
    {
        var q = await Db.SalesQuotes.Include(x => x.Customer).Include(x => x.Salesperson).Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (q == null) return NotFound();
        ViewBag.Company = HttpContext.RequestServices.GetRequiredService<IOptions<ErpSettings>>().Value;
        return View(q);
    }

    /// <summary>Giá bán và giá vốn gợi ý khi chọn hàng trên form báo giá</summary>
    public async Task<IActionResult> Lookup(int itemId, int? partnerId, decimal qty = 1)
    {
        var r = await _pricing.GetAsync(itemId, partnerId, qty <= 0 ? 1 : qty, DateTime.Today);
        var item = await Db.Items.FindAsync(itemId);
        return Json(new { price = r.Price, discountPct = r.DiscountPct, source = r.Source, cost = item?.AvgCost ?? 0, vat = item?.VatRate ?? 10 });
    }
}
