using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Hợp đồng mua / bán: theo dõi giá trị, đơn hàng, hóa đơn, công nợ và tuổi nợ theo từng hợp đồng</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Sales + "," + AppRoles.Purchasing + "," + AppRoles.FinanceView)]
public class ContractsController : ErpControllerBase
{
    private readonly CodeService _codes;
    public ContractsController(ErpDbContext db, CodeService codes) : base(db) => _codes = codes;

    public record Row(Contract Contract, decimal Ordered, decimal Invoiced, decimal Paid, decimal Outstanding, decimal Overdue);

    public async Task<IActionResult> Index(ContractType? type, ContractStatus? status, string? q)
    {
        var list = Db.Contracts.Include(c => c.Partner).AsQueryable();
        if (type != null) list = list.Where(c => c.Type == type);
        if (status != null) list = list.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(c => c.Number.Contains(q) || c.Title.Contains(q) || c.Partner!.Name.Contains(q));
        var contracts = await list.OrderByDescending(c => c.SignDate).ToListAsync();
        var ids = contracts.Select(c => c.Id).ToList();
        var invs = await Db.Invoices.Where(i => i.ContractId != null && ids.Contains(i.ContractId.Value) && i.Status == InvoiceStatus.Issued).ToListAsync();
        var so = await Db.SalesOrders.Where(o => o.ContractId != null && ids.Contains(o.ContractId.Value) && o.Status != SoStatus.Cancelled).ToListAsync();
        var po = await Db.PurchaseOrders.Where(o => o.ContractId != null && ids.Contains(o.ContractId.Value) && o.Status != PoStatus.Cancelled).ToListAsync();
        var rows = contracts.Select(c =>
        {
            var ci = invs.Where(i => i.ContractId == c.Id).ToList();
            var ordered = c.Type == ContractType.Sale ? so.Where(o => o.ContractId == c.Id).Sum(o => o.Total) : po.Where(o => o.ContractId == c.Id).Sum(o => o.Total);
            return new Row(c, ordered, ci.Sum(i => i.Total), ci.Sum(i => i.PaidAmount), ci.Sum(i => i.Outstanding), ci.Where(i => i.DueDate < DateTime.Today).Sum(i => i.Outstanding));
        }).ToList();
        ViewBag.Type = type; ViewBag.Status = status; ViewBag.Q = q;
        return View(rows);
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await Db.Contracts.Include(x => x.Partner).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewBag.Invoices = await Db.Invoices.Where(i => i.ContractId == id && i.Status != InvoiceStatus.Draft).OrderBy(i => i.Date).ToListAsync();
        ViewBag.SalesOrders = c.Type == ContractType.Sale ? await Db.SalesOrders.Where(o => o.ContractId == id).OrderBy(o => o.Date).ToListAsync() : new List<SalesOrder>();
        ViewBag.PurchaseOrders = c.Type == ContractType.Purchase ? await Db.PurchaseOrders.Where(o => o.ContractId == id).OrderBy(o => o.Date).ToListAsync() : new List<PurchaseOrder>();
        var invIds = ((List<Invoice>)ViewBag.Invoices).Select(i => i.Id).ToList();
        ViewBag.Payments = await Db.PaymentAllocations.Include(a => a.CashVoucher).Include(a => a.Invoice).Where(a => invIds.Contains(a.InvoiceId) && a.CashVoucher!.Status == DocStatus.Posted).OrderBy(a => a.CashVoucher!.Date).ToListAsync();
        return View(c);
    }

    private bool CanEdit => User.InAny(AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Sales + "," + AppRoles.Purchasing);

    public async Task<IActionResult> Edit(int? id, ContractType type = ContractType.Sale)
    {
        if (!CanEdit) return Denied();
        var c = id == null ? new Contract { Type = type, Number = await _codes.NextAsync("contract." + type) ?? "" } : await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        ViewBag.Partners = c.Type == ContractType.Sale ? await CustomersAsync(c.PartnerId) : await VendorsAsync(c.PartnerId);
        return View(c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Contract m)
    {
        if (!CanEdit) return Denied();
        m.Number = (m.Number ?? "").Trim().ToUpperInvariant();
        if (m.Number == "") m.Number = await _codes.NextAsync("contract." + m.Type) ?? "";
        ModelState.Remove(nameof(m.Number)); ModelState.Remove(nameof(m.Partner));
        if (m.Number == "") ModelState.AddModelError(nameof(m.Number), "Nhập số hợp đồng");
        if (await Db.Contracts.AnyAsync(x => x.Number == m.Number && x.Id != m.Id)) ModelState.AddModelError(nameof(m.Number), "Số hợp đồng đã tồn tại");
        if (m.PartnerId == null) ModelState.AddModelError(nameof(m.PartnerId), "Chọn đối tác");
        if (m.EndDate < m.SignDate) ModelState.AddModelError(nameof(m.EndDate), "Ngày kết thúc phải sau ngày ký");
        if (!ModelState.IsValid)
        {
            ViewBag.Partners = m.Type == ContractType.Sale ? await CustomersAsync(m.PartnerId) : await VendorsAsync(m.PartnerId);
            return View(m);
        }
        if (m.Id == 0) Db.Contracts.Add(m);
        else
        {
            var c = await Db.Contracts.FindAsync(m.Id) ?? throw new BusinessException("Không tìm thấy hợp đồng.");
            c.Number = m.Number; c.PartnerId = m.PartnerId; c.Title = m.Title; c.SignDate = m.SignDate; c.EndDate = m.EndDate; c.Value = m.Value;
            c.PaymentTermDays = m.PaymentTermDays; c.PaymentTerms = m.PaymentTerms; c.Status = m.Status; c.Note = m.Note;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu hợp đồng {m.Number}.");
        return RedirectToAction(nameof(Index), new { type = m.Type });
    }
}
