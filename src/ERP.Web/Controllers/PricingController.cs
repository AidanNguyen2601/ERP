using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Bảng giá bán theo khách hàng / nhóm khách / thời gian / loại tiền, bậc số lượng, chiết khấu và khuyến mại</summary>
[Authorize(Roles = AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class PricingController : ErpControllerBase
{
    private readonly PricingService _svc;
    public PricingController(ErpDbContext db, PricingService svc) : base(db) => _svc = svc;
    private bool CanEdit => User.InAny(AppRoles.SalesTeam + "," + AppRoles.Accountant);

    public async Task<IActionResult> Index()
    {
        ViewBag.Promos = await Db.Promotions.Include(p => p.Item).OrderByDescending(p => p.IsActive).ThenByDescending(p => p.ValidFrom).ToListAsync();
        ViewBag.Items = await ItemsAsync(i => i.Type != ItemType.Material);
        ViewBag.Groups = await Db.Partners.Where(p => p.PartnerGroup != null).Select(p => p.PartnerGroup!).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.CanEdit = CanEdit;
        return View(await Db.PriceLists.Include(l => l.Partner).Include(l => l.Lines).OrderByDescending(l => l.IsActive).ThenByDescending(l => l.Priority).ToListAsync());
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (!CanEdit) return Denied();
        var l = id == null ? new PriceList { Code = $"BG{DateTime.Today:yyMM}-{(await Db.PriceLists.CountAsync() + 1):00}" } : await Db.PriceLists.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        if (l.Lines.Count == 0) l.Lines.Add(new PriceListLine());
        await BagAsync();
        return View(l);
    }

    private async Task BagAsync()
    {
        ViewBag.Items = await ItemsAsync();
        ViewBag.Customers = await CustomersAsync();
        ViewBag.Groups = await Db.Partners.Where(p => p.PartnerGroup != null).Select(p => p.PartnerGroup!).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.Currencies = await Db.Currencies.Where(c => c.IsActive).Select(c => c.Code).ToListAsync();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PriceList m, List<PriceListLine> lines)
    {
        if (!CanEdit) return Denied();
        lines = lines.Where(l => l.ItemId > 0 && (l.Price > 0 || l.DiscountPct > 0)).ToList();
        ModelState.Clear();
        if (string.IsNullOrWhiteSpace(m.Code) || string.IsNullOrWhiteSpace(m.Name)) ModelState.AddModelError("", "Nhập mã và tên bảng giá.");
        if (await Db.PriceLists.AnyAsync(x => x.Code == m.Code && x.Id != m.Id)) ModelState.AddModelError("", "Mã bảng giá đã tồn tại.");
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng giá.");
        if (m.ValidTo != null && m.ValidTo < m.ValidFrom) ModelState.AddModelError("", "Ngày kết thúc không trước ngày bắt đầu.");
        if (lines.GroupBy(l => (l.ItemId, l.MinQty)).Any(g => g.Count() > 1)) ModelState.AddModelError("", "Trùng hàng hóa + bậc số lượng.");
        if (!ModelState.IsValid) { m.Lines = lines.Any() ? lines : new List<PriceListLine> { new() }; await BagAsync(); return View(m); }
        PriceList p;
        if (m.Id == 0) { p = new PriceList(); Db.PriceLists.Add(p); }
        else { p = await Db.PriceLists.Include(x => x.Lines).FirstAsync(x => x.Id == m.Id); Db.PriceListLines.RemoveRange(p.Lines); p.Lines.Clear(); }
        p.Code = m.Code.Trim().ToUpperInvariant(); p.Name = m.Name.Trim(); p.PartnerGroup = string.IsNullOrWhiteSpace(m.PartnerGroup) ? null : m.PartnerGroup; p.PartnerId = m.PartnerId;
        p.CurrencyCode = string.IsNullOrWhiteSpace(m.CurrencyCode) ? "VND" : m.CurrencyCode; p.ValidFrom = m.ValidFrom.Date; p.ValidTo = m.ValidTo?.Date; p.Priority = m.Priority; p.IsActive = m.IsActive;
        foreach (var l in lines) p.Lines.Add(new PriceListLine { ItemId = l.ItemId, MinQty = Math.Max(0, l.MinQty), Price = l.Price, DiscountPct = Math.Clamp(l.DiscountPct, 0, 100) });
        await Db.SaveChangesAsync();
        Success($"Đã lưu bảng giá {p.Code}.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePromotion(Promotion m)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(m.Name) || m.DiscountPct <= 0 || m.DiscountPct > 100) { Error("Nhập tên và mức chiết khấu 0–100%."); return RedirectToAction(nameof(Index)); }
        var p = m.Id == 0 ? new Promotion() : await Db.Promotions.FindAsync(m.Id);
        if (p == null) return NotFound();
        p.Name = m.Name.Trim(); p.ItemId = m.ItemId; p.PartnerGroup = string.IsNullOrWhiteSpace(m.PartnerGroup) ? null : m.PartnerGroup; p.MinQty = Math.Max(0, m.MinQty); p.DiscountPct = m.DiscountPct;
        p.ValidFrom = m.ValidFrom.Date; p.ValidTo = m.ValidTo?.Date; p.IsActive = m.IsActive;
        if (p.Id == 0) Db.Promotions.Add(p);
        await Db.SaveChangesAsync();
        Success($"Đã lưu khuyến mại {p.Name}.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePromotion(int id)
    {
        if (!CanEdit) return Denied();
        var p = await Db.Promotions.FindAsync(id); if (p == null) return NotFound();
        p.IsActive = !p.IsActive; await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Tra giá: GET /Pricing/Lookup?itemId=&amp;partnerId=&amp;qty=&amp;date=</summary>
    public async Task<IActionResult> Lookup(int itemId, int? partnerId, decimal qty = 1, DateTime? date = null)
    {
        var r = await _svc.GetAsync(itemId, partnerId, qty <= 0 ? 1 : qty, (date ?? DateTime.Today).Date);
        return Json(new { price = r.Price, discountPct = r.DiscountPct, source = r.Source });
    }
}
