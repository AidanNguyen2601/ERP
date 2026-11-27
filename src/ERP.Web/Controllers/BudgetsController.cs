using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Ngân sách chi phí theo khoản mục (tiền tố TK) và bộ phận, theo tháng; cảnh báo / chặn khi ghi sổ vượt ngân sách; báo cáo thực hiện</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class BudgetsController : ErpControllerBase
{
    private readonly BudgetService _svc;
    public BudgetsController(ErpDbContext db, BudgetService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index(int? year)
    {
        var y = year ?? DateTime.Today.Year; ViewBag.Year = y;
        return View(await Db.Budgets.Include(b => b.Lines).Include(b => b.Branch).Where(b => b.Year == y).OrderBy(b => b.Name).ToListAsync());
    }

    public async Task<IActionResult> Details(int id, int? month)
    {
        var b = await Db.Budgets.Include(x => x.Lines).Include(x => x.Branch).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        var m = month ?? (b.Year == DateTime.Today.Year ? DateTime.Today.Month : 12);
        ViewBag.Month = m;
        ViewBag.Usage = await _svc.UsageAsync(b, m);
        var monthly = new Dictionary<int, decimal[]>();
        foreach (var l in b.Lines) monthly[l.Id] = await _svc.MonthlyActualAsync(b, l);
        ViewBag.Monthly = monthly;
        return View(b);
    }

    [Authorize(Roles = AppRoles.Accounting + "," + AppRoles.Director)]
    public async Task<IActionResult> Edit(int? id)
    {
        var b = id == null ? new Budget { Name = $"Ngân sách chi phí {DateTime.Today.Year}" } : await Db.Budgets.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        if (b.Lines.Count == 0) b.Lines.Add(new BudgetLine());
        await BagAsync();
        return View(b);
    }

    private async Task BagAsync()
    {
        ViewBag.Branches = new SelectList(await Db.Branches.OrderBy(x => x.Code).ToListAsync(), "Id", "Name");
        ViewBag.Accounts = await Db.Accounts.Where(a => a.Code.StartsWith("6") || a.Code.StartsWith("8")).OrderBy(a => a.Code).ToListAsync();
        ViewBag.Depts = await Db.CatalogItems.Where(c => c.Type == CatalogType.Department).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting + "," + AppRoles.Director)]
    public async Task<IActionResult> Edit(Budget m, List<BudgetLine> lines)
    {
        lines = lines.Where(l => !string.IsNullOrWhiteSpace(l.AccountPrefix)).ToList();
        ModelState.Clear();
        if (string.IsNullOrWhiteSpace(m.Name)) ModelState.AddModelError("", "Nhập tên ngân sách.");
        if (!lines.Any()) ModelState.AddModelError("", "Nhập ít nhất một khoản mục.");
        if (lines.GroupBy(l => (l.AccountPrefix.Trim(), (l.Department ?? "").Trim())).Any(g => g.Count() > 1)) ModelState.AddModelError("", "Trùng khoản mục + bộ phận.");
        if (!ModelState.IsValid) { m.Lines = lines.Any() ? lines : new List<BudgetLine> { new() }; await BagAsync(); return View(m); }
        Budget b;
        if (m.Id == 0) { b = new Budget(); Db.Budgets.Add(b); }
        else { b = await Db.Budgets.Include(x => x.Lines).FirstAsync(x => x.Id == m.Id); Db.BudgetLines.RemoveRange(b.Lines); b.Lines.Clear(); }
        b.Name = m.Name.Trim(); b.Year = m.Year; b.BranchId = m.BranchId; b.Control = m.Control; b.IsActive = m.IsActive;
        foreach (var l in lines)
        {
            var nl = new BudgetLine { AccountPrefix = l.AccountPrefix.Trim(), Department = string.IsNullOrWhiteSpace(l.Department) ? null : l.Department.Trim(), Note = l.Note };
            for (var k = 1; k <= 12; k++) nl.SetMonth(k, Math.Max(0, l.Month(k)));
            b.Lines.Add(nl);
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu ngân sách {b.Name}.");
        return RedirectToAction(nameof(Details), new { id = b.Id });
    }
}
