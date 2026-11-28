using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;
using static ERP.Web.Services.Reporting.ReportExport;

namespace ERP.Web.Controllers;

/// <summary>Tuân thủ pháp lý nhân sự – thuế: tờ khai 05/KK-TNCN, D02-LT, chế độ ốm đau / thai sản, tạm ứng lương, chi lương qua ngân hàng</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class ComplianceController : ErpControllerBase
{
    private readonly ComplianceService _svc; private readonly ErpSettings _cfg;
    public ComplianceController(ErpDbContext db, ComplianceService svc, IOptions<ErpSettings> cfg) : base(db) { _svc = svc; _cfg = cfg.Value; }
    private bool CanEdit => User.InAny(AppRoles.Accounting);
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // ------------------------------------------------------------------ 05/KK-TNCN
    public async Task<IActionResult> Pit05(int? year, string kind = "M", int? period = null, string? format = null)
    {
        var y = year ?? DateTime.Today.Year; kind = kind == "Q" ? "Q" : "M";
        var p = period ?? (kind == "Q" ? (DateTime.Today.Month - 1) / 3 + 1 : DateTime.Today.Month);
        p = Math.Clamp(p, 1, kind == "Q" ? 4 : 12);
        var d = await _svc.Pit05Async(y, kind, p);
        var tag = kind == "Q" ? $"Q{p}_{y}" : $"{p:00}_{y}";
        if (format == "xml") return File(System.Text.Encoding.UTF8.GetBytes(ComplianceService.Pit05Xml(d, _cfg)), "application/xml", $"05KK_TNCN_{tag}.xml");
        if (format == "excel")
            return File(ReportExport.Xlsx("05KK-TNCN", "TỜ KHAI KHẤU TRỪ THUẾ TNCN (05/KK-TNCN)", new[] { _cfg.CompanyName, $"MST {_cfg.CompanyTaxCode} · Kỳ {(kind == "Q" ? "quý" : "tháng")} {p}/{y}" },
                new List<Col> { new("Mã NV", FieldKind.Text), new("Họ tên", FieldKind.Text), new("MST cá nhân", FieldKind.Text), new("Số tháng", FieldKind.Number), new("TNCT", FieldKind.Money), new("Thuế đã khấu trừ", FieldKind.Money) },
                d.Rows.Select(r => new object?[] { r.Employee.Code, r.Employee.FullName, r.Employee.PersonalTaxCode, (decimal)r.Months, r.Taxable, r.Pit }),
                new object?[] { "Cộng", null, null, null, d.C27, d.C35 }), Xlsx, $"05KK_TNCN_{tag}.xlsx");
        ViewBag.Year = y; ViewBag.Kind = kind; ViewBag.Period = p;
        return View(d);
    }

    // ------------------------------------------------------------------ D02-LT
    public async Task<IActionResult> D02(int? year, int? month, bool excel = false)
    {
        var y = year ?? DateTime.Today.Year; var m = month ?? DateTime.Today.Month;
        var rows = await _svc.D02Async(y, m);
        if (excel)
            return File(ReportExport.Xlsx("D02-LT", "DANH SÁCH LAO ĐỘNG THAM GIA BHXH, BHYT, BHTN (D02-LT)", new[] { _cfg.CompanyName, $"Tháng {m:00}/{y}" },
                new List<Col> { new("Nhóm", FieldKind.Text), new("Họ và tên", FieldKind.Text), new("Mã số BHXH", FieldKind.Text), new("Ngày sinh", FieldKind.Date), new("Giới tính", FieldKind.Text),
                    new("Chức vụ / chức danh", FieldKind.Text), new("Mức lương đóng", FieldKind.Money), new("Từ tháng", FieldKind.Text), new("Đến tháng", FieldKind.Text), new("Phương án", FieldKind.Text), new("Ghi chú", FieldKind.Text) },
                rows.Select(r => new object?[] { r.Group, r.Employee.FullName, r.Employee.InsuranceBookNo, r.Employee.DateOfBirth, r.Employee.Gender?.GetDisplayName(), r.Employee.Position, r.Salary,
                    r.FromMonth.ToString("MM/yyyy"), r.ToMonth?.ToString("MM/yyyy"), r.Plan, r.Note }), null), Xlsx, $"D02LT_{m:00}_{y}.xlsx");
        ViewBag.Year = y; ViewBag.Month = m;
        return View(rows);
    }

    // ------------------------------------------------------------------ ốm đau, thai sản
    public async Task<IActionResult> Benefits(int? year, int? month, bool excel = false)
    {
        var y = year ?? DateTime.Today.Year; var m = month ?? DateTime.Today.Month;
        var rows = await _svc.BenefitsAsync(y, m);
        if (excel)
            return File(ReportExport.Xlsx("01B-HSB", "DANH SÁCH ĐỀ NGHỊ GIẢI QUYẾT HƯỞNG CHẾ ĐỘ ỐM ĐAU, THAI SẢN (01B-HSB)", new[] { _cfg.CompanyName, $"Tháng {m:00}/{y} – số tiền là ước tính, cơ quan BHXH xét duyệt" },
                new List<Col> { new("Họ và tên", FieldKind.Text), new("Mã số BHXH", FieldKind.Text), new("Chế độ", FieldKind.Text), new("Từ ngày", FieldKind.Date), new("Đến ngày", FieldKind.Date),
                    new("Số ngày / tháng", FieldKind.Qty), new("Mức lương đóng", FieldKind.Money), new("Tỷ lệ (%)", FieldKind.Percent), new("Tiền hưởng", FieldKind.Money), new("Trợ cấp một lần", FieldKind.Money), new("Căn cứ", FieldKind.Text) },
                rows.Select(r => new object?[] { r.Request.Employee!.FullName, r.Request.Employee.InsuranceBookNo, r.Kind.GetDisplayName(), r.Request.FromDate, r.Request.ToDate, r.Days, r.BaseSalary, r.Rate, r.Amount, r.LumpSum, r.Basis }),
                new object?[] { "Cộng", null, null, null, null, null, null, null, rows.Sum(r => r.Amount), rows.Sum(r => r.LumpSum), null }), Xlsx, $"01B_HSB_{m:00}_{y}.xlsx");
        ViewBag.Year = y; ViewBag.Month = m;
        return View(rows);
    }

    // ------------------------------------------------------------------ tạm ứng lương
    public async Task<IActionResult> Advances(int? year, int? month)
    {
        var y = year ?? DateTime.Today.Year; var m = month ?? DateTime.Today.Month;
        ViewBag.Year = y; ViewBag.Month = m; ViewBag.CanEdit = CanEdit;
        ViewBag.Emps = new SelectList(await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.FullName }).ToListAsync(), "Id", "Name");
        ViewBag.Cash = new SelectList(await Db.CashAccounts.Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Id, Name = c.Code + " – " + c.Name }).ToListAsync(), "Id", "Name");
        return View(await Db.SalaryAdvances.Include(a => a.Employee).Include(a => a.CashAccount).Where(a => a.Year == y && a.Month == m).OrderByDescending(a => a.Date).ThenByDescending(a => a.Id).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAdvance(SalaryAdvance model, bool pay)
    {
        if (!CanEdit) return Denied();
        if (model.Amount <= 0) throw new BusinessException("Số tiền tạm ứng phải lớn hơn 0.");
        var e = await Db.Employees.FindAsync(model.EmployeeId) ?? throw new BusinessException("Chọn nhân viên.");
        var max = await Db.SalaryAdvances.Where(a => a.EmployeeId == e.Id && a.Year == model.Year && a.Month == model.Month && a.Status != AdvanceStatus.Cancelled).SumAsync(a => (decimal?)a.Amount) ?? 0;
        var limitPct = await HttpContext.RequestServices.GetRequiredService<SettingsService>().DecAsync("Payroll.AdvanceMaxPct", 50);
        if (max + model.Amount > e.BaseSalary * limitPct / 100) throw new BusinessException($"Tổng tạm ứng trong kỳ vượt {limitPct:0}% lương cơ bản ({(e.BaseSalary * limitPct / 100).ToMoney()} đ).");
        var a = new SalaryAdvance { EmployeeId = e.Id, Employee = e, Date = model.Date.Date, Amount = model.Amount, Year = model.Year, Month = model.Month, CashAccountId = model.CashAccountId, Note = model.Note,
            Number = await DocNumbers.NextAsync(Db.SalaryAdvances.Select(x => x.Number), "TU", model.Date) };
        Db.SalaryAdvances.Add(a);
        await Db.SaveChangesAsync();
        if (pay) await _svc.PayAdvanceAsync(a);
        Success(pay ? $"Đã chi tạm ứng {a.Number} và ghi sổ Nợ 334 / Có tiền." : $"Đã lập phiếu tạm ứng {a.Number}.");
        return RedirectToAction(nameof(Advances), new { year = a.Year, month = a.Month });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PayAdvance(int id, int? cashAccountId)
    {
        if (!CanEdit) return Denied();
        var a = await Db.SalaryAdvances.Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        if (cashAccountId != null) a.CashAccountId = cashAccountId;
        await _svc.PayAdvanceAsync(a);
        Success($"Đã chi tạm ứng {a.Number}.");
        return RedirectToAction(nameof(Advances), new { year = a.Year, month = a.Month });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelAdvance(int id, string? reason)
    {
        if (!CanEdit) return Denied();
        var a = await Db.SalaryAdvances.FindAsync(id);
        if (a == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do hủy.");
        await _svc.CancelAdvanceAsync(a, reason);
        Success($"Đã hủy tạm ứng {a.Number}" + (a.JournalEntryId != null ? " và đảo bút toán." : "."));
        return RedirectToAction(nameof(Advances), new { year = a.Year, month = a.Month });
    }

    // ------------------------------------------------------------------ chi lương qua ngân hàng
    /// <summary>Tệp chi lương hàng loạt: Excel / CSV theo cột chung các ngân hàng chấp nhận (STT, tên, số TK, ngân hàng, số tiền, nội dung)</summary>
    public async Task<IActionResult> BankFile(int runId, string format = "xlsx")
    {
        var run = await Db.PayrollRuns.Include(r => r.Lines).ThenInclude(l => l.Employee).FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null) return NotFound();
        var content = $"Luong thang {run.Month:00}/{run.Year}";
        var lines = run.Lines.Where(l => l.Net > 0).OrderBy(l => l.Employee!.Code).ToList();
        var missing = lines.Where(l => string.IsNullOrWhiteSpace(l.Employee!.BankAccountNo)).Select(l => l.Employee!.Code).ToList();
        var rows = lines.Select((l, i) => new object?[] { (decimal)(i + 1), Unaccent(l.Employee!.FullName).ToUpperInvariant(), l.Employee.BankAccountNo, l.Employee.BankName, l.Net, content, l.Employee.Code }).ToList();
        var name = $"ChiLuong_{run.Month:00}_{run.Year}";
        if (format == "csv")
        {
            var cols = new List<Col> { new("STT", FieldKind.Number), new("Ten nguoi nhan", FieldKind.Text), new("So tai khoan", FieldKind.Text), new("Ngan hang", FieldKind.Text), new("So tien", FieldKind.Number), new("Noi dung", FieldKind.Text), new("Ma NV", FieldKind.Text) };
            return File(ReportExport.Csv(cols, rows, null), "text/csv; charset=utf-8", name + ".csv");
        }
        var sub = new List<string> { _cfg.CompanyName, $"Tổng {lines.Count} người · {lines.Sum(l => l.Net).ToMoney()} đ" };
        if (missing.Count > 0) sub.Add("Thiếu số tài khoản: " + string.Join(", ", missing));
        return File(ReportExport.Xlsx("ChiLuong", $"DANH SÁCH CHI LƯƠNG THÁNG {run.Month:00}/{run.Year}", sub,
            new List<Col> { new("STT", FieldKind.Number), new("Tên người nhận", FieldKind.Text), new("Số tài khoản", FieldKind.Text), new("Ngân hàng", FieldKind.Text), new("Số tiền", FieldKind.Money), new("Nội dung", FieldKind.Text), new("Mã NV", FieldKind.Text) },
            rows, new object?[] { null, "Cộng", null, null, lines.Sum(l => l.Net), null, null }), Xlsx, name + ".xlsx");
    }

    /// <summary>Bỏ dấu tiếng Việt (tên người nhận trong tệp ngân hàng thường yêu cầu không dấu)</summary>
    public static string Unaccent(string s)
    {
        var n = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(System.Text.NormalizationForm.FormD);
        return new string(n.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).Normalize(System.Text.NormalizationForm.FormC);
    }
}
