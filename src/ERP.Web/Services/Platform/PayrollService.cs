using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Tính lương tháng từ hồ sơ nhân viên + chấm công: lương theo ngày công, làm thêm giờ, phụ cấp, bảo hiểm bắt buộc (người lao động / doanh nghiệp),
/// thuế TNCN lũy tiến sau giảm trừ gia cảnh. Hạch toán: chi phí lương theo bộ phận (622, 627, 641, 642…) / 334, 338x, 3335.
/// Tỷ lệ bảo hiểm, mức giảm trừ, biểu thuế là cài đặt hệ thống (cập nhật khi chính sách thay đổi).
/// </summary>
public class PayrollService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly SettingsService _set;
    private readonly WorkflowService _wf;
    private readonly CurrentUser _user;
    public PayrollService(ErpDbContext db, AccountingService acc, SettingsService set, WorkflowService wf, CurrentUser user) { _db = db; _acc = acc; _set = set; _wf = wf; _user = user; }

    public record Rates(decimal EmpBhxh, decimal EmpBhyt, decimal EmpBhtn, decimal ErBhxh, decimal ErBhyt, decimal ErBhtn, decimal ErKpcd, decimal Personal, decimal Dependent, decimal Cap, decimal StdDays, List<(decimal Upper, decimal Rate)> Brackets);

    public async Task<Rates> RatesAsync()
    {
        var brackets = (await _set.GetAsync("Payroll.PitBrackets", "5000000:5;10000000:10;18000000:15;32000000:20;52000000:25;80000000:30;0:35"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':'))
            .Select(p => (decimal.Parse(p[0], CultureInfo.InvariantCulture), decimal.Parse(p[1], CultureInfo.InvariantCulture))).ToList();
        return new Rates(await _set.DecAsync("Payroll.EmpBhxh", 8), await _set.DecAsync("Payroll.EmpBhyt", 1.5m), await _set.DecAsync("Payroll.EmpBhtn", 1),
            await _set.DecAsync("Payroll.ErBhxh", 17.5m), await _set.DecAsync("Payroll.ErBhyt", 3), await _set.DecAsync("Payroll.ErBhtn", 1), await _set.DecAsync("Payroll.ErKpcd", 2),
            await _set.DecAsync("Payroll.PersonalDeduction", 15_500_000), await _set.DecAsync("Payroll.DependentDeduction", 6_200_000),
            await _set.DecAsync("Payroll.InsuranceCap", 46_800_000), await _set.DecAsync("Payroll.StandardDays", 26), brackets);
    }

    /// <summary>Thuế TNCN lũy tiến từng phần (thu nhập tính thuế tháng)</summary>
    public static decimal Pit(decimal taxable, List<(decimal Upper, decimal Rate)> brackets)
    {
        if (taxable <= 0) return 0;
        decimal tax = 0, lower = 0;
        foreach (var (upper, rate) in brackets)
        {
            var top = upper <= 0 ? decimal.MaxValue : upper;
            if (taxable <= lower) break;
            tax += (Math.Min(taxable, top) - lower) * rate / 100;
            lower = top;
        }
        return Math.Round(tax, 0);
    }

    public static void Compute(PayrollLine l, Employee e, decimal workDays, decimal otHours, decimal stdDays, Rates r)
    {
        l.EmployeeId = e.Id; l.Department = e.Department; l.ExpenseAccount = e.ExpenseAccount; l.BaseSalary = e.BaseSalary; l.WorkDays = workDays;
        l.SalaryByDays = Math.Round(e.BaseSalary * Math.Min(workDays, stdDays) / stdDays, 0);
        l.Overtime = Math.Round(e.BaseSalary / stdDays / 8m * 1.5m * otHours, 0);
        l.Allowance = e.Allowance; l.NonTaxAllowance = e.NonTaxAllowance;
        l.Gross = l.SalaryByDays + l.Overtime + l.Allowance + l.NonTaxAllowance;
        l.InsuranceBase = Math.Min(e.InsuranceSalary, r.Cap);
        l.EmpBhxh = Math.Round(l.InsuranceBase * r.EmpBhxh / 100, 0); l.EmpBhyt = Math.Round(l.InsuranceBase * r.EmpBhyt / 100, 0); l.EmpBhtn = Math.Round(l.InsuranceBase * r.EmpBhtn / 100, 0);
        l.ErBhxh = Math.Round(l.InsuranceBase * r.ErBhxh / 100, 0); l.ErBhyt = Math.Round(l.InsuranceBase * r.ErBhyt / 100, 0); l.ErBhtn = Math.Round(l.InsuranceBase * r.ErBhtn / 100, 0);
        l.ErKpcd = Math.Round(l.InsuranceBase * r.ErKpcd / 100, 0);
        l.FamilyDeduction = r.Personal + e.Dependents * r.Dependent;
        // Phần lương làm thêm giờ trả cao hơn ngày thường được miễn thuế: chỉ tính phần lương giờ bình thường (1/1.5)
        var otExempt = Math.Round(l.Overtime / 1.5m * 0.5m, 0);
        l.TaxableIncome = Math.Max(0, l.Gross - l.NonTaxAllowance - otExempt - l.EmpTotal - l.FamilyDeduction);
        l.Pit = Pit(l.TaxableIncome, r.Brackets);
        l.Net = l.Gross - l.EmpTotal - l.Pit;
    }

    public async Task<PayrollRun> CreateAsync(int year, int month, int? branchId)
    {
        if (await _db.PayrollRuns.AnyAsync(p => p.Year == year && p.Month == month && p.BranchId == branchId && p.Status != DocStatus.Cancelled))
            throw new BusinessException($"Đã có bảng lương tháng {month:00}/{year} cho chi nhánh này.");
        var r = await RatesAsync();
        var run = new PayrollRun { Year = year, Month = month, BranchId = branchId, StandardDays = r.StdDays, CreatedById = _user.Id };
        await FillAsync(run, r);
        if (run.Lines.Count == 0) throw new BusinessException("Không có nhân viên đang làm việc để tính lương.");
        _db.PayrollRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    private async Task FillAsync(PayrollRun run, Rates r)
    {
        var emps = await _db.Employees.Where(e => e.IsActive && (run.BranchId == null || e.BranchId == run.BranchId)).OrderBy(e => e.Code).ToListAsync();
        var att = await _db.Attendances.Where(a => a.Year == run.Year && a.Month == run.Month).ToDictionaryAsync(a => a.EmployeeId);
        foreach (var e in emps)
        {
            var a = att.GetValueOrDefault(e.Id);
            var l = new PayrollLine();
            Compute(l, e, a?.WorkDays ?? run.StandardDays, a?.OvertimeHours ?? 0, run.StandardDays, r);
            run.Lines.Add(l);
        }
    }

    public async Task RecalculateAsync(PayrollRun run)
    {
        if (run.Status != DocStatus.Draft) throw new BusinessException("Chỉ tính lại bảng lương nháp.");
        if (await _wf.IsPendingAsync(ApprovalDocType.Payroll, run.Id)) throw new BusinessException("Bảng lương đang chờ duyệt.");
        _db.PayrollLines.RemoveRange(run.Lines.ToList()); run.Lines.Clear();
        await FillAsync(run, await RatesAsync());
        await _db.SaveChangesAsync();
    }

    /// <summary>Gửi duyệt (nếu có quy tắc) hoặc hạch toán ngay</summary>
    public async Task<string> SubmitAsync(PayrollRun run)
    {
        if (run.Status != DocStatus.Draft) throw new BusinessException("Bảng lương đã hạch toán hoặc đã hủy.");
        if (!await _wf.IsApprovedAsync(ApprovalDocType.Payroll, run.Id))
        {
            if (await _wf.IsPendingAsync(ApprovalDocType.Payroll, run.Id)) throw new PendingApprovalException("Bảng lương đang chờ duyệt.");
            var req = await _wf.StartAsync(ApprovalDocType.Payroll, run.Id, $"BL{run.Month:00}{run.Year % 100:00}", run.TotalCost, $"Bảng lương tháng {run.Month:00}/{run.Year} – {run.Lines.Count} nhân viên", run.BranchId);
            if (req != null) throw new PendingApprovalException($"Bảng lương tháng {run.Month:00}/{run.Year} đã gửi duyệt – {WorkflowService.Describe(req)}. Sẽ tự hạch toán khi được duyệt.");
        }
        await PostAsync(run);
        return $"Đã hạch toán bảng lương tháng {run.Month:00}/{run.Year}.";
    }

    public async Task PostAsync(PayrollRun run)
    {
        if (run.Status != DocStatus.Draft) throw new BusinessException("Bảng lương đã hạch toán hoặc đã hủy.");
        if (run.Lines.Count == 0) await _db.Entry(run).Collection(r => r.Lines).LoadAsync();
        var date = new DateTime(run.Year, run.Month, 1).AddMonths(1).AddDays(-1);
        async Task<int> A(string code)
        {
            var a = await _acc.AccAsync(code);
            if (!a.IsPostable) throw new BusinessException($"TK {code} là tài khoản tổng hợp – chọn TK chi tiết cho nhân viên / cài đặt lương.");
            return a.Id;
        }
        int a334 = await A("3341"), a3383 = await A("3383"), a3384 = await A("3384"), a3386 = await A("3386"), a3382 = await A("3382"), a3335 = await A("3335");
        var lines = new List<JLine>();
        foreach (var g in run.Lines.GroupBy(l => (l.ExpenseAccount, l.Department)))
        {
            var exp = await A(g.Key.ExpenseAccount);
            lines.Add(new JLine(exp, g.Sum(x => x.Gross), 0, null, "Lương " + g.Key.Department, g.Key.Department));
            lines.Add(new JLine(exp, g.Sum(x => x.ErTotal), 0, null, "BHXH, BHYT, BHTN, KPCĐ doanh nghiệp chịu – " + g.Key.Department, g.Key.Department));
        }
        decimal S(Func<PayrollLine, decimal> f) => run.Lines.Sum(f);
        lines.Add(new JLine(a334, 0, S(l => l.Gross), null, "Phải trả người lao động"));
        lines.Add(new JLine(a334, S(l => l.EmpTotal) + S(l => l.Pit), 0, null, "Khấu trừ bảo hiểm, thuế TNCN vào lương"));
        lines.Add(new JLine(a3383, 0, S(l => l.ErBhxh + l.EmpBhxh), null, "BHXH"));
        lines.Add(new JLine(a3384, 0, S(l => l.ErBhyt + l.EmpBhyt), null, "BHYT"));
        lines.Add(new JLine(a3386, 0, S(l => l.ErBhtn + l.EmpBhtn), null, "BHTN"));
        lines.Add(new JLine(a3382, 0, S(l => l.ErKpcd), null, "KPCĐ"));
        lines.Add(new JLine(a3335, 0, S(l => l.Pit), null, "Thuế TNCN khấu trừ"));
        var je = await _acc.PostAsync(date, $"Hạch toán tiền lương tháng {run.Month:00}/{run.Year}", JournalSource.Payroll, $"BL{run.Month:00}{run.Year % 100:00}", run.Id, lines);
        run.JournalEntryId = je.Id; run.Status = DocStatus.Posted;
        await _db.SaveChangesAsync();
    }

    public async Task CancelAsync(PayrollRun run, string reason)
    {
        if (run.Status == DocStatus.Cancelled) throw new BusinessException("Bảng lương đã hủy.");
        if (run.Status == DocStatus.Posted) await _acc.ReverseAsync(run.JournalEntryId, DateTime.Today, $"Hủy bảng lương tháng {run.Month:00}/{run.Year}: {reason}");
        run.Status = DocStatus.Cancelled; run.Note = $"{run.Note} [Hủy: {reason}]".Trim();
        await _db.SaveChangesAsync();
    }
}
