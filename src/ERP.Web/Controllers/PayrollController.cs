using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Nhân sự – tiền lương: danh mục nhân viên, chấm công, bảng lương và hạch toán lương tự động</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class PayrollController : ErpControllerBase
{
    private readonly PayrollService _payroll; private readonly CodeService _codes; private readonly DuplicateService _dup; private readonly HrService _hr;
    public PayrollController(ErpDbContext db, PayrollService payroll, CodeService codes, DuplicateService dup, HrService hr) : base(db) { _payroll = payroll; _codes = codes; _dup = dup; _hr = hr; }

    // ------------------------------------------------------------------ nhân viên
    public async Task<IActionResult> Employees(string? q, bool inactive = false)
    {
        var list = Db.Employees.Include(e => e.Branch).AsQueryable();
        if (!inactive) list = list.Where(e => e.IsActive);
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(e => e.Code.Contains(q) || e.FullName.Contains(q) || (e.Department ?? "").Contains(q));
        ViewBag.Q = q; ViewBag.Inactive = inactive;
        return View(await list.OrderBy(e => e.Code).ToListAsync());
    }

    private async Task FormBagAsync()
    {
        ViewBag.Branches = new SelectList(await Db.Branches.OrderBy(b => b.Code).ToListAsync(), "Id", "Name");
        ViewBag.Depts = await Db.CatalogItems.Where(c => c.Type == CatalogType.Department).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
        ViewBag.Shifts = new SelectList(await Db.Shifts.Where(s => s.IsActive).OrderBy(s => s.StartMinute).Select(s => new { s.Id, Name = s.Name + " (" + s.Code + ")" }).ToListAsync(), "Id", "Name");
        ViewBag.ExpenseAccounts = (await PostableAccountsAsync()).Where(a => new[] { "622", "627", "641", "642", "154", "241" }.Any(p => a.Code.StartsWith(p))).ToList();
    }

    public async Task<IActionResult> EditEmployee(int? id)
    {
        var e = id == null ? new Employee { Code = await _codes.NextAsync("employee") ?? "" } : await Db.Employees.FindAsync(id);
        if (e == null) return NotFound();
        await FormBagAsync();
        return View(e);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditEmployee(Employee m, bool confirmDuplicate = false)
    {
        m.Code = (m.Code ?? "").Trim().ToUpperInvariant();
        if (m.Code == "") m.Code = await _codes.NextAsync("employee") ?? "";
        if (m.Code == "") ModelState.AddModelError(nameof(m.Code), "Nhập mã nhân viên");
        else if (m.Id == 0 && await _codes.ValidateAsync("employee", m.Code) is { } ruleErr) ModelState.AddModelError(nameof(m.Code), ruleErr);
        if (await Db.Employees.IgnoreQueryFilters().AnyAsync(x => x.Code == m.Code && x.Id != m.Id)) ModelState.AddModelError(nameof(m.Code), "Mã nhân viên đã tồn tại");
        if (!await Db.Accounts.AnyAsync(a => a.Code == m.ExpenseAccount && a.IsPostable)) ModelState.AddModelError(nameof(m.ExpenseAccount), "TK chi phí không hợp lệ");
        ModelState.Remove(nameof(Employee.Branch)); ModelState.Remove(nameof(Employee.Code));
        var warns = await _dup.EmployeeAsync(m);
        if (warns.Any() && !(confirmDuplicate && DuplicateService.CanOverride(User)))
        {
            ViewBag.Duplicates = warns;
            ModelState.AddModelError("", "Dữ liệu trùng – kiểm tra lại hoặc xác nhận lưu.");
        }
        if (!ModelState.IsValid) { await FormBagAsync(); return View(m); }
        if (m.InsuranceSalary == 0) m.InsuranceSalary = m.BaseSalary;
        if (m.Id == 0) { Db.Employees.Add(m); await Db.SaveChangesAsync(); _hr.LogHistory(m.Id, HistoryKind.Hired, null, $"{m.Position} – {m.Department}", "Tuyển dụng", m.StartDate); }
        else
        {
            var old = await Db.Employees.AsNoTracking().FirstAsync(x => x.Id == m.Id);
            if (old.Department != m.Department) _hr.LogHistory(m.Id, HistoryKind.Transfer, old.Department, m.Department, null);
            if (old.Position != m.Position) _hr.LogHistory(m.Id, HistoryKind.Promotion, old.Position, m.Position, null);
            if (old.BaseSalary != m.BaseSalary) _hr.LogHistory(m.Id, HistoryKind.SalaryChange, old.BaseSalary.ToMoney(), m.BaseSalary.ToMoney(), null);
            if (old.IsActive && !m.IsActive && m.ResignDate == null) m.ResignDate = DateTime.Today;
            Db.Employees.Update(m);
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu nhân viên {m.Code} – {m.FullName}.");
        return RedirectToAction(nameof(Employees));
    }

    // ------------------------------------------------------------------ chấm công
    public async Task<IActionResult> Attendance(int? year, int? month)
    {
        int y = year ?? DateTime.Today.Year, mo = month ?? DateTime.Today.Month;
        var emps = await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).ToListAsync();
        ViewBag.Year = y; ViewBag.Month = mo;
        ViewBag.Att = await Db.Attendances.Where(a => a.Year == y && a.Month == mo).ToDictionaryAsync(a => a.EmployeeId);
        ViewBag.StdDays = (await _payroll.RatesAsync()).StdDays;
        return View(emps);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Attendance(int year, int month, int[] employeeId, decimal[] workDays, decimal[] overtime, decimal[] leave)
    {
        var att = await Db.Attendances.Where(a => a.Year == year && a.Month == month).ToDictionaryAsync(a => a.EmployeeId);
        for (var i = 0; i < employeeId.Length; i++)
        {
            if (!att.TryGetValue(employeeId[i], out var a)) { a = new Attendance { EmployeeId = employeeId[i], Year = year, Month = month, Source = "Nhập tay" }; Db.Attendances.Add(a); }
            a.WorkDays = Math.Clamp(workDays.ElementAtOrDefault(i), 0, 31); a.OvertimeHours = Math.Max(0, overtime.ElementAtOrDefault(i)); a.LeaveDays = Math.Max(0, leave.ElementAtOrDefault(i));
        }
        await Db.SaveChangesAsync();
        Success($"Đã lưu bảng chấm công tháng {month:00}/{year}.");
        return RedirectToAction(nameof(Attendance), new { year, month });
    }

    // ------------------------------------------------------------------ bảng lương
    public async Task<IActionResult> Index()
    {
        ViewBag.Branches = new SelectList(await Db.Branches.OrderBy(b => b.Code).ToListAsync(), "Id", "Name");
        return View(await Db.PayrollRuns.Include(r => r.Lines).Include(r => r.Branch).OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int year, int month, int? branchId)
    {
        var run = await _payroll.CreateAsync(year, month, branchId);
        Success($"Đã lập bảng lương tháng {month:00}/{year}: {run.Lines.Count} nhân viên.");
        return RedirectToAction(nameof(Details), new { id = run.Id });
    }

    private Task<PayrollRun?> LoadAsync(int id) => Db.PayrollRuns.Include(r => r.Branch).Include(r => r.Lines).ThenInclude(l => l.Employee).FirstOrDefaultAsync(r => r.Id == id);

    public async Task<IActionResult> Details(int id, [FromServices] WorkflowService wf)
    {
        var run = await LoadAsync(id); if (run == null) return NotFound();
        ViewBag.Approval = await wf.LatestAsync(ApprovalDocType.Payroll, id);
        return View(run);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Recalculate(int id)
    {
        var run = await LoadAsync(id); if (run == null) return NotFound();
        await _payroll.RecalculateAsync(run);
        Success("Đã tính lại bảng lương theo chấm công và hồ sơ nhân viên mới nhất.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var run = await LoadAsync(id); if (run == null) return NotFound();
        try { Success(await _payroll.SubmitAsync(run)); }
        catch (PendingApprovalException ex) { TempData["Warning"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string reason)
    {
        var run = await LoadAsync(id); if (run == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) { Error("Nhập lý do hủy."); return RedirectToAction(nameof(Details), new { id }); }
        await _payroll.CancelAsync(run, reason);
        Success("Đã hủy bảng lương" + (run.JournalEntryId != null ? " và lập bút toán đảo." : "."));
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Payslip(int id, int lineId)
    {
        var run = await LoadAsync(id); var line = run?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return NotFound();
        ViewBag.Run = run;
        return View(line);
    }
}
