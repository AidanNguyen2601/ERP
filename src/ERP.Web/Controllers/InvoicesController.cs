using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
    private readonly FxService _fx;
    public InvoicesController(ErpDbContext db, InvoiceService svc, IOptions<ErpSettings> cfg, FxService fx) : base(db) { _svc = svc; _cfg = cfg.Value; _fx = fx; }

    public async Task<IActionResult> Index(InvoiceDirection direction = InvoiceDirection.Sales, InvoiceStatus? status = null, string? q = null, DateTime? from = null, DateTime? to = null, bool unpaid = false, int page = 1)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-3)).Date;
        var query = Db.Invoices.Include(i => i.Partner).Where(i => i.Direction == direction && i.Date >= f && i.Date <= t);
        if (status.HasValue) query = query.Where(i => i.Status == status);
        if (unpaid) query = query.Where(i => i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(i => i.Number.Contains(q) || (i.InvoiceNo != null && i.InvoiceNo.Contains(q)) || i.Partner!.Name.Contains(q));
        ViewBag.Direction = direction; ViewBag.Status = status; ViewBag.Q = q; ViewBag.From = f; ViewBag.To = t; ViewBag.Unpaid = unpaid;
        var valid = query.Where(i => i.Status != InvoiceStatus.Cancelled);
        ViewBag.Sum = await valid.GroupBy(_ => 1).Select(g => new decimal[] { g.Sum(i => i.Subtotal), g.Sum(i => i.VatAmount), g.Sum(i => i.Total), g.Sum(i => i.Status == InvoiceStatus.Issued ? i.Total - i.PaidAmount : 0) }).FirstOrDefaultAsync() ?? new decimal[4];
        return View(await query.OrderByDescending(i => i.Date).ThenByDescending(i => i.Id).PageAsync(this, page));
    }

    /// <summary>Tải XML hóa đơn điện tử đã gửi nhà cung cấp / cơ quan thuế</summary>
    public async Task<IActionResult> EInvoiceXml(int id)
    {
        var i = await Db.Invoices.FirstOrDefaultAsync(x => x.Id == id);
        if (i?.EInvoiceXml == null) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(i.EInvoiceXml), "application/xml", $"HDDT_{i.Series}_{i.InvoiceNo}.xml");
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
            SalesOrderId = i.SalesOrderId, PurchaseOrderId = i.PurchaseOrderId, Number = i.Number, CurrencyCode = i.CurrencyCode, ExchangeRate = i.ExchangeRate, ContractId = i.ContractId,
            Lines = i.Lines.OrderBy(l => l.Id).Select(l => new LineInput { Id = l.Id, ItemId = l.ItemId, Description = l.Description, Quantity = l.Quantity, UnitPrice = i.CurrencyCode == "VND" ? l.UnitPrice : l.FcUnitPrice, VatRate = l.VatRate, AccountId = l.AccountId }).ToList()
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
        inv.ContractId = form.ContractId ?? inv.ContractId;
        if (!fromOrder)
        {
            inv.CurrencyCode = string.IsNullOrWhiteSpace(form.CurrencyCode) ? "VND" : form.CurrencyCode;
            inv.ExchangeRate = inv.CurrencyCode == "VND" ? 1 : form.ExchangeRate > 0 ? form.ExchangeRate : await _fx.RateAsync(inv.CurrencyCode, form.Date);
            if (inv.ExchangeRate <= 0) throw new BusinessException($"Chưa có tỷ giá {inv.CurrencyCode} – nhập tỷ giá trên hóa đơn.");
        }
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
                    Quantity = l.Quantity, UnitPrice = l.UnitPrice, FcUnitPrice = inv.CurrencyCode == "VND" ? 0 : l.UnitPrice, VatRate = l.VatRate, AccountId = l.AccountId
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

    /// <summary>Lập hóa đơn thay thế (nháp) từ hóa đơn đã phát hành; khi phát hành hóa đơn thay thế, hóa đơn gốc tự hủy và đảo bút toán</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Replace(int id)
    {
        var o = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (o == null) return NotFound();
        InvoiceService.ValidateReplaceable(o);
        var exists = await Db.Invoices.FirstOrDefaultAsync(x => x.ReplacesInvoiceId == id && x.Status == InvoiceStatus.Draft);
        if (exists != null) { Success($"Đã có hóa đơn thay thế nháp {exists.Number}."); return RedirectToAction(nameof(Details), new { id = exists.Id }); }
        var copy = (Invoice)Db.Entry(o).CurrentValues.ToObject();
        copy.Id = 0; copy.Number = await _svc.NextNumberAsync(o.Direction, DateTime.Today); copy.InvoiceNo = null; copy.Status = InvoiceStatus.Draft; copy.JournalEntryId = null; copy.PaidAmount = 0;
        copy.Date = DateTime.Today; copy.CreatedById = Me; copy.CreatedAt = DateTime.Now; copy.ReplacesInvoiceId = o.Id; copy.ErrorNoticeXml = null;
        copy.EInvoiceStatus = EInvoiceStatus.None; copy.EInvoiceLookupCode = null; copy.EInvoiceTaxCode = null; copy.EInvoiceMessage = null; copy.EInvoiceXml = null; copy.EInvoiceSentAt = null;
        copy.Note = $"Thay thế hóa đơn {o.Series} số {o.InvoiceNo} ngày {o.Date:dd/MM/yyyy}";
        copy.Lines = o.Lines.Select(l => { var n = (InvoiceLine)Db.Entry(l).CurrentValues.ToObject(); n.Id = 0; n.InvoiceId = 0; return n; }).ToList();
        // như khi lập hóa đơn từ đơn hàng: dòng nháp đã tính vào số lượng đã lập hóa đơn (hóa đơn gốc trả lại khi bị hủy lúc phát hành bản thay thế)
        var srcIds = copy.Lines.Where(l => l.SourceLineId != null).Select(l => l.SourceLineId!.Value).ToList();
        if (o.SalesOrderId != null)
            foreach (var sl in await Db.SalesOrderLines.Where(x => srcIds.Contains(x.Id)).ToListAsync())
                sl.InvoicedQty += copy.Lines.Where(l => l.SourceLineId == sl.Id).Sum(l => l.Quantity);
        Db.Invoices.Add(copy);
        await Db.SaveChangesAsync();
        Success($"Đã lập hóa đơn thay thế nháp {copy.Number}. Sửa nội dung sai rồi bấm Phát hành – hóa đơn {o.InvoiceNo} sẽ tự hủy và đảo bút toán.");
        return RedirectToAction(nameof(Details), new { id = copy.Id });
    }

    public async Task<IActionResult> ErrorNoticeXml(int id)
    {
        var i = await Db.Invoices.FirstOrDefaultAsync(x => x.Id == id);
        if (i?.ErrorNoticeXml == null) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(i.ErrorNoticeXml), "application/xml", $"04SS_HDDT_{i.Series}_{i.InvoiceNo}.xml");
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
        ViewBag.Currencies = new SelectList(await Db.Currencies.Where(c => c.IsActive || c.Code == f.CurrencyCode).OrderBy(c => c.Code == "VND" ? 0 : 1).ThenBy(c => c.Code).Select(c => new { c.Code, Name = c.Code + " – " + c.Name }).ToListAsync(), "Code", "Name", f.CurrencyCode);
        var ctype = f.Direction == InvoiceDirection.Sales ? ContractType.Sale : ContractType.Purchase;
        ViewBag.Contracts = new SelectList(await Db.Contracts.Include(c => c.Partner).Where(c => c.Type == ctype && (c.Status == ContractStatus.Active || c.Id == f.ContractId)).OrderByDescending(c => c.SignDate)
            .Select(c => new { c.Id, Name = c.Number + " – " + (c.Partner != null ? c.Partner.Name : "") + " – " + c.Title }).ToListAsync(), "Id", "Name", f.ContractId);
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
