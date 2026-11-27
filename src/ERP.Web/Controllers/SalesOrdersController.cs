using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>
/// Bán hàng: lập đơn → xác nhận (kiểm tra hạn mức công nợ) → giao hàng (phiếu xuất kho, Nợ 632 / Có 15x)
/// → kế toán lập & phát hành hóa đơn (Nợ 131 / Có 511, 33311) → thu tiền.
/// </summary>
public class SalesOrdersController : ErpControllerBase
{
    private readonly SalesService _svc;
    private readonly InventoryService _inv;
    public SalesOrdersController(ErpDbContext db, SalesService svc, InventoryService inv) : base(db) { _svc = svc; _inv = inv; }

    private bool CanEdit => User.InAny(AppRoles.SalesTeam);
    private bool CanDeliver => User.InAny(AppRoles.SalesTeam + "," + AppRoles.Warehouse);
    private bool CanOverride => User.InAny(AppRoles.Approvers);

    private Task<SalesOrder?> LoadAsync(int id) => Db.SalesOrders.Include(p => p.Customer).Include(p => p.Warehouse).Include(p => p.CreatedBy).Include(p => p.CreditApprovedBy)
        .Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IActionResult> Index(SoStatus? status, string? q)
    {
        var query = Db.SalesOrders.Include(p => p.Customer).AsQueryable();
        if (status.HasValue) query = query.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => p.Number.Contains(q) || p.Customer!.Name.Contains(q));
        ViewBag.Status = status; ViewBag.Q = q; ViewBag.CanEdit = CanEdit;
        ViewBag.Counts = (await Db.SalesOrders.Select(p => p.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return View(await query.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var so = await LoadAsync(id);
        if (so == null) return NotFound();
        ViewBag.Deliveries = await Db.InventoryDocs.Include(d => d.Lines).Where(d => d.Origin == DocOrigin.Sales && d.OriginId == id).OrderBy(d => d.Date).ToListAsync();
        ViewBag.Invoices = await Db.Invoices.Where(i => i.SalesOrderId == id).OrderBy(i => i.Date).ToListAsync();
        ViewBag.Shipments = await Db.Shipments.Include(s => s.InventoryDoc).Where(s => s.SalesOrderId == id).OrderBy(s => s.Id).ToListAsync();
        ViewBag.Stock = await _inv.OnHandAsync(so.WarehouseId!.Value, so.Lines.Select(l => l.ItemId));
        ViewBag.CanEdit = CanEdit; ViewBag.CanDeliver = CanDeliver; ViewBag.CanOverride = CanOverride;
        if (so.Status == SoStatus.Draft && so.Customer!.CreditLimit > 0)
        {
            var (bal, open) = await _svc.ExposureAsync(so.Customer.Id, so.Id);
            ViewBag.Exposure = (bal, open, bal + open + so.Total, so.Customer.CreditLimit);
        }
        return View(so);
    }

    public async Task<IActionResult> Create(int? customerId)
    {
        if (!CanEdit) return Denied();
        var f = new OrderForm { PartnerId = customerId, DeliveryDate = DateTime.Today.AddDays(3), Lines = { new LineInput { Quantity = 1, VatRate = 10 } } };
        await FormDataAsync(f);
        return View("Form", f);
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!CanEdit) return Denied();
        var so = await LoadAsync(id);
        if (so == null) return NotFound();
        if (so.Status != SoStatus.Draft) { Error("Chỉ sửa đơn nháp."); return RedirectToAction(nameof(Details), new { id }); }
        var f = new OrderForm
        {
            Id = so.Id, Number = so.Number, Date = so.Date, PartnerId = so.CustomerId, WarehouseId = so.WarehouseId, DeliveryDate = so.DeliveryDate, Note = so.Note, ContractId = so.ContractId, SalespersonId = so.SalespersonId,
            Lines = so.Lines.Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPct = l.DiscountPct, VatRate = l.VatRate }).ToList()
        };
        await FormDataAsync(f);
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(OrderForm form, bool confirm)
    {
        if (!CanEdit) return Denied();
        var lines = form.Lines.Where(l => l.ItemId != null).ToList();
        await UomService.NormalizeAsync(Db, lines);
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng hàng.");
        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0 || l.DiscountPct < 0 || l.DiscountPct > 100)) ModelState.AddModelError("", "Số lượng > 0, đơn giá không âm, chiết khấu 0–100%.");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) ModelState.AddModelError("", "Mỗi mặt hàng chỉ một dòng.");
        if (!ModelState.IsValid) { await FormDataAsync(form); return View("Form", form); }

        SalesOrder so;
        if (form.Id == 0)
        {
            so = new SalesOrder { CreatedById = Me, Number = await DocNumbers.NextAsync(Db.SalesOrders.Select(x => x.Number), "DBH", form.Date) };
            Db.SalesOrders.Add(so);
        }
        else
        {
            so = (await LoadAsync(form.Id))!;
            if (so.Status != SoStatus.Draft) throw new BusinessException("Đơn đã xác nhận, không sửa được.");
            Db.SalesOrderLines.RemoveRange(so.Lines.ToList()); so.Lines.Clear();
        }
        so.Date = form.Date.Date; so.CustomerId = form.PartnerId; so.WarehouseId = form.WarehouseId; so.DeliveryDate = form.DeliveryDate.Date; so.Note = form.Note; so.ContractId = form.ContractId; so.SalespersonId = form.SalespersonId ?? so.SalespersonId ?? Me;
        foreach (var l in lines) so.Lines.Add(new SalesOrderLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPct = l.DiscountPct, VatRate = l.VatRate });
        SalesService.Recalc(so);
        await Db.SaveChangesAsync();
        Success($"Đã lưu đơn bán {so.Number}.");
        if (confirm) return await Confirm(so.Id);
        return RedirectToAction(nameof(Details), new { id = so.Id });
    }

    /// <summary>Áp dụng bảng giá, chiết khấu và khuyến mại cho các dòng của đơn nháp</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyPrices(int id, [FromServices] PricingService pricing)
    {
        if (!CanEdit) return Denied();
        var so = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        var n = await pricing.ApplyAsync(so);
        Success(n == 0 ? "Giá và chiết khấu trên đơn đã đúng theo bảng giá / khuyến mại." : $"Đã cập nhật giá, chiết khấu của {n} dòng theo bảng giá và khuyến mại.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id)
    {
        if (!CanEdit && !CanOverride) return Denied();
        var so = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        try
        {
            var warn = await _svc.ConfirmAsync(so, CanOverride);
            if (warn != null) TempData["Warning"] = warn;
            Success($"Đã xác nhận đơn {so.Number}, chuyển kho chuẩn bị giao hàng.");
        }
        catch (PendingApprovalException ex) { TempData["Warning"] = ex.Message; }
        catch (BusinessException ex) { Error(ex.Message); }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Deliver(int id)
    {
        if (!CanDeliver) return Denied();
        var so = await LoadAsync(id);
        if (so == null) return NotFound();
        if (so.Status is not (SoStatus.Confirmed or SoStatus.PartiallyDelivered)) { Error("Đơn chưa xác nhận hoặc đã giao đủ."); return RedirectToAction(nameof(Details), new { id }); }
        ViewBag.Stock = await _inv.OnHandAsync(so.WarehouseId!.Value, so.Lines.Select(l => l.ItemId));
        return View(so);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deliver(int id, DateTime date, Dictionary<int, decimal> qty)
    {
        if (!CanDeliver) return Denied();
        var so = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        var doc = await _svc.DeliverAsync(so, date, qty);
        Success(doc != null ? $"Đã xuất kho phiếu {doc.Number}, giá vốn {doc.TotalAmount.ToMoney()} đ." : "Đã ghi nhận hoàn thành dịch vụ.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInvoice(int id, DateTime? date, bool issue = false)
    {
        if (!User.CanAccounting()) return Denied();
        var so = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        var inv = await _svc.CreateInvoiceAsync(so, date ?? DateTime.Today);
        if (issue)
        {
            await HttpContext.RequestServices.GetRequiredService<InvoiceService>().IssueAsync(inv);
            Success($"Đã phát hành hóa đơn số {inv.InvoiceNo} – {inv.Total.ToMoney()} đ.");
        }
        else Success($"Đã lập hóa đơn nháp {inv.Number}.");
        return RedirectToAction("Details", "Invoices", new { id = inv.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanEdit) return Denied();
        var so = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy đơn.");
        if (so.Lines.Any(l => l.DeliveredQty > 0)) throw new BusinessException("Đơn đã giao hàng, không hủy được.");
        so.Status = SoStatus.Cancelled;
        await Db.SaveChangesAsync();
        Success($"Đã hủy đơn {so.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task FormDataAsync(OrderForm f)
    {
        ViewBag.Partners = await CustomersAsync(f.PartnerId);
        ViewBag.Whs = await WarehousesAsync(f.WarehouseId);
        ViewBag.Contracts = await ContractsAsync(ContractType.Sale, f.ContractId);
        ViewBag.Salespeople = new SelectList(await Db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Sales || u.Id == f.SalespersonId)).OrderBy(u => u.FullName).Select(u => new { u.Id, Name = u.FullName }).ToListAsync(), "Id", "Name", f.SalespersonId ?? Me);
        ViewBag.LineOptions = new LineEditorOptions { ShowUnit = true, Uoms = await UomService.LoadAsync(Db), Items = await ItemsAsync(i => i.Type != ItemType.Material), PriceSource = "sale", ShowDiscount = true, Lines = f.Lines, PriceLabel = "Đơn giá bán" };
        var stock = (await Db.StockLevels.ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        ViewBag.StockHint = stock;
    }
}
