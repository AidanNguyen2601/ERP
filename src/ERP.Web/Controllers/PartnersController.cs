using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Danh mục khách hàng / nhà cung cấp</summary>
public class PartnersController : ErpControllerBase
{
    private readonly AccountingService _acc;
    public PartnersController(ErpDbContext db, AccountingService acc) : base(db) => _acc = acc;

    private bool CanEdit(bool customer) => User.InAny(AppRoles.Accounting) || (customer ? User.IsInRole(AppRoles.Sales) : User.IsInRole(AppRoles.Purchasing));

    public async Task<IActionResult> Index(string type = "customer", string? q = null)
    {
        var customer = type != "vendor";
        var list = await Db.Partners.Where(p => customer ? p.IsCustomer : p.IsVendor).OrderBy(p => p.Code).ToListAsync();
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(p => p.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (p.TaxCode ?? "").Contains(q)).ToList();
        ViewBag.Balances = await _acc.PartnerBalancesAsync(customer ? "131" : "331");
        ViewBag.Customer = customer; ViewBag.Q = q; ViewBag.CanEdit = CanEdit(customer);
        return View(list);
    }

    public IActionResult Create(string type = "customer")
    {
        var customer = type != "vendor";
        if (!CanEdit(customer)) return Denied();
        return View("Form", new Partner { IsCustomer = customer, IsVendor = !customer, PaymentTermDays = customer ? 30 : 30 });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.Partners.FindAsync(id);
        if (p == null) return NotFound();
        if (!CanEdit(p.IsCustomer)) return Denied();
        return View("Form", p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Partner model)
    {
        if (!CanEdit(model.IsCustomer)) return Denied();
        model.Code = model.Code.Trim().ToUpperInvariant();
        if (await Db.Partners.AnyAsync(p => p.Code == model.Code && p.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã đã tồn tại");
        if (!string.IsNullOrWhiteSpace(model.TaxCode) && await Db.Partners.AnyAsync(p => p.TaxCode == model.TaxCode && p.Id != model.Id))
            ModelState.AddModelError(nameof(model.TaxCode), "Mã số thuế đã có ở đối tượng khác");
        if (!model.IsCustomer && !model.IsVendor) ModelState.AddModelError("", "Chọn ít nhất: khách hàng hoặc nhà cung cấp.");
        if (!ModelState.IsValid) return View("Form", model);
        if (model.Id == 0) Db.Partners.Add(model);
        else
        {
            var p = await Db.Partners.FindAsync(model.Id);
            if (p == null) return NotFound();
            p.Code = model.Code; p.Name = model.Name; p.TaxCode = model.TaxCode; p.Address = model.Address; p.Phone = model.Phone; p.Email = model.Email;
            p.ContactPerson = model.ContactPerson; p.IsCustomer = model.IsCustomer; p.IsVendor = model.IsVendor; p.PaymentTermDays = model.PaymentTermDays; p.IsActive = model.IsActive;
            // Hạn mức công nợ chỉ Kế toán / Admin được đổi
            if (User.InAny(AppRoles.Accounting)) p.CreditLimit = model.CreditLimit;
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu {model.Code} – {model.Name}.");
        return RedirectToAction(nameof(Index), new { type = model.IsCustomer ? "customer" : "vendor" });
    }
}
