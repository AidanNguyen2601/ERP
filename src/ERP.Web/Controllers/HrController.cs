using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Nhân sự: hồ sơ, hợp đồng, ca và chấm công ngày, nghỉ phép, thành phần lương – thưởng – lương sản phẩm, bảo hiểm và quyết toán thuế TNCN</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class HrController : ErpControllerBase
{
    private readonly HrService _hr; private readonly PayrollService _payroll;
    public HrController(ErpDbContext db, HrService hr, PayrollService payroll) : base(db) { _hr = hr; _payroll = payroll; }
    private bool CanEdit => User.InAny(AppRoles.Admin + "," + AppRoles.Accountant);

    // ------------------------------------------------------------------ hồ sơ nhân sự
    public async Task<IActionResult> Profile(int id)
    {
        var e = await Db.Employees.Include(x => x.Branch).FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return NotFound();
        await _hr.RefreshContractsAsync();
        ViewBag.Contracts = await Db.LaborContracts.Where(c => c.EmployeeId == id).OrderByDescending(c => c.StartDate).ToListAsync();
        ViewBag.History = await Db.EmployeeHistories.Where(h => h.EmployeeId == id).OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).ToListAsync();
        ViewBag.Balance = (await _hr.BalancesAsync(DateTime.Today.Year)).FirstOrDefault(b => b.Employee.Id == id);
        ViewBag.Leaves = await Db.LeaveRequests.Include(l => l.LeaveType).Where(l => l.EmployeeId == id).OrderByDescending(l => l.FromDate).Take(10).ToListAsync();
        ViewBag.Comps = await Db.EmployeeSalaryComponents.Include(c => c.Component).Where(c => c.EmployeeId == id).ToListAsync();
        ViewBag.Shift = e.DefaultShiftId == null ? null : await Db.Shifts.FindAsync(e.DefaultShiftId);
        ViewBag.CanEdit = CanEdit;
        return View(e);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHistory(int id, HistoryKind kind, DateTime? date, string? from, string? to, string? note)
    {
        if (!CanEdit) return Denied();
        _hr.LogHistory(id, kind, from, to, note, date);
        if (kind == HistoryKind.Resigned) { var e = await Db.Employees.FindAsync(id); if (e != null) { e.ResignDate = (date ?? DateTime.Today).Date; e.IsActive = false; } }
        await Db.SaveChangesAsync();
        Success("Đã ghi nhận vào quá trình công tác.");
        return RedirectToAction(nameof(Profile), new { id });
    }

    // ------------------------------------------------------------------ hợp đồng
    public async Task<IActionResult> Contracts(int days = 60, bool all = false)
    {
        var expiring = await _hr.RefreshContractsAsync(days);
        ViewBag.Expiring = expiring; ViewBag.Days = days; ViewBag.All = all; ViewBag.CanEdit = CanEdit;
        var q = Db.LaborContracts.Include(c => c.Employee).AsQueryable();
        if (!all) q = q.Where(c => c.Status == LaborContractStatus.Active);
        ViewBag.Emps = new SelectList(await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.FullName }).ToListAsync(), "Id", "Name");
        ViewBag.NoContract = await Db.Employees.Where(e => e.IsActive && !Db.LaborContracts.Any(c => c.EmployeeId == e.Id && c.Status == LaborContractStatus.Active)).OrderBy(e => e.Code).ToListAsync();
        return View(await q.OrderBy(c => c.EndDate == null).ThenBy(c => c.EndDate).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContract(LaborContract model, bool updateSalary)
    {
        if (!CanEdit) return Denied();
        model.Number = (model.Number ?? "").Trim();
        if (string.IsNullOrWhiteSpace(model.Number)) throw new BusinessException("Nhập số hợp đồng.");
        await _hr.SaveContractAsync(model);
        if (updateSalary && model.Salary > 0)
        {
            var e = await Db.Employees.FindAsync(model.EmployeeId);
            if (e != null && e.BaseSalary != model.Salary) { _hr.LogHistory(e.Id, HistoryKind.SalaryChange, e.BaseSalary.ToMoney(), model.Salary.ToMoney(), "Theo hợp đồng " + model.Number, model.StartDate); e.BaseSalary = model.Salary; if (e.InsuranceSalary < model.Salary) e.InsuranceSalary = model.Salary; await Db.SaveChangesAsync(); }
        }
        Success($"Đã lưu hợp đồng {model.Number}.");
        return RedirectToAction(nameof(Contracts));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TerminateContract(int id, string? reason)
    {
        if (!CanEdit) return Denied();
        var c = await Db.LaborContracts.FindAsync(id);
        if (c == null) return NotFound();
        c.Status = LaborContractStatus.Terminated; c.Note = (c.Note + " [Chấm dứt: " + reason + "]").Trim();
        _hr.LogHistory(c.EmployeeId, HistoryKind.Other, c.Number, null, "Chấm dứt hợp đồng: " + reason);
        await Db.SaveChangesAsync();
        Success($"Đã chấm dứt hợp đồng {c.Number}.");
        return RedirectToAction(nameof(Contracts));
    }

    // ------------------------------------------------------------------ ca làm việc
    public async Task<IActionResult> Shifts(int? edit)
    {
        ViewBag.CanEdit = CanEdit; ViewBag.Edit = edit == null ? new Shift() : await Db.Shifts.FindAsync(edit) ?? new Shift();
        ViewBag.Users = (await Db.Employees.Where(e => e.DefaultShiftId != null).GroupBy(e => e.DefaultShiftId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
        return View(await Db.Shifts.OrderBy(s => s.StartMinute).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveShift(Shift model, string startTime, string endTime)
    {
        if (!CanEdit) return Denied();
        static int Min(string t) => TimeSpan.TryParse(t, out var ts) ? (int)ts.TotalMinutes : 0;
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name)) { Error("Nhập mã và tên ca."); return RedirectToAction(nameof(Shifts)); }
        if (await Db.Shifts.AnyAsync(s => s.Code == model.Code && s.Id != model.Id)) { Error("Mã ca đã tồn tại."); return RedirectToAction(nameof(Shifts)); }
        var s = model.Id == 0 ? new Shift() : await Db.Shifts.FindAsync(model.Id) ?? new Shift();
        if (s.Id == 0) Db.Shifts.Add(s);
        s.Code = model.Code; s.Name = model.Name.Trim(); s.StartMinute = Min(startTime); s.EndMinute = Min(endTime); s.BreakMinutes = model.BreakMinutes; s.GraceMinutes = model.GraceMinutes;
        s.IsNight = s.EndMinute <= s.StartMinute; s.OtRate = model.OtRate <= 0 ? 1.5m : model.OtRate; s.IsActive = model.IsActive;
        await Db.SaveChangesAsync();
        Success($"Đã lưu ca {s.Code}.");
        return RedirectToAction(nameof(Shifts));
    }

    // ------------------------------------------------------------------ chấm công ngày & máy chấm công
    public async Task<IActionResult> TimeClock(int? year, int? month)
    {
        int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
        var from = new DateTime(y, m, 1); var to = from.AddMonths(1);
        ViewBag.Year = y; ViewBag.Month = m; ViewBag.CanEdit = CanEdit;
        ViewBag.Emps = await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).ToListAsync();
        ViewBag.Days = (await Db.AttendanceDays.Include(d => d.Shift).Where(d => d.Date >= from && d.Date < to).ToListAsync()).GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.ToDictionary(d => d.Date.Day));
        ViewBag.Pending = await Db.TimeClockPunches.CountAsync(p => !p.Processed);
        ViewBag.Punches = await Db.TimeClockPunches.OrderByDescending(p => p.PunchTime).Take(15).ToListAsync();
        ViewBag.Shifts = new SelectList(await Db.Shifts.Where(s => s.IsActive).OrderBy(s => s.StartMinute).Select(s => new { s.Id, Name = s.Name + " (" + s.Code + ")" }).ToListAsync(), "Id", "Name");
        ViewBag.Att = await Db.Attendances.Where(a => a.Year == y && a.Month == m).ToDictionaryAsync(a => a.EmployeeId);
        return View();
    }

    /// <summary>Nhập lượt quẹt thô từ file hoặc dán nội dung: mỗi dòng "mã NV, ngày giờ[, thiết bị]"</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportPunches(IFormFile? file, string? text)
    {
        if (!CanEdit) return Denied();
        var content = text ?? "";
        if (file is { Length: > 0 }) { using var sr = new StreamReader(file.OpenReadStream()); content += "\n" + await sr.ReadToEndAsync(); }
        var codes = (await Db.Employees.IgnoreQueryFilters().Select(e => e.Code).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] fmts = { "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "d/M/yyyy H:mm" };
        int ok = 0, dup = 0, bad = 0;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().Split(new[] { ',', ';', '\t' }, StringSplitOptions.TrimEntries);
            if (p.Length < 2 || !codes.Contains(p[0]) || !DateTime.TryParseExact(p[1], fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) { if (!line.StartsWith("Mã", StringComparison.OrdinalIgnoreCase)) bad++; continue; }
            var code = p[0].ToUpperInvariant();
            if (await Db.TimeClockPunches.AnyAsync(x => x.EmployeeCode == code && x.PunchTime == t)) { dup++; continue; }
            Db.TimeClockPunches.Add(new TimeClockPunch { EmployeeCode = code, PunchTime = t, Device = p.Length > 2 ? p[2] : "Nhập file" });
            ok++;
        }
        await Db.SaveChangesAsync();
        var (days, _) = ok > 0 ? await _hr.ProcessPunchesAsync() : (0, 0);
        Success($"Đã nhập {ok} lượt quẹt (trùng {dup}, lỗi {bad}) và xử lý thành {days} ngày công.");
        return RedirectToAction(nameof(TimeClock));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ProcessPunches()
    {
        if (!CanEdit) return Denied();
        var (days, n) = await _hr.ProcessPunchesAsync();
        Success($"Đã xử lý {n} lượt quẹt thành {days} ngày công.");
        return RedirectToAction(nameof(TimeClock));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RollUp(int year, int month)
    {
        if (!CanEdit) return Denied();
        var n = await _hr.RollUpMonthAsync(year, month);
        Success($"Đã tổng hợp công tháng {month:00}/{year} cho {n} nhân viên vào bảng chấm công tháng (dùng tính lương).");
        return RedirectToAction(nameof(TimeClock), new { year, month });
    }

    /// <summary>Nhập / sửa tay chấm công một ngày</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDay(int employeeId, DateTime date, AttendanceKind kind, int? shiftId, string? checkIn, string? checkOut, decimal overtime, string? note)
    {
        if (!CanEdit) return Denied();
        var d = await Db.AttendanceDays.FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.Date == date.Date);
        if (d == null) { d = new AttendanceDay { EmployeeId = employeeId, Date = date.Date }; Db.AttendanceDays.Add(d); }
        var sh = shiftId == null ? null : await Db.Shifts.FindAsync(shiftId);
        d.Kind = kind; d.ShiftId = shiftId; d.Source = "Nhập tay"; d.Note = note; d.OvertimeHours = overtime;
        d.CheckIn = TimeSpan.TryParse(checkIn, out var a) ? date.Date + a : null; d.CheckOut = TimeSpan.TryParse(checkOut, out var b) ? date.Date + b : null;
        if (kind == AttendanceKind.Present && d.CheckIn != null && d.CheckOut != null)
        {
            var gross = (decimal)(d.CheckOut.Value - d.CheckIn.Value).TotalHours; if (gross < 0) gross += 24;
            d.WorkedHours = Math.Round(Math.Max(0, gross - (gross > 5 ? (sh?.BreakMinutes ?? 60) / 60m : 0)), 2);
            d.LateMinutes = sh == null ? 0 : Math.Max(0, (int)d.CheckIn.Value.TimeOfDay.TotalMinutes - sh.StartMinute - sh.GraceMinutes);
        }
        else if (kind == AttendanceKind.Present) d.WorkedHours = sh?.PaidHours ?? 8;
        else d.WorkedHours = 0;
        await Db.SaveChangesAsync();
        Success("Đã lưu chấm công ngày " + date.ToString("dd/MM/yyyy") + ".");
        return RedirectToAction(nameof(TimeClock), new { year = date.Year, month = date.Month });
    }

    // ------------------------------------------------------------------ nghỉ phép
    public async Task<IActionResult> Leave(int? year, LeaveStatus? status)
    {
        var y = year ?? DateTime.Today.Year;
        ViewBag.Year = y; ViewBag.Status = status; ViewBag.CanEdit = CanEdit;
        ViewBag.Balances = await _hr.BalancesAsync(y);
        ViewBag.Types = await Db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Code).ToListAsync();
        ViewBag.Emps = new SelectList(await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.FullName }).ToListAsync(), "Id", "Name");
        var q = Db.LeaveRequests.Include(l => l.Employee).Include(l => l.LeaveType).Where(l => l.FromDate.Year == y);
        if (status.HasValue) q = q.Where(l => l.Status == status);
        return View(await q.OrderByDescending(l => l.FromDate).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestLeave(LeaveRequest model)
    {
        await _hr.RequestLeaveAsync(model);
        Success("Đã tạo đơn nghỉ, chờ duyệt.");
        return RedirectToAction(nameof(Leave), new { year = model.FromDate.Year });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideLeave(int id, bool approve, string? note)
    {
        var r = await Db.LeaveRequests.FindAsync(id);
        if (r == null) return NotFound();
        await _hr.DecideLeaveAsync(r, approve, note);
        Success(approve ? "Đã duyệt đơn nghỉ, ghi nhận vào chấm công." : "Đã từ chối đơn nghỉ.");
        return RedirectToAction(nameof(Leave), new { year = r.FromDate.Year });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelLeave(int id)
    {
        var r = await Db.LeaveRequests.FindAsync(id);
        if (r == null) return NotFound();
        if (r.Status == LeaveStatus.Approved) throw new BusinessException("Đơn đã duyệt, không hủy được (liên hệ nhân sự điều chỉnh chấm công).");
        r.Status = LeaveStatus.Cancelled;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Leave), new { year = r.FromDate.Year });
    }

    // ------------------------------------------------------------------ thành phần lương, thưởng, lương sản phẩm
    public async Task<IActionResult> Components(int? edit)
    {
        ViewBag.CanEdit = CanEdit; ViewBag.Edit = edit == null ? new SalaryComponent() : await Db.SalaryComponents.FindAsync(edit) ?? new SalaryComponent();
        ViewBag.Emps = new SelectList(await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.FullName }).ToListAsync(), "Id", "Name");
        ViewBag.Assigned = await Db.EmployeeSalaryComponents.Include(a => a.Employee).Include(a => a.Component).OrderBy(a => a.Employee!.Code).ToListAsync();
        ViewBag.Comps = new SelectList(await Db.SalaryComponents.Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Id, Name = c.Code + " – " + c.Name }).ToListAsync(), "Id", "Name");
        return View(await Db.SalaryComponents.OrderBy(c => c.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveComponent(SalaryComponent model)
    {
        if (!CanEdit) return Denied();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name)) { Error("Nhập mã và tên khoản."); return RedirectToAction(nameof(Components)); }
        if (!string.IsNullOrWhiteSpace(model.Formula) && FormulaEvaluator.Validate(model.Formula) is { } err) { Error("Công thức: " + err); return RedirectToAction(nameof(Components), new { edit = model.Id == 0 ? (int?)null : model.Id }); }
        if (await Db.SalaryComponents.AnyAsync(c => c.Code == model.Code && c.Id != model.Id)) { Error("Mã khoản đã tồn tại."); return RedirectToAction(nameof(Components)); }
        var c = model.Id == 0 ? new SalaryComponent() : await Db.SalaryComponents.FindAsync(model.Id) ?? new SalaryComponent();
        if (c.Id == 0) Db.SalaryComponents.Add(c);
        c.Code = model.Code; c.Name = model.Name.Trim(); c.Kind = model.Kind; c.Taxable = model.Taxable; c.Formula = string.IsNullOrWhiteSpace(model.Formula) ? null : model.Formula.Trim(); c.Note = model.Note; c.IsActive = model.IsActive;
        await Db.SaveChangesAsync();
        Success($"Đã lưu khoản lương {c.Code}.");
        return RedirectToAction(nameof(Components));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int employeeId, int salaryComponentId, decimal amount)
    {
        if (!CanEdit) return Denied();
        var a = await Db.EmployeeSalaryComponents.FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.SalaryComponentId == salaryComponentId);
        if (a == null) Db.EmployeeSalaryComponents.Add(a = new EmployeeSalaryComponent { EmployeeId = employeeId, SalaryComponentId = salaryComponentId });
        a.Amount = amount;
        await Db.SaveChangesAsync();
        Success("Đã gán khoản lương cho nhân viên.");
        return RedirectToAction(nameof(Components));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Unassign(int id)
    {
        if (!CanEdit) return Denied();
        var a = await Db.EmployeeSalaryComponents.FindAsync(id);
        if (a != null) { Db.EmployeeSalaryComponents.Remove(a); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Components));
    }

    /// <summary>Thử công thức với số liệu một nhân viên</summary>
    public async Task<IActionResult> TestFormula(string formula, int employeeId)
    {
        var e = await Db.Employees.FindAsync(employeeId);
        if (e == null) return Json(new { error = "Chọn nhân viên." });
        try
        {
            var std = (await _payroll.RatesAsync()).StdDays;
            var vars = new Dictionary<string, decimal> { ["BaseSalary"] = e.BaseSalary, ["WorkDays"] = std, ["StdDays"] = std, ["OvertimeHours"] = 0, ["Dependents"] = e.Dependents, ["Years"] = Math.Floor(HrService.YearsOfService(e)), ["InsuranceSalary"] = e.InsuranceSalary, ["SalaryByDays"] = e.BaseSalary, ["Allowance"] = e.Allowance };
            return Json(new { value = FormulaEvaluator.Eval(formula, vars) });
        }
        catch (BusinessException ex) { return Json(new { error = ex.Message }); }
    }

    public async Task<IActionResult> Bonuses(int? year, int? month)
    {
        int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month; var from = new DateTime(y, m, 1);
        ViewBag.Year = y; ViewBag.Month = m; ViewBag.CanEdit = CanEdit;
        ViewBag.Emps = new SelectList(await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.FullName }).ToListAsync(), "Id", "Name");
        ViewBag.Items = new SelectList(await Db.Items.Where(i => i.IsActive && i.Type == ItemType.FinishedGood || i.Type == ItemType.SemiFinished).OrderBy(i => i.Code).Select(i => new { i.Id, Name = i.Code + " – " + i.Name }).ToListAsync(), "Id", "Name");
        ViewBag.Pieces = await Db.PieceRateEntries.Include(p => p.Employee).Include(p => p.Item).Where(p => p.Date >= from && p.Date < from.AddMonths(1)).OrderByDescending(p => p.Date).ToListAsync();
        return View(await Db.PayrollBonuses.Include(b => b.Employee).Where(b => b.Year == y && b.Month == m).OrderBy(b => b.Employee!.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBonus(int employeeId, int year, int month, string description, decimal amount, bool taxable = true)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(description) || amount == 0) throw new BusinessException("Nhập nội dung và số tiền thưởng.");
        Db.PayrollBonuses.Add(new PayrollBonus { EmployeeId = employeeId, Year = year, Month = month, Description = description.Trim(), Amount = amount, Taxable = taxable });
        await Db.SaveChangesAsync();
        Success("Đã thêm khoản thưởng. Tính lại bảng lương nháp để cập nhật.");
        return RedirectToAction(nameof(Bonuses), new { year, month });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBonus(int id, int year, int month)
    {
        if (!CanEdit) return Denied();
        var b = await Db.PayrollBonuses.FindAsync(id);
        if (b != null) { Db.PayrollBonuses.Remove(b); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Bonuses), new { year, month });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPiece(int employeeId, DateTime date, int? itemId, string? description, decimal quantity, decimal rate)
    {
        if (!CanEdit) return Denied();
        if (quantity <= 0 || rate < 0) throw new BusinessException("Số lượng > 0 và đơn giá không âm.");
        Db.PieceRateEntries.Add(new PieceRateEntry { EmployeeId = employeeId, Date = date.Date, ItemId = itemId, Description = description, Quantity = quantity, Rate = rate });
        await Db.SaveChangesAsync();
        Success("Đã ghi nhận lương sản phẩm.");
        return RedirectToAction(nameof(Bonuses), new { year = date.Year, month = date.Month });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePiece(int id, int year, int month)
    {
        if (!CanEdit) return Denied();
        var p = await Db.PieceRateEntries.FindAsync(id);
        if (p != null) { Db.PieceRateEntries.Remove(p); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Bonuses), new { year, month });
    }

    // ------------------------------------------------------------------ tuân thủ: bảo hiểm & quyết toán thuế TNCN
    public async Task<IActionResult> Insurance(int? year, int? month, bool excel = false)
    {
        int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
        var lines = await Db.PayrollLines.Include(l => l.Employee).Include(l => l.PayrollRun).Where(l => l.PayrollRun!.Year == y && l.PayrollRun.Month == m && l.PayrollRun.Status != DocStatus.Cancelled).OrderBy(l => l.Employee!.Code).ToListAsync();
        ViewBag.Year = y; ViewBag.Month = m;
        if (excel)
        {
            var cols = new List<ReportExport.Col> { new("Mã NV", FieldKind.Text), new("Họ tên", FieldKind.Text), new("Số sổ BHXH", FieldKind.Text), new("Lương đóng BH", FieldKind.Money), new("BHXH NLĐ", FieldKind.Money), new("BHYT NLĐ", FieldKind.Money), new("BHTN NLĐ", FieldKind.Money),
                new("BHXH DN", FieldKind.Money), new("BHYT DN", FieldKind.Money), new("BHTN DN", FieldKind.Money), new("KPCĐ", FieldKind.Money), new("Tổng nộp", FieldKind.Money) };
            var rows = lines.Select(l => new object?[] { l.Employee?.Code, l.Employee?.FullName, l.Employee?.InsuranceBookNo, l.InsuranceBase, l.EmpBhxh, l.EmpBhyt, l.EmpBhtn, l.ErBhxh, l.ErBhyt, l.ErBhtn, l.ErKpcd, l.EmpTotal + l.ErTotal }).ToList();
            object?[] tot = { "Cộng", "", "", lines.Sum(l => l.InsuranceBase), lines.Sum(l => l.EmpBhxh), lines.Sum(l => l.EmpBhyt), lines.Sum(l => l.EmpBhtn), lines.Sum(l => l.ErBhxh), lines.Sum(l => l.ErBhyt), lines.Sum(l => l.ErBhtn), lines.Sum(l => l.ErKpcd), lines.Sum(l => l.EmpTotal + l.ErTotal) };
            return File(ReportExport.Xlsx("BHXH", $"Bảng kê đóng bảo hiểm tháng {m:00}/{y}", new[] { "BHXH, BHYT, BHTN, KPCĐ" }, cols, rows, tot), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"bao-hiem-{y}{m:00}.xlsx");
        }
        ViewBag.Rates = await _payroll.RatesAsync();
        return View(lines);
    }

    public record PitRow(Employee Employee, int Months, decimal Gross, decimal NonTax, decimal Exempt, decimal Insurance, decimal Family, decimal Taxable, decimal AnnualTax, decimal Withheld)
    {
        public decimal Difference => AnnualTax - Withheld;
    }

    public async Task<IActionResult> PitFinalization(int? year, bool excel = false, bool xml = false, [FromServices] Microsoft.Extensions.Options.IOptions<ErpSettings>? cfg = null)
    {
        var y = year ?? DateTime.Today.Year;
        var r = await _payroll.RatesAsync();
        var annualBrackets = r.Brackets.Select(b => (b.Upper <= 0 ? 0 : b.Upper * 12, b.Rate)).ToList();
        var lines = await Db.PayrollLines.Include(l => l.Employee).Include(l => l.PayrollRun).Where(l => l.PayrollRun!.Year == y && l.PayrollRun.Status == DocStatus.Posted).ToListAsync();
        var rows = new List<PitRow>();
        foreach (var g in lines.GroupBy(l => l.EmployeeId).OrderBy(g => g.First().Employee!.Code))
        {
            var e = g.First().Employee!; var months = g.Select(l => l.PayrollRun!.Month).Distinct().Count();
            var gross = g.Sum(l => l.Gross); var non = g.Sum(l => l.NonTaxAllowance + l.ExtraNonTax); var ex = g.Sum(l => Math.Round(l.Overtime / 1.5m * 0.5m, 0)); var ins = g.Sum(l => l.EmpTotal);
            var family = months * (r.Personal + e.Dependents * r.Dependent);
            var taxable = Math.Max(0, gross - non - ex - ins - family);
            rows.Add(new PitRow(e, months, gross, non, ex, ins, family, taxable, PayrollService.Pit(taxable, annualBrackets), g.Sum(l => l.Pit)));
        }
        ViewBag.Year = y;
        if (xml && cfg != null)
            return File(System.Text.Encoding.UTF8.GetBytes(ComplianceService.Pit05QttXml(y, rows.Select(r => (r.Employee, r.Gross - r.NonTax - r.Exempt, r.Taxable, r.Insurance, r.Family, r.AnnualTax, r.Withheld)), cfg.Value)), "application/xml", $"05QTT_TNCN_{y}.xml");
        if (excel)
        {
            var cols = new List<ReportExport.Col> { new("Mã NV", FieldKind.Text), new("Họ tên", FieldKind.Text), new("MST", FieldKind.Text), new("Số tháng", FieldKind.Number), new("Tổng thu nhập", FieldKind.Money), new("Không chịu thuế", FieldKind.Money), new("Miễn thuế (làm thêm)", FieldKind.Money),
                new("Bảo hiểm", FieldKind.Money), new("Giảm trừ gia cảnh", FieldKind.Money), new("Thu nhập tính thuế", FieldKind.Money), new("Thuế phải nộp cả năm", FieldKind.Money), new("Thuế đã khấu trừ", FieldKind.Money), new("Còn phải nộp (+) / hoàn (−)", FieldKind.Money) };
            var data = rows.Select(x => new object?[] { x.Employee.Code, x.Employee.FullName, x.Employee.PersonalTaxCode, (decimal)x.Months, x.Gross, x.NonTax, x.Exempt, x.Insurance, x.Family, x.Taxable, x.AnnualTax, x.Withheld, x.Difference }).ToList();
            object?[] tot = { "Cộng", "", "", 0m, rows.Sum(x => x.Gross), rows.Sum(x => x.NonTax), rows.Sum(x => x.Exempt), rows.Sum(x => x.Insurance), rows.Sum(x => x.Family), rows.Sum(x => x.Taxable), rows.Sum(x => x.AnnualTax), rows.Sum(x => x.Withheld), rows.Sum(x => x.Difference) };
            return File(ReportExport.Xlsx("QTT-TNCN", $"Quyết toán thuế TNCN năm {y}", new[] { "Mẫu 05/QTT-TNCN – bảng kê tính thuế cả năm" }, cols, data, tot), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"quyet-toan-tncn-{y}.xlsx");
        }
        return View(rows);
    }
}
