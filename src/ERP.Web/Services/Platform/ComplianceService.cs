using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Chỉ tiêu tờ khai khấu trừ thuế TNCN mẫu 05/KK-TNCN (TT 80/2021) – toàn bộ người lao động coi là cá nhân cư trú</summary>
public record Pit05(int Year, string Kind, int Period, DateTime From, DateTime To,
    int C21, int C22, int C23, int C24, int C25, decimal C26, decimal C27, decimal C28, decimal C29, decimal C30, decimal C31, decimal C32, decimal C33, decimal C34, decimal C35, decimal C36,
    List<Pit05Row> Rows, int Runs);
public record Pit05Row(Employee Employee, decimal Taxable, decimal Pit, int Months);

/// <summary>Dòng danh sách D02-LT báo tăng / giảm / điều chỉnh đóng BHXH, BHYT, BHTN (QĐ 595/QĐ-BHXH, sửa đổi QĐ 490)</summary>
public record D02Row(string Group, string Plan, Employee Employee, decimal Salary, DateTime FromMonth, DateTime? ToMonth, string Note);

/// <summary>Dòng đề nghị hưởng chế độ ốm đau / thai sản (mẫu 01B-HSB, QĐ 166/QĐ-BHXH)</summary>
public record BenefitRow(LeaveRequest Request, BenefitKind Kind, decimal BaseSalary, decimal Days, decimal Rate, decimal Amount, decimal LumpSum, string Basis);

/// <summary>Báo cáo tuân thủ: thuế TNCN, BHXH, chế độ ốm đau / thai sản, tạm ứng và chi lương qua ngân hàng</summary>
public class ComplianceService
{
    private readonly ErpDbContext _db;
    private readonly SettingsService _set;
    private readonly AccountingService _acc;
    public ComplianceService(ErpDbContext db, SettingsService set, AccountingService acc) { _db = db; _set = set; _acc = acc; }

    public static (DateTime From, DateTime To) Range(int year, string kind, int period) =>
        kind == "Q" ? (new DateTime(year, (period - 1) * 3 + 1, 1), new DateTime(year, (period - 1) * 3 + 1, 1).AddMonths(3).AddDays(-1))
                    : (new DateTime(year, period, 1), new DateTime(year, period, 1).AddMonths(1).AddDays(-1));

    /// <summary>Thu nhập chịu thuế (TNCT) của một dòng lương: tổng thu nhập − khoản không chịu thuế − phần tiền làm thêm giờ được miễn</summary>
    public static decimal Tnct(PayrollLine l) => l.Gross - l.NonTaxAllowance - l.ExtraNonTax - Math.Round(l.Overtime / 1.5m * 0.5m, 0);

    // ------------------------------------------------------------------ 05/KK-TNCN
    public async Task<Pit05> Pit05Async(int year, string kind, int period)
    {
        var (from, to) = Range(year, kind, period);
        var lines = await _db.PayrollLines.Include(l => l.Employee).Include(l => l.PayrollRun)
            .Where(l => l.PayrollRun!.Status == DocStatus.Posted && l.PayrollRun.Year == year && l.PayrollRun.Month >= from.Month && l.PayrollRun.Month <= to.Month).ToListAsync();
        var rows = lines.GroupBy(l => l.EmployeeId).Select(g => new Pit05Row(g.First().Employee!, g.Sum(Tnct), g.Sum(l => l.Pit), g.Select(l => l.PayrollRun!.Month).Distinct().Count()))
            .OrderBy(r => r.Employee.Code).ToList();
        var withheld = rows.Where(r => r.Pit > 0).ToList();
        int c21 = rows.Count, c24 = withheld.Count;
        decimal c27 = rows.Sum(r => r.Taxable), c32 = withheld.Sum(r => r.Taxable), c35 = rows.Sum(r => r.Pit);
        return new Pit05(year, kind, period, from, to, c21, c21, c24, c24, 0, c27, c27, 0, 0, 0, c32, c32, 0, c35, c35, 0, rows, lines.Select(l => l.PayrollRunId).Distinct().Count());
    }

