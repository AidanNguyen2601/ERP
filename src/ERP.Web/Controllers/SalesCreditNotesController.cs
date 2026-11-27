using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Hóa đơn điều chỉnh giảm bán hàng: hàng bán bị trả lại, giảm giá hàng bán</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class SalesCreditNotesController : ErpControllerBase
{
    private readonly SalesCreditService _svc;
    public SalesCreditNotesController(ErpDbContext db, SalesCreditService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index(CreditNoteKind? kind, DateTime? from, DateTime? to, string? q)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-6)).Date;
        var query = Db.SalesCreditNotes.Include(n => n.Customer).Include(n => n.Invoice).Where(n => n.Date >= f && n.Date <= t);
        if (kind.HasValue) query = query.Where(n => n.Kind == kind);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(n => n.Number.Contains(q) || n.Customer!.Name.Contains(q) || (n.Invoice!.InvoiceNo != null && n.Invoice.InvoiceNo.Contains(q)));
        ViewBag.Kind = kind; ViewBag.From = f; ViewBag.To = t; ViewBag.Q = q;
        return View(await query.OrderByDescending(n => n.Date).ThenByDescending(n => n.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var n = await Db.SalesCreditNotes.Include(x => x.Customer).Include(x => x.Invoice).Include(x => x.Warehouse).Include(x => x.InventoryDoc).Include(x => x.CreatedBy)
            .Include(x => x.Lines).ThenInclude(l => l.Item).Include(x => x.Offsets).ThenInclude(o => o.Invoice).FirstOrDefaultAsync(x => x.Id == id);
        if (n == null) return NotFound();
        ViewBag.CanEdit = User.CanAccounting();
        return View(n);
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create(int invoiceId, CreditNoteKind kind = CreditNoteKind.Return)
    {
        var inv = await LoadInvoiceAsync(invoiceId);
        if (inv == null) return NotFound();
        if (inv.Direction != InvoiceDirection.Sales || inv.Status != InvoiceStatus.Issued || inv.Series == "DK")
        { Error("Chỉ lập điều chỉnh giảm cho hóa đơn bán đã phát hành."); return RedirectToAction("Details", "Invoices", new { id = invoiceId }); }
        await LoadAsync(inv);
        return View("Form", new CreditNoteForm { InvoiceId = inv.Id, Kind = kind, WarehouseId = inv.SalesOrder?.WarehouseId });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(CreditNoteForm form)
    {
        var inv = await LoadInvoiceAsync(form.InvoiceId);
        if (inv == null) return NotFound();
        try
        {
            var note = await _svc.CreateAsync(form);
            Success($"Đã lập {note.Kind.GetDisplayName().ToLowerInvariant()} {note.Number}: giảm doanh thu {note.Subtotal.ToMoney()}, thuế {note.VatAmount.ToMoney()}, giảm phải thu {note.Total.ToMoney()} đ.");
            return RedirectToAction(nameof(Details), new { id = note.Id });
        }
        catch (BusinessException ex)
        {
            // Giữ lại dữ liệu đã nhập để sửa
            ModelState.AddModelError("", ex.Message);
            await LoadAsync(inv);
            return View("Form", form);
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var n = await Db.SalesCreditNotes.Include(x => x.Offsets).FirstOrDefaultAsync(x => x.Id == id);
        if (n == null) return NotFound();
        await _svc.CancelAsync(n, reason ?? "");
        Success($"Đã hủy {n.Number}: bút toán, tồn kho và công nợ hóa đơn gốc đã được đảo lại.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private Task<Invoice?> LoadInvoiceAsync(int id) => Db.Invoices.Include(i => i.Partner).Include(i => i.SalesOrder).Include(i => i.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(i => i.Id == id);

    private async Task LoadAsync(Invoice inv)
    {
        ViewBag.Invoice = inv;
        ViewBag.Credited = await _svc.CreditedAsync(inv.Id);
        ViewBag.Whs = await WarehousesAsync(inv.SalesOrder?.WarehouseId);
    }
}
