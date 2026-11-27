using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

public abstract class ErpControllerBase : Controller
{
    protected readonly ErpDbContext Db;
    protected ErpControllerBase(ErpDbContext db) => Db = db;

    protected int Me => User.GetUserId();
    protected void Success(string msg) => TempData["Success"] = msg;
    protected void Error(string msg) => TempData["Error"] = msg;
    protected IActionResult Denied() => RedirectToAction("AccessDenied", "Account");

    protected async Task<SelectList> CustomersAsync(int? selected = null) =>
        new(await Db.Partners.Where(p => p.IsCustomer && (p.IsActive || p.Id == selected)).OrderBy(p => p.Code)
            .Select(p => new { p.Id, Name = p.Code + " – " + p.Name }).ToListAsync(), "Id", "Name", selected);
    protected async Task<SelectList> VendorsAsync(int? selected = null) =>
        new(await Db.Partners.Where(p => p.IsVendor && (p.IsActive || p.Id == selected)).OrderBy(p => p.Code)
            .Select(p => new { p.Id, Name = p.Code + " – " + p.Name }).ToListAsync(), "Id", "Name", selected);
    protected async Task<SelectList> PartnersAsync(int? selected = null) =>
        new(await Db.Partners.Where(p => p.IsActive || p.Id == selected).OrderBy(p => p.Code)
            .Select(p => new { p.Id, Name = p.Code + " – " + p.Name }).ToListAsync(), "Id", "Name", selected);
    protected async Task<SelectList> WarehousesAsync(int? selected = null) =>
        new(await Db.Warehouses.Where(w => w.IsActive || w.Id == selected).OrderBy(w => w.Code)
            .Select(w => new { w.Id, Name = w.Code + " – " + w.Name }).ToListAsync(), "Id", "Name", selected);
    protected async Task<SelectList> CashAccountsAsync(int? selected = null) =>
        new(await Db.CashAccounts.Where(c => c.IsActive || c.Id == selected).OrderBy(c => c.Code)
            .Select(c => new { c.Id, Name = c.Code + " – " + c.Name }).ToListAsync(), "Id", "Name", selected);
    protected async Task<SelectList> ContractsAsync(ContractType type, int? selected = null) =>
        new(await Db.Contracts.Include(c => c.Partner).Where(c => c.Type == type && (c.Status == ContractStatus.Active || c.Id == selected)).OrderByDescending(c => c.SignDate)
            .Select(c => new { c.Id, Name = c.Number + " – " + (c.Partner != null ? c.Partner.Name : "") + " – " + c.Title }).ToListAsync(), "Id", "Name", selected);
    protected async Task<List<Account>> PostableAccountsAsync() =>
        await Db.Accounts.Where(a => a.IsPostable && a.IsActive).OrderBy(a => a.Code).ToListAsync();
    protected async Task<SelectList> AccountSelectAsync(int? selected = null, Func<Account, bool>? filter = null) =>
        new((await PostableAccountsAsync()).Where(a => filter == null || filter(a) || a.Id == selected)
            .Select(a => new { a.Id, Name = a.Code + " – " + a.Name }), "Id", "Name", selected);
    protected async Task<List<Item>> ItemsAsync(Func<Item, bool>? filter = null) =>
        (await Db.Items.Where(i => i.IsActive).OrderBy(i => i.Code).ToListAsync()).Where(i => filter == null || filter(i)).ToList();

    /// <summary>Khoảng ngày mặc định: từ đầu tháng đến hôm nay</summary>
    protected static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.Today).Date;
        var f = (from ?? new DateTime(t.Year, t.Month, 1)).Date;
        return f > t ? (t, f) : (f, t);
    }
}