    /// <summary>XML tờ khai theo bố cục hồ sơ thuế điện tử (HSoThueDTu / TTinChung / CTieuTKhaiChinh). Cần kiểm tra lại bằng HTKK / eTax trước khi nộp.</summary>
    public static string Pit05Xml(Pit05 d, ErpSettings c)
    {
        string N(decimal v) => v.ToString("0", CultureInfo.InvariantCulture);
        var x = new XElement("HSoThueDTu",
            new XElement("TTinChung",
                new XElement("maTKhai", "953"), new XElement("tenTKhai", "TỜ KHAI KHẤU TRỪ THUẾ THU NHẬP CÁ NHÂN (Mẫu số 05/KK-TNCN)"),
                new XElement("kieuKy", d.Kind), new XElement("kyKKhai", d.Kind == "Q" ? $"{d.Period}/{d.Year}" : $"{d.Period:00}/{d.Year}"),
                new XElement("kyKKhaiTuNgay", d.From.ToString("dd/MM/yyyy")), new XElement("kyKKhaiDenNgay", d.To.ToString("dd/MM/yyyy")),
                new XElement("mst", c.CompanyTaxCode), new XElement("tenNNT", c.CompanyName), new XElement("dchiNNT", c.CompanyAddress), new XElement("loaiTKhai", "C")),
            new XElement("CTieuTKhaiChinh",
                new XElement("ct21", d.C21), new XElement("ct22", d.C22), new XElement("ct23", d.C23), new XElement("ct24", d.C24), new XElement("ct25", d.C25),
                new XElement("ct26", N(d.C26)), new XElement("ct27", N(d.C27)), new XElement("ct28", N(d.C28)), new XElement("ct29", N(d.C29)), new XElement("ct30", N(d.C30)),
                new XElement("ct31", N(d.C31)), new XElement("ct32", N(d.C32)), new XElement("ct33", N(d.C33)), new XElement("ct34", N(d.C34)), new XElement("ct35", N(d.C35)), new XElement("ct36", N(d.C36))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), x).Declaration + Environment.NewLine + x;
    }

    /// <summary>XML quyết toán thuế TNCN mẫu 05/QTT-TNCN kèm phụ lục 05-1/BK-QTT-TNCN (cá nhân cư trú có hợp đồng lao động)</summary>
    public static string Pit05QttXml(int year, IEnumerable<(Employee E, decimal Gross, decimal Taxable, decimal Insurance, decimal Family, decimal AnnualTax, decimal Withheld)> rows, ErpSettings c)
    {
        string N(decimal v) => v.ToString("0", CultureInfo.InvariantCulture);
        var list = rows.ToList();
        var pl = new XElement("PL_05_1_BK_QTT", list.Select((r, i) => new XElement("BangKeChiTiet",
            new XElement("stt", i + 1), new XElement("hoTen", r.E.FullName), new XElement("mstCaNhan", r.E.PersonalTaxCode), new XElement("soCMND", r.E.IdNumber),
            new XElement("tongTNCT", N(r.Gross)), new XElement("soNguoiPhuThuoc", r.E.Dependents), new XElement("tongGiamTruGiaCanh", N(r.Family)),
            new XElement("baoHiem", N(r.Insurance)), new XElement("tongTNTT", N(r.Taxable)), new XElement("soThueDaKhauTru", N(r.Withheld)),
            new XElement("soThuePhaiKhauTru", N(r.AnnualTax)), new XElement("soThueConPhaiNop", N(Math.Max(0, r.AnnualTax - r.Withheld))), new XElement("soThueNopThua", N(Math.Max(0, r.Withheld - r.AnnualTax))))));
        var x = new XElement("HSoThueDTu",
            new XElement("TTinChung", new XElement("maTKhai", "954"), new XElement("tenTKhai", "TỜ KHAI QUYẾT TOÁN THUẾ THU NHẬP CÁ NHÂN (Mẫu số 05/QTT-TNCN)"),
                new XElement("kieuKy", "Y"), new XElement("kyKKhai", year), new XElement("mst", c.CompanyTaxCode), new XElement("tenNNT", c.CompanyName)),
            new XElement("CTieuTKhaiChinh",
                new XElement("ct16", list.Count), new XElement("ct17", list.Count), new XElement("ct20", list.Count(r => r.AnnualTax > 0)),
                new XElement("ct23", N(list.Sum(r => r.Gross))), new XElement("ct24", N(list.Sum(r => r.Gross))), new XElement("ct27", N(list.Where(r => r.AnnualTax > 0).Sum(r => r.Gross))),
                new XElement("ct30", N(list.Sum(r => r.Withheld))), new XElement("ct31", N(list.Sum(r => r.Withheld)))),
            pl);
        return new XDeclaration("1.0", "utf-8", null) + Environment.NewLine + x;
    }

