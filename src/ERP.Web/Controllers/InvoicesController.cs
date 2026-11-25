using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Quản lý hóa đơn bán ra / mua vào, phát hành, hủy, bảng kê thuế GTGT</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class InvoicesController : ErpControllerBase
{
    private readonly InvoiceService _svc;
    private readonly ErpSettings _cfg;
    public InvoicesController(ErpDbContext db, InvoiceService svc, IOptions<ErpSettings> cfg) : base(db) { _svc = svc; _cfg = cfg.Value; }

    public async Task<IActionResult> Index(InvoiceDirection direction = InvoiceDirection.Sales, InvoiceStatus? status = null, string? q = null, DateTime? from = null, DateTime? to = null, bool unpaid = false)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-3)).Date;
        var query = Db.Invoices.Include(i => i.Partner).Where(i => i.Direction == direction && i.Date >= f && i.Date <= t);
        if (status.HasValue) query = query.Where(i => i.Status == status);
        if (unpaid) query = query.Where(i => i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(i => i.Number.Contains(q) || (i.InvoiceNo != null && i.InvoiceNo.Contains(q)) || i.Partner!.Name.Contains(q));
        ViewBag.Direction = direction; ViewBag.Status = status; ViewBag.Q = q; ViewBag.From = f; ViewBag.To = t; ViewBag.Unpaid = unpaid;
        return View(await query.OrderByDescending(i => i.Date).ThenByDescending(i => i.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var i = await Db.Invoices.Include(x => x.Partner).Include(x => x.Lines).ThenInclude(l => l.Item).Include(x => x.Lines).ThenInclude(l => l.Account)
            .Include(x => x.SalesOrder).Include(x => x.PurchaseOrder).Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id);
        if (i == null) return NotFound();
        ViewBag.Payments = await Db.PaymentAllocations.Include(a => a.CashVoucher).Where(a => a.InvoiceId == id).ToListAsync();
        ViewBag.CreditNotes = await Db.SalesCreditNotes.Where(n => n.InvoiceId == id).OrderBy(n => n.Date).ThenBy(n => n.Id).ToListAsync();
        ViewBag.Company = _cfg;
        return View(i);
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create(InvoiceDirection direction = InvoiceDirection.Sales)
    {
        var f = new InvoiceForm { Direction = direction, Series = direction == InvoiceDirection.Sales ? _cfg.InvoiceSeries : null, Lines = { new LineInput { Quantity = 1, VatRate = 10 } } };
        await LoadAsync(f, true);
        return View("Form", f);
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Edit(int id)
    {
        var i = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (i == null) return NotFound();
        if (i.Status != InvoiceStatus.Draft) { Error("Chỉ sửa hóa đơn nháp."); return RedirectToAction(nameof(Details), new { id }); }
        var f = new InvoiceForm
        {
            Id = i.Id, Direction = i.Direction, Date = i.Date, DueDate = i.DueDate, PartnerId = i.PartnerId, Series = i.Series, InvoiceNo = i.InvoiceNo, Note = i.Note,
            SalesOrderId = i.SalesOrderId, PurchaseOrderId = i.PurchaseOrderId, Number = i.Number,
            Lines = i.Lines.OrderBy(l => l.Id).Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate, AccountId = l.AccountId }).ToList()
        };
        await LoadAsync(f, i.SalesOrderId == null && i.PurchaseOrderId == null);
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(InvoiceForm form, bool issue)
    {
        Invoice inv;
        var fromOrder = false;
        if (form.Id == 0)
        {
            inv = new Invoice { Direction = form.Direction, CreatedById = Me, Number = await _svc.NextNumberAsync(form.Direction, form.Date) };
            Db.Invoices.Add(inv);
        }
        else
        {
            inv = await Db.Invoices.Include(x => x.Lines).FirstAsync(x => x.Id == form.Id);
            if (inv.Status != InvoiceStatus.Draft) throw new BusinessException("Hóa đơn đã phát hành.");
            fromOrder = inv.SalesOrderId != null || inv.PurchaseOrderId != null;
        }
        var lines = form.Lines.Where(l => l.ItemId != null || !string.IsNullOrWhiteSpace(l.Description)).ToList();
        if (!fromOrder && !lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một dòng hàng hóa / dịch vụ.");
        if (form.DueDate < form.Date) ModelState.AddModelError(nameof(form.DueDate), "Hạn thanh toán không trước ngày hóa đơn.");
        if (!ModelState.IsValid) { if (form.Id == 0) Db.Invoices.Remove(inv); await LoadAsync(form, !fromOrder); return View("Form", form); }

        inv.Date = form.Date.Date; inv.DueDate = form.DueDate.Date; inv.Series = form.Series?.Trim(); inv.InvoiceNo = form.InvoiceNo?.Trim(); inv.Note = form.Note;
        if (!fromOrder)
        {
            inv.PartnerId = form.PartnerId;
            Db.InvoiceLines.RemoveRange(inv.Lines.ToList()); inv.Lines.Clear();
            var items = (await Db.Items.ToListAsync()).ToDictionary(i => i.Id);
            foreach (var l in lines)
            {
                var it = l.ItemId.HasValue ? items[l.ItemId.Value] : null;
                inv.Lines.Add(new InvoiceLine
                {
                    ItemId = l.ItemId, Description = string.IsNullOrWhiteSpace(l.Description) ? it?.Name ?? "" : l.Description.Trim(), Unit = it?.Unit,
                    Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate, AccountId = l.AccountId
                });
            }
        }
        InvoiceService.Recalc(inv);
        await Db.SaveChangesAsync();
        if (issue)
        {
            try { await _svc.IssueAsync(inv); }
            catch (BusinessException ex)
            {
                ModelState.AddModelError("", ex.Message + " Hóa đơn đã được lưu nháp.");
                form.Id = inv.Id; form.Number = inv.Number; ModelState.Remove(nameof(form.Id));
                await LoadAsync(form, !fromOrder);
                return View("Form", form);
            }
            Success(inv.Direction == InvoiceDirection.Sales ? $"Đã phát hành hóa đơn {inv.Series} số {inv.InvoiceNo}." : $"Đã ghi sổ hóa đơn mua {inv.Series} {inv.InvoiceNo}.");
        }
        else Success($"Đã lưu nháp {inv.Number}.");
        return RedirectToAction(nameof(Details), new { id = inv.Id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Issue(int id)
    {
        var inv = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        await _svc.IssueAsync(inv);
        Success(inv.Direction == InvoiceDirection.Sales ? $"Đã phát hành hóa đơn số {inv.InvoiceNo} và ghi nhận doanh thu, công nợ." : $"Đã ghi sổ hóa đơn mua {inv.InvoiceNo}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var inv = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do hủy hóa đơn.");
        await _svc.CancelAsync(inv, reason);
        Success($"Đã hủy hóa đơn {inv.InvoiceNo ?? inv.Number}" + (inv.JournalEntryId.HasValue ? " và đảo bút toán." : "."));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Delete(int id)
    {
        var inv = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        var dir = inv.Direction;
        await _svc.DeleteDraftAsync(inv);
        Success("Đã xóa hóa đơn nháp.");
        return RedirectToAction(nameof(Index), new { direction = dir });
    }

    /// <summary>Bảng kê hóa đơn hàng hóa, dịch vụ bán ra / mua vào và số thuế GTGT phải nộp trong tháng</summary>
    public async Task<IActionResult> VatReport(int? year, int? month)
    {
        var y = year ?? DateTime.Today.Year; var m = month ?? DateTime.Today.Month;
        var f = new DateTime(y, m, 1); var t = f.AddMonths(1).AddDays(-1);
        var list = await Db.Invoices.Include(i => i.Partner).Include(i => i.Lines)
            .Where(i => i.Status == InvoiceStatus.Issued && i.Date >= f && i.Date <= t && i.Series != "DK" && i.Series != AssetService.AssetInvoiceSeries).OrderBy(i => i.Date).ThenBy(i => i.InvoiceNo).ToListAsync();
        ViewBag.Year = y; ViewBag.Month = m;
        ViewBag.CreditNotes = await Db.SalesCreditNotes.Include(n => n.Customer).Include(n => n.Invoice).Where(n => n.Status == DocStatus.Posted && n.Date >= f && n.Date <= t && n.VatAmount > 0).OrderBy(n => n.Date).ToListAsync();
        ViewBag.Returns = await Db.PurchaseReturns.Include(r => r.Ncr).Include(r => r.PurchaseOrderLine).ThenInclude(l => l!.Item).Include(r => r.PurchaseOrderLine).ThenInclude(l => l!.PurchaseOrder)
            .Where(r => r.Date >= f && r.Date <= t && r.VatAmount > 0).OrderBy(r => r.Date).ToListAsync();
        return View(list);
    }

    private async Task LoadAsync(InvoiceForm f, bool editableLines)
    {
        ViewBag.Partners = f.Direction == InvoiceDirection.Sales ? await CustomersAsync(f.PartnerId) : await VendorsAsync(f.PartnerId);
        ViewBag.EditableLines = editableLines;
        ViewBag.LineOptions = new LineEditorOptions
        {
            Items = await ItemsAsync(i => f.Direction == InvoiceDirection.Sales ? i.Type == ItemType.Service : true),
            Accounts = (await PostableAccountsAsync()).Where(a => f.Direction == InvoiceDirection.Sales ? a.Code.StartsWith("5") || a.Code.StartsWith("7") : a.Code[0] is '6' or '8' or '2' or '1').ToList(),
            AllowFreeText = true, ShowAccount = true, ShowVat = true, PriceSource = f.Direction == InvoiceDirection.Sales ? "sale" : "cost",
            Lines = f.Lines
        };
        if (!editableLines && f.Id != 0)
            ViewBag.FixedLines = await Db.InvoiceLines.Include(l => l.Item).Where(l => l.InvoiceId == f.Id).ToListAsync();
    }
}
