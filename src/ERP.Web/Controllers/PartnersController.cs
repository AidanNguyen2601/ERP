using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Danh mục khách hàng / nhà cung cấp: mã tự sinh theo quy tắc, cảnh báo trùng mã số thuế / tên (cấp duyệt được xác nhận lưu)</summary>
public class PartnersController : ErpControllerBase
{
    private readonly AccountingService _acc; private readonly CodeService _codes; private readonly DuplicateService _dup;
    public PartnersController(ErpDbContext db, AccountingService acc, CodeService codes, DuplicateService dup) : base(db) { _acc = acc; _codes = codes; _dup = dup; }

    private bool CanEdit(bool customer) => User.InAny(AppRoles.Accounting) || (customer ? User.IsInRole(AppRoles.Sales) : User.IsInRole(AppRoles.Purchasing));

    public async Task<IActionResult> Index(string type = "customer", string? q = null, int page = 1)
    {
        var customer = type != "vendor";
        var pq = Db.Partners.Where(p => customer ? p.IsCustomer : p.IsVendor);
        if (!string.IsNullOrWhiteSpace(q)) pq = pq.Where(p => p.Code.Contains(q) || p.Name.Contains(q) || (p.TaxCode ?? "").Contains(q));
        var list = await pq.OrderBy(p => p.Code).PageAsync(this, page);
        ViewBag.Balances = await _acc.PartnerBalancesAsync(customer ? "131" : "331");
        ViewBag.Customer = customer; ViewBag.Q = q; ViewBag.CanEdit = CanEdit(customer);
        return View(list);
    }

    private async Task BagAsync() => ViewBag.Companies = new SelectList(await Db.Companies.OrderBy(c => c.Code).ToListAsync(), "Id", "Name");

    public async Task<IActionResult> Create(string type = "customer")
    {
        var customer = type != "vendor";
        if (!CanEdit(customer)) return Denied();
        await BagAsync();
        return View("Form", new Partner { IsCustomer = customer, IsVendor = !customer, PaymentTermDays = 30, Code = await _codes.NextAsync(customer ? "customer" : "vendor") ?? "" });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.Partners.FindAsync(id);
        if (p == null) return NotFound();
        if (!CanEdit(p.IsCustomer)) return Denied();
        await BagAsync();
        return View("Form", p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Partner model, bool confirmDuplicate = false)
    {
        if (!CanEdit(model.IsCustomer)) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        model.TaxCode = string.IsNullOrWhiteSpace(model.TaxCode) ? null : model.TaxCode.Trim();
        ModelState.Remove(nameof(model.Code)); ModelState.Remove(nameof(model.GroupCompany));
        var target = CodeService.Target(model);
        if (model.Code == "") model.Code = await _codes.NextAsync(target) ?? "";
        if (model.Code == "") ModelState.AddModelError(nameof(model.Code), "Nhập mã");
        else if (model.Id == 0 && await _codes.ValidateAsync(target, model.Code) is { } ruleErr) ModelState.AddModelError(nameof(model.Code), ruleErr);
        if (await Db.Partners.AnyAsync(p => p.Code == model.Code && p.Id != model.Id)) ModelState.AddModelError(nameof(model.Code), "Mã đã tồn tại");
        if (model.TaxCode != null && !System.Text.RegularExpressions.Regex.IsMatch(model.TaxCode, @"^\d{10}(-\d{3})?$|^\d{12}$"))
            ModelState.AddModelError(nameof(model.TaxCode), "Mã số thuế gồm 10 số, 13 số (10-3) hoặc 12 số (CCCD)");
        if (!model.IsCustomer && !model.IsVendor) ModelState.AddModelError("", "Chọn ít nhất: khách hàng hoặc nhà cung cấp.");
        if (ModelState.IsValid)
        {
            var warns = await _dup.PartnerAsync(model);
            if (warns.Any() && !(confirmDuplicate && DuplicateService.CanOverride(User)))
            {
                ViewBag.Duplicates = warns;
                ModelState.AddModelError("", "Phát hiện trùng dữ liệu – kiểm tra lại" + (DuplicateService.CanOverride(User) ? " hoặc xác nhận vẫn lưu." : "; cần Kế toán trưởng / Ban Giám đốc xác nhận."));
            }
        }
        if (!ModelState.IsValid) { await BagAsync(); return View("Form", model); }
        if (model.Id == 0) Db.Partners.Add(model);
        else
        {
            var p = await Db.Partners.FindAsync(model.Id);
            if (p == null) return NotFound();
            p.Code = model.Code; p.Name = model.Name; p.TaxCode = model.TaxCode; p.Address = model.Address; p.Phone = model.Phone; p.Email = model.Email;
            p.ContactPerson = model.ContactPerson; p.IsCustomer = model.IsCustomer; p.IsVendor = model.IsVendor; p.PaymentTermDays = model.PaymentTermDays; p.IsActive = model.IsActive;
            p.BankAccountNo = model.BankAccountNo; p.BankName = model.BankName;
            p.PartnerGroup = model.PartnerGroup; p.BusinessLicenseNo = model.BusinessLicenseNo; p.LegalRepresentative = model.LegalRepresentative; p.PaymentCondition = model.PaymentCondition;
            if (User.InAny(AppRoles.Accounting + "," + AppRoles.Director)) { p.Grade = model.Grade; p.IsBlocked = model.IsBlocked; p.BlockReason = model.IsBlocked ? model.BlockReason : null; }
            // Hạn mức công nợ và khai báo công ty trong tập đoàn chỉ Kế toán / Admin được đổi
            if (User.InAny(AppRoles.Accounting)) { p.CreditLimit = model.CreditLimit; p.GroupCompanyId = model.GroupCompanyId; }
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu {model.Code} – {model.Name}.");
        return RedirectToAction(nameof(Index), new { type = model.IsCustomer ? "customer" : "vendor" });
    }
}