    // ------------------------------------------------------------------ D02-LT
    public async Task<List<D02Row>> D02Async(int year, int month)
    {
        var from = new DateTime(year, month, 1); var to = from.AddMonths(1);
        var rows = new List<D02Row>();
        var emps = await _db.Employees.ToListAsync();
        foreach (var e in emps.Where(e => e.StartDate >= from && e.StartDate < to))
            rows.Add(new D02Row("I. Tăng", "TM", e, e.InsuranceSalary, from, null, "Tăng mới – vào làm " + e.StartDate.ToString("dd/MM/yyyy")));
        foreach (var e in emps.Where(e => e.ResignDate >= from && e.ResignDate < to))
            rows.Add(new D02Row("II. Giảm", "GH", e, e.InsuranceSalary, from, null, "Giảm hẳn – nghỉ việc " + e.ResignDate!.Value.ToString("dd/MM/yyyy")));
        var changes = await _db.EmployeeHistories.Include(h => h.Employee).Where(h => h.Kind == HistoryKind.SalaryChange && h.Note == "Lương đóng BHXH" && h.Date >= from && h.Date < to).ToListAsync();
        foreach (var h in changes)
            rows.Add(new D02Row("I. Tăng", "DC", h.Employee!, h.Employee!.InsuranceSalary, from, null, $"Điều chỉnh mức đóng: {h.FromValue} → {h.ToValue}"));
        // Nghỉ thai sản, nghỉ không lương từ 14 ngày làm việc trở lên trong tháng: báo giảm tạm (không đóng BHXH tháng đó)
        var leaves = await _db.LeaveRequests.Include(l => l.Employee).Include(l => l.LeaveType)
            .Where(l => l.Status == LeaveStatus.Approved && l.FromDate < to && l.ToDate >= from).ToListAsync();
        foreach (var g in leaves.Where(l => l.LeaveType!.BenefitKind == BenefitKind.Maternity && l.FromDate >= from).GroupBy(l => l.EmployeeId))
            rows.Add(new D02Row("II. Giảm", "TS", g.First().Employee!, g.First().Employee!.InsuranceSalary, from, g.Max(l => l.ToDate), "Nghỉ thai sản từ " + g.Min(l => l.FromDate).ToString("dd/MM/yyyy")));
        foreach (var g in leaves.Where(l => !l.LeaveType!.IsPaid && l.LeaveType.BenefitKind == BenefitKind.None).GroupBy(l => l.EmployeeId))
        {
            var days = g.Sum(l => l.Days);
            if (days >= 14) rows.Add(new D02Row("II. Giảm", "KL", g.First().Employee!, g.First().Employee!.InsuranceSalary, from, from.AddMonths(1).AddDays(-1), $"Nghỉ không lương {days:0.#} ngày trong tháng"));
        }
        return rows.OrderBy(r => r.Group).ThenBy(r => r.Plan).ThenBy(r => r.Employee.Code).ToList();
    }

