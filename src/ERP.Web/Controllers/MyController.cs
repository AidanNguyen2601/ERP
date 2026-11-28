using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Cổng nhân viên tự phục vụ: hồ sơ, phiếu lương, phép năm, gửi đơn nghỉ, tài sản đang giữ, đào tạo, tự đánh giá KPI</summary>
[Authorize]
public class MyController : ErpControllerBase
{
    private readonly HrService _hr;
    public MyController(ErpDbContext db, HrService hr) : base(db) => _hr = hr;

    /// <summary>Nhân viên của tài khoản: gắn trực tiếp, hoặc tự nhận theo email trùng</summary>
    private async Task<Employee?> MeEmployeeAsync()
    {
        var u = await Db.Users.FindAsync(Me);
        if (u == null) return null;
        if (u.EmployeeId != null) return await Db.Employees.FindAsync(u.EmployeeId);
        if (string.IsNullOrWhiteSpace(u.Email)) return null;
        var e = await Db.Employees.Where(x => x.IsActive && x.Email == u.Email).ToListAsync();
        if (e.Count != 1) return null;
        u.EmployeeId = e[0].Id; await Db.SaveChangesAsync();
        return e[0];
    }

    public async Task<IActionResult> Index()
    {
        var e = await MeEmployeeAsync();
        if (e == null) return View("NotLinked");
        var y = DateTime.Today.Year;
        ViewBag.Balance = (await _hr.BalancesAsync(y)).FirstOrDefault(b => b.Employee.Id == e.Id);
        ViewBag.Payslips = await Db.PayrollLines.Include(l => l.PayrollRun).Where(l => l.EmployeeId == e.Id && l.PayrollRun!.Status == DocStatus.Posted)
            .OrderByDescending(l => l.PayrollRun!.Year).ThenByDescending(l => l.PayrollRun!.Month).Take(12).ToListAsync();
        ViewBag.Leaves = await Db.LeaveRequests.Include(l => l.LeaveType).Where(l => l.EmployeeId == e.Id).OrderByDescending(l => l.FromDate).Take(10).ToListAsync();
        ViewBag.Types = await Db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Code).ToListAsync();
        ViewBag.Assets = await Db.EmployeeAssetIssues.Where(a => a.EmployeeId == e.Id && a.ReturnedDate == null).ToListAsync();
        ViewBag.Trainings = await Db.TrainingAttendees.Include(a => a.Course).Where(a => a.EmployeeId == e.Id).OrderByDescending(a => a.Course!.FromDate).Take(10).ToListAsync();
        ViewBag.Appraisal = await Db.Appraisals.Include(a => a.Cycle).Where(a => a.EmployeeId == e.Id && !a.Cycle!.IsClosed).OrderByDescending(a => a.Id).FirstOrDefaultAsync();
        ViewBag.Contract = await Db.LaborContracts.Where(c => c.EmployeeId == e.Id && c.Status == LaborContractStatus.Active).OrderByDescending(c => c.StartDate).FirstOrDefaultAsync();
        return View(e);
    }

    /// <summary>Phiếu lương của chính mình (không xem được của người khác)</summary>
    public async Task<IActionResult> Payslip(int lineId)
    {
        var e = await MeEmployeeAsync();
        var l = await Db.PayrollLines.Include(x => x.PayrollRun).Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == lineId);
        if (e == null || l == null || l.EmployeeId != e.Id || l.PayrollRun!.Status != DocStatus.Posted) return NotFound();
        ViewBag.Run = await Db.PayrollRuns.Include(r => r.Branch).FirstAsync(r => r.Id == l.PayrollRunId);
        return View("~/Views/Payroll/Payslip.cshtml", l);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestLeave(int leaveTypeId, DateTime fromDate, DateTime toDate, decimal days, string? reason)
    {
        var e = await MeEmployeeAsync() ?? throw new BusinessException("Tài khoản chưa gắn hồ sơ nhân viên.");
        await _hr.RequestLeaveAsync(new LeaveRequest { EmployeeId = e.Id, LeaveTypeId = leaveTypeId, FromDate = fromDate.Date, ToDate = toDate.Date, Days = days, Reason = reason });
        Success("Đã gửi đơn nghỉ – chờ quản lý duyệt.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelLeave(int id)
    {
        var e = await MeEmployeeAsync();
        var l = await Db.LeaveRequests.FindAsync(id);
        if (e == null || l == null || l.EmployeeId != e.Id) return NotFound();
        if (l.Status != LeaveStatus.Pending) throw new BusinessException("Chỉ hủy được đơn đang chờ duyệt.");
        l.Status = LeaveStatus.Cancelled;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SelfReview(int id, string? text)
    {
        var e = await MeEmployeeAsync();
        var a = await Db.Appraisals.Include(x => x.Cycle).FirstOrDefaultAsync(x => x.Id == id);
        if (e == null || a == null || a.EmployeeId != e.Id) return NotFound();
        if (a.Status == AppraisalStatus.Approved || a.Cycle!.IsClosed) throw new BusinessException("Kỳ đánh giá đã chốt.");
        a.SelfReview = text;
        await Db.SaveChangesAsync();
        Success("Đã gửi tự đánh giá.");
        return RedirectToAction(nameof(Index));
    }
}