    // ------------------------------------------------------------------ chế độ ốm đau / thai sản
    public async Task<List<BenefitRow>> BenefitsAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1); var to = from.AddMonths(1);
        var baseGov = await _set.DecAsync("Payroll.BaseSalaryGov", 2_340_000);
        var sickRate = await _set.DecAsync("Payroll.SickBenefitRate", 75);
        var reqs = await _db.LeaveRequests.Include(l => l.Employee).Include(l => l.LeaveType)
            .Where(l => l.Status == LeaveStatus.Approved && l.LeaveType!.BenefitKind != BenefitKind.None && l.FromDate >= from && l.FromDate < to).OrderBy(l => l.FromDate).ToListAsync();
        var rows = new List<BenefitRow>();
        foreach (var r in reqs)
        {
            var e = r.Employee!;
            var ins = e.InsuranceSalary > 0 ? e.InsuranceSalary : e.BaseSalary;
            if (r.LeaveType!.BenefitKind == BenefitKind.Sickness)
            {
                var amt = Math.Round(ins / 24m * r.Days * sickRate / 100m, 0);
                rows.Add(new BenefitRow(r, BenefitKind.Sickness, ins, r.Days, sickRate, amt, 0, $"{sickRate:0}% × {ins:N0} / 24 ngày × {r.Days:0.#} ngày"));
            }
            else
            {
                var months = Math.Round((decimal)((r.ToDate - r.FromDate).TotalDays + 1) / 30m, 2);
                var amt = Math.Round(ins * months, 0);
                rows.Add(new BenefitRow(r, BenefitKind.Maternity, ins, months, 100, amt, 2 * baseGov, $"100% × {ins:N0} × {months:0.##} tháng + trợ cấp một lần 2 × {baseGov:N0}"));
            }
        }
        return rows;
    }

    // ------------------------------------------------------------------ tạm ứng lương
    public async Task PayAdvanceAsync(SalaryAdvance a)
    {
        if (a.Status != AdvanceStatus.Draft) throw new BusinessException("Phiếu tạm ứng đã chi hoặc đã hủy.");
        var cash = await _db.CashAccounts.FindAsync(a.CashAccountId) ?? throw new BusinessException("Chọn quỹ / tài khoản chi.");
        if (await _db.PayrollRuns.AnyAsync(p => p.Year == a.Year && p.Month == a.Month && p.Status == DocStatus.Posted && (p.BranchId == null || p.BranchId == a.Employee!.BranchId)))
            throw new BusinessException($"Bảng lương tháng {a.Month:00}/{a.Year} đã hạch toán – chọn kỳ trừ lương sau.");
        var e = a.Employee ?? await _db.Employees.FindAsync(a.EmployeeId);
        var a334 = await _acc.AccAsync("3341");
        var je = await _acc.PostAsync(a.Date, $"Tạm ứng lương {e!.Code} {e.FullName} – trừ lương {a.Month:00}/{a.Year}", JournalSource.Payroll, a.Number, a.Id,
            new[] { new JLine(a334.Id, a.Amount, 0, null, "Tạm ứng lương " + e.FullName), new JLine(cash.AccountId, 0, a.Amount, null, "Chi tạm ứng lương") });
        a.JournalEntryId = je.Id; a.Status = AdvanceStatus.Paid;
        await _db.SaveChangesAsync();
    }

    public async Task CancelAdvanceAsync(SalaryAdvance a, string reason)
    {
        if (a.Status == AdvanceStatus.Cancelled) throw new BusinessException("Phiếu đã hủy.");
        if (a.Status == AdvanceStatus.Paid)
        {
            if (await _db.PayrollRuns.AnyAsync(p => p.Year == a.Year && p.Month == a.Month && p.Status == DocStatus.Posted))
                throw new BusinessException("Đã trừ vào bảng lương đã hạch toán – không hủy được.");
            await _acc.ReverseAsync(a.JournalEntryId, DateTime.Today, reason);
        }
        a.Status = AdvanceStatus.Cancelled; a.Note = $"{a.Note} [Hủy: {reason}]".Trim();
        await _db.SaveChangesAsync();
    }
}
