using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Trình tính công thức lương: số, biến, + − * /, ngoặc, so sánh (&gt; &lt; &gt;= &lt;= = &lt;&gt;), hàm MIN, MAX, ROUND, IF(đk; a; b)</summary>
public static class FormulaEvaluator
{
    public static readonly string[] Variables = { "BaseSalary", "WorkDays", "StdDays", "OvertimeHours", "Dependents", "Years", "InsuranceSalary", "SalaryByDays", "Allowance" };

    public static decimal Eval(string expr, IReadOnlyDictionary<string, decimal> vars)
    {
        var p = new Parser(expr, vars);
        var v = p.ParseCompare();
        p.End();
        return v;
    }

    /// <summary>Kiểm tra công thức hợp lệ (dùng giá trị 1 cho mọi biến); trả về thông báo lỗi hoặc null</summary>
    public static string? Validate(string expr)
    {
        try { Eval(expr, Variables.ToDictionary(x => x, _ => 1m, StringComparer.OrdinalIgnoreCase)); return null; }
        catch (BusinessException ex) { return ex.Message; }
        catch (DivideByZeroException) { return null; }
    }

    private class Parser
    {
        private readonly string _s; private int _i; private readonly IReadOnlyDictionary<string, decimal> _vars;
        public Parser(string s, IReadOnlyDictionary<string, decimal> vars) { _s = s; _vars = new Dictionary<string, decimal>(vars, StringComparer.OrdinalIgnoreCase); }
        private void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private bool Eat(string t) { Ws(); if (string.CompareOrdinal(_s, _i, t, 0, t.Length) == 0) { _i += t.Length; return true; } return false; }
        public void End() { Ws(); if (_i < _s.Length) throw new BusinessException($"Công thức không hợp lệ gần \"{_s[_i..Math.Min(_s.Length, _i + 8)]}\"."); }

        public decimal ParseCompare()
        {
            var a = ParseSum();
            foreach (var op in new[] { ">=", "<=", "<>", "!=", "==", ">", "<", "=" })
                if (Eat(op))
                {
                    var b = ParseSum();
                    return (op switch { ">=" => a >= b, "<=" => a <= b, "<>" or "!=" => a != b, ">" => a > b, "<" => a < b, _ => a == b }) ? 1 : 0;
                }
            return a;
        }
        private decimal ParseSum()
        {
            var v = ParseProd();
            while (true) { if (Eat("+")) v += ParseProd(); else if (Eat("-")) v -= ParseProd(); else return v; }
        }
        private decimal ParseProd()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Eat("*")) v *= ParseUnary();
                else if (Eat("/")) { var d = ParseUnary(); if (d == 0) throw new BusinessException("Công thức chia cho 0."); v /= d; }
                else return v;
            }
        }
        private decimal ParseUnary() => Eat("-") ? -ParseUnary() : ParseAtom();
        private decimal ParseAtom()
        {
            Ws();
            if (Eat("(")) { var v = ParseCompare(); if (!Eat(")")) throw new BusinessException("Công thức thiếu dấu \")\"."); return v; }
            if (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
            {
                var st = _i; while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                return decimal.Parse(_s[st.._i], CultureInfo.InvariantCulture);
            }
            if (_i < _s.Length && (char.IsLetter(_s[_i]) || _s[_i] == '_'))
            {
                var st = _i; while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                var name = _s[st.._i]; Ws();
                if (_i < _s.Length && _s[_i] == '(')
                {
                    _i++; var args = new List<decimal>();
                    Ws();
                    if (!Eat(")"))
                    {
                        do { args.Add(ParseCompare()); } while (Eat(";") || Eat(","));
                        if (!Eat(")")) throw new BusinessException("Công thức thiếu dấu \")\".");
                    }
                    return Call(name.ToUpperInvariant(), args);
                }
                if (_vars.TryGetValue(name, out var val)) return val;
                throw new BusinessException($"Biến \"{name}\" không có trong công thức. Dùng: {string.Join(", ", Variables)}.");
            }
            throw new BusinessException("Công thức không hợp lệ.");
        }
        private static decimal Call(string f, List<decimal> a) => f switch
        {
            "MIN" when a.Count >= 1 => a.Min(),
            "MAX" when a.Count >= 1 => a.Max(),
            "ROUND" when a.Count is 1 or 2 => Math.Round(a[0], a.Count == 2 ? (int)a[1] : 0),
            "IF" when a.Count == 3 => a[0] != 0 ? a[1] : a[2],
            _ => throw new BusinessException($"Hàm {f} không hợp lệ hoặc sai số tham số (MIN, MAX, ROUND, IF).")
        };
    }
}

public record LeaveBalance(Employee Employee, decimal Entitlement, decimal Used, decimal Pending)
{
    public decimal Remaining => Entitlement - Used;
}

/// <summary>Nhân sự: quá trình công tác, hợp đồng, chấm công ngày theo ca từ máy chấm công, nghỉ phép, thành phần lương</summary>
public class HrService
{
    private readonly ErpDbContext _db;
    private readonly CurrentUser _user;
    public HrService(ErpDbContext db, CurrentUser user) { _db = db; _user = user; }

    // ------------------------------------------------------------------ Hồ sơ
    public void LogHistory(int employeeId, HistoryKind kind, string? from, string? to, string? note, DateTime? date = null) =>
        _db.EmployeeHistories.Add(new EmployeeHistory { EmployeeId = employeeId, Kind = kind, FromValue = from, ToValue = to, Note = note, Date = (date ?? DateTime.Today).Date });

    public static decimal YearsOfService(Employee e, DateTime? at = null)
    {
        var end = e.ResignDate ?? at ?? DateTime.Today;
        return Math.Max(0, Math.Round((decimal)(end - e.StartDate).TotalDays / 365.25m, 2));
    }

    // ------------------------------------------------------------------ Hợp đồng
    /// <summary>Hợp đồng hết hạn → chuyển trạng thái; trả danh sách sắp hết hạn trong N ngày</summary>
    public async Task<List<LaborContract>> RefreshContractsAsync(int withinDays = 45)
    {
        var today = DateTime.Today;
        var expired = await _db.LaborContracts.Where(c => c.Status == LaborContractStatus.Active && c.EndDate != null && c.EndDate < today).ToListAsync();
        foreach (var c in expired) c.Status = LaborContractStatus.Expired;
        if (expired.Count > 0) await _db.SaveChangesAsync();
        return await _db.LaborContracts.Include(c => c.Employee).Where(c => c.Status == LaborContractStatus.Active && c.EndDate != null && c.EndDate <= today.AddDays(withinDays) && c.Employee!.IsActive).OrderBy(c => c.EndDate).ToListAsync();
    }

    public async Task<LaborContract> SaveContractAsync(LaborContract c)
    {
        if (c.EndDate != null && c.EndDate < c.StartDate) throw new BusinessException("Ngày hết hạn không trước ngày bắt đầu.");
        if (c.Type is LaborContractType.FixedTerm or LaborContractType.Seasonal or LaborContractType.Probation && c.EndDate == null) throw new BusinessException("Hợp đồng có thời hạn phải nhập ngày hết hạn.");
        if (c.Type == LaborContractType.Indefinite) c.EndDate = null;
        if (await _db.LaborContracts.AnyAsync(x => x.Number == c.Number && x.Id != c.Id)) throw new BusinessException("Số hợp đồng đã tồn tại.");
        var emp = await _db.Employees.FindAsync(c.EmployeeId) ?? throw new BusinessException("Chọn nhân viên.");
        // Hợp đồng mới hiệu lực thay thế hợp đồng đang hiệu lực trước đó
        if (c.Id == 0 && c.Status == LaborContractStatus.Active)
            foreach (var old in await _db.LaborContracts.Where(x => x.EmployeeId == c.EmployeeId && x.Status == LaborContractStatus.Active).ToListAsync()) old.Status = LaborContractStatus.Expired;
        if (c.Id == 0) { _db.LaborContracts.Add(c); LogHistory(emp.Id, HistoryKind.Other, null, c.Number, $"Ký hợp đồng lao động {c.Type.GetDisplayName()} từ {c.StartDate:dd/MM/yyyy}", c.SignedDate); }
        await _db.SaveChangesAsync();
        return c;
    }

    // ------------------------------------------------------------------ Chấm công theo ca
    private static int Minutes(DateTime t) => t.Hour * 60 + t.Minute;

    private static Shift? PickShift(IEnumerable<Shift> shifts, Employee e, DateTime checkIn)
    {
        var list = shifts.Where(s => s.IsActive).ToList();
        if (e.DefaultShiftId != null) { var d = list.FirstOrDefault(s => s.Id == e.DefaultShiftId); if (d != null) return d; }
        return list.OrderBy(s => Math.Abs(s.StartMinute - Minutes(checkIn))).FirstOrDefault();
    }

    /// <summary>Xử lý lượt quẹt thô thành chấm công ngày: giờ vào = lượt đầu, giờ ra = lượt cuối; tính đi trễ, về sớm, giờ làm và giờ làm thêm theo ca</summary>
    public async Task<(int Days, int Punches)> ProcessPunchesAsync(DateTime? from = null, DateTime? to = null)
    {
        var q = _db.TimeClockPunches.Where(p => !p.Processed);
        if (from != null) q = q.Where(p => p.PunchTime >= from);
        if (to != null) q = q.Where(p => p.PunchTime < to.Value.Date.AddDays(1));
        var punches = await q.OrderBy(p => p.PunchTime).ToListAsync();
        if (punches.Count == 0) return (0, 0);
        var emps = await _db.Employees.IgnoreQueryFilters().ToDictionaryAsync(e => e.Code, StringComparer.OrdinalIgnoreCase);
        var shifts = await _db.Shifts.ToListAsync();
        var days = 0;
        foreach (var g in punches.GroupBy(p => (p.EmployeeCode, p.PunchTime.Date)))
        {
            if (!emps.TryGetValue(g.Key.EmployeeCode, out var e)) { foreach (var p in g) p.Processed = true; continue; }
            var date = g.Key.Date; var ins = g.Min(p => p.PunchTime); var outs = g.Max(p => p.PunchTime);
            var day = await _db.AttendanceDays.FirstOrDefaultAsync(d => d.EmployeeId == e.Id && d.Date == date);
            if (day != null && day.Source == "Nhập tay") { foreach (var p in g) p.Processed = true; continue; }   // dữ liệu nhập tay không bị máy ghi đè
            if (day == null) { day = new AttendanceDay { EmployeeId = e.Id, Date = date }; _db.AttendanceDays.Add(day); }
            var sh = PickShift(shifts, e, ins);
            day.ShiftId = sh?.Id; day.Kind = AttendanceKind.Present; day.CheckIn = ins; day.Source = "Máy chấm công";
            if (outs > ins.AddMinutes(30))
            {
                day.CheckOut = outs;
                var gross = (decimal)(outs - ins).TotalHours;
                var breakH = (sh?.BreakMinutes ?? 60) / 60m;
                var worked = Math.Max(0, gross - (gross > 5 ? breakH : 0));
                var paid = sh?.PaidHours ?? 8m;
                day.WorkedHours = Math.Round(worked, 2);
                day.OvertimeHours = Math.Max(0, Math.Round(Math.Floor((worked - paid) * 2) / 2m, 1));   // làm thêm tính theo nửa giờ
                if (sh != null)
                {
                    day.LateMinutes = Math.Max(0, Minutes(ins) - sh.StartMinute - sh.GraceMinutes);
                    day.EarlyLeaveMinutes = sh.IsNight ? 0 : Math.Max(0, sh.EndMinute - Minutes(outs));
                }
                day.Note = null;
            }
            else { day.CheckOut = null; day.WorkedHours = 0; day.OvertimeHours = 0; day.Note = "Thiếu lượt quẹt ra"; }
            foreach (var p in g) p.Processed = true;
            days++;
        }
        await _db.SaveChangesAsync();
        return (days, punches.Count);
    }

    /// <summary>Tổng hợp công tháng (từ chấm công ngày + nghỉ phép đã duyệt) vào bảng chấm công tháng dùng tính lương</summary>
    public async Task<int> RollUpMonthAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1); var to = from.AddMonths(1).AddDays(-1);
        var days = await _db.AttendanceDays.Include(d => d.Shift).Where(d => d.Date >= from && d.Date <= to).ToListAsync();
        var leaves = await _db.LeaveRequests.Include(l => l.LeaveType).Where(l => l.Status == LeaveStatus.Approved && l.FromDate <= to && l.ToDate >= from).ToListAsync();
        var ids = days.Select(d => d.EmployeeId).Concat(leaves.Select(l => l.EmployeeId)).Distinct().ToList();
        var att = await _db.Attendances.Where(a => a.Year == year && a.Month == month).ToDictionaryAsync(a => a.EmployeeId);
        foreach (var id in ids)
        {
            decimal work = 0, ot = 0;
            foreach (var d in days.Where(x => x.EmployeeId == id && x.Kind == AttendanceKind.Present))
            {
                var paid = d.Shift?.PaidHours ?? 8m;
                var frac = paid <= 0 ? 1 : Math.Min(1m, d.WorkedHours / paid);
                work += d.CheckOut == null ? 0 : Math.Round(frac * 2, MidpointRounding.AwayFromZero) / 2m;
                ot += d.OvertimeHours;
            }
            decimal leaveDays = 0, paidLeave = 0;
            foreach (var l in leaves.Where(x => x.EmployeeId == id))
            {
                var dd = DaysInMonth(l, from, to);
                leaveDays += dd; if (l.LeaveType?.IsPaid == true) paidLeave += dd;
            }
            if (!att.TryGetValue(id, out var a)) { a = new Attendance { EmployeeId = id, Year = year, Month = month }; _db.Attendances.Add(a); }
            a.WorkDays = Math.Min(31, work + paidLeave); a.OvertimeHours = ot; a.LeaveDays = leaveDays; a.Source = "Tổng hợp chấm công ngày";
        }
        await _db.SaveChangesAsync();
        return ids.Count;
    }

    private static decimal DaysInMonth(LeaveRequest l, DateTime from, DateTime to)
    {
        var s = l.FromDate < from ? from : l.FromDate; var e = l.ToDate > to ? to : l.ToDate;
        var total = Enumerable.Range(0, Math.Max(0, (l.ToDate.Date - l.FromDate.Date).Days + 1)).Count(i => l.FromDate.Date.AddDays(i).DayOfWeek != DayOfWeek.Sunday);
        var inMonth = Enumerable.Range(0, Math.Max(0, (e.Date - s.Date).Days + 1)).Count(i => s.Date.AddDays(i).DayOfWeek != DayOfWeek.Sunday);
        return total == 0 ? 0 : Math.Round(l.Days * inMonth / total, 1);
    }

    // ------------------------------------------------------------------ Nghỉ phép
    public static decimal Entitlement(Employee e, int year)
    {
        var basic = e.AnnualLeaveDays + Math.Floor(YearsOfService(e, new DateTime(year, 12, 31)) / 5);   // thâm niên: +1 ngày mỗi 5 năm
        if (e.StartDate.Year == year) basic = Math.Floor(basic * (13 - e.StartDate.Month) / 12m);
        return Math.Max(0, basic);
    }

    public async Task<List<LeaveBalance>> BalancesAsync(int year)
    {
        var emps = await _db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).ToListAsync();
        var reqs = await _db.LeaveRequests.Include(l => l.LeaveType).Where(l => l.FromDate.Year == year && l.LeaveType!.DeductsAnnualBalance && (l.Status == LeaveStatus.Approved || l.Status == LeaveStatus.Pending)).ToListAsync();
        return emps.Select(e => new LeaveBalance(e, Entitlement(e, year), reqs.Where(r => r.EmployeeId == e.Id && r.Status == LeaveStatus.Approved).Sum(r => r.Days), reqs.Where(r => r.EmployeeId == e.Id && r.Status == LeaveStatus.Pending).Sum(r => r.Days))).ToList();
    }

    public async Task<LeaveRequest> RequestLeaveAsync(LeaveRequest r)
    {
        if (r.ToDate < r.FromDate) throw new BusinessException("Ngày kết thúc không trước ngày bắt đầu.");
        if (r.Days <= 0) throw new BusinessException("Số ngày nghỉ phải lớn hơn 0.");
        var type = await _db.LeaveTypes.FindAsync(r.LeaveTypeId) ?? throw new BusinessException("Chọn loại nghỉ.");
        var emp = await _db.Employees.FindAsync(r.EmployeeId) ?? throw new BusinessException("Chọn nhân viên.");
        if (await _db.LeaveRequests.AnyAsync(x => x.EmployeeId == r.EmployeeId && (x.Status == LeaveStatus.Approved || x.Status == LeaveStatus.Pending) && x.FromDate <= r.ToDate && x.ToDate >= r.FromDate))
            throw new BusinessException("Nhân viên đã có đơn nghỉ trùng thời gian này.");
        if (type.DeductsAnnualBalance)
        {
            var bal = (await BalancesAsync(r.FromDate.Year)).First(b => b.Employee.Id == emp.Id);
            if (r.Days > bal.Remaining - bal.Pending) throw new BusinessException($"Vượt số ngày phép năm còn lại ({(bal.Remaining - bal.Pending).ToQty()} ngày, đã gồm đơn chờ duyệt).");
        }
        r.Status = LeaveStatus.Pending; r.CreatedAt = DateTime.Now;
        _db.LeaveRequests.Add(r);
        await _db.SaveChangesAsync();
        return r;
    }

    public async Task DecideLeaveAsync(LeaveRequest r, bool approve, string? note)
    {
        if (r.Status != LeaveStatus.Pending) throw new BusinessException("Đơn đã được xử lý.");
        r.Status = approve ? LeaveStatus.Approved : LeaveStatus.Rejected; r.DecidedBy = _user.Name; r.DecidedAt = DateTime.Now; r.DecisionNote = note;
        if (approve)
        {
            var type = await _db.LeaveTypes.FindAsync(r.LeaveTypeId);
            for (var d = r.FromDate.Date; d <= r.ToDate.Date; d = d.AddDays(1))
            {
                if (d.DayOfWeek == DayOfWeek.Sunday) continue;
                var day = await _db.AttendanceDays.FirstOrDefaultAsync(x => x.EmployeeId == r.EmployeeId && x.Date == d);
                if (day == null) _db.AttendanceDays.Add(new AttendanceDay { EmployeeId = r.EmployeeId, Date = d, Kind = AttendanceKind.Leave, Source = "Đơn nghỉ phép", Note = type?.Name });
                else if (day.Kind != AttendanceKind.Present) { day.Kind = AttendanceKind.Leave; day.Note = type?.Name; }
            }
        }
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ Thành phần lương
    public record Extras(decimal Taxable, decimal NonTax, decimal Bonus, decimal Piece, decimal Deduction, string Note, decimal Advance = 0);

    /// <summary>Phụ cấp / thưởng / lương sản phẩm / khấu trừ của một nhân viên trong tháng (thành phần theo công thức tính từ dữ liệu nhân viên và chấm công)</summary>
    public static Extras ExtrasFor(Employee e, decimal workDays, decimal otHours, decimal stdDays, decimal salaryByDays, int year, int month,
        IReadOnlyList<EmployeeSalaryComponent> comps, IReadOnlyList<PayrollBonus> bonuses, IReadOnlyList<PieceRateEntry> pieces)
    {
        var vars = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["BaseSalary"] = e.BaseSalary, ["WorkDays"] = workDays, ["StdDays"] = stdDays, ["OvertimeHours"] = otHours, ["Dependents"] = e.Dependents,
            ["Years"] = Math.Floor(YearsOfService(e, new DateTime(year, month, 1).AddMonths(1).AddDays(-1))), ["InsuranceSalary"] = e.InsuranceSalary, ["SalaryByDays"] = salaryByDays, ["Allowance"] = e.Allowance
        };
        decimal tax = 0, non = 0, ded = 0; var notes = new List<string>();
        foreach (var c in comps.Where(x => x.Component is { IsActive: true }))
        {
            var comp = c.Component!;
            decimal v = string.IsNullOrWhiteSpace(comp.Formula) ? c.Amount : FormulaEvaluator.Eval(comp.Formula, vars) * (c.Amount == 0 ? 1 : c.Amount);
            v = Math.Round(v, 0);
            if (v == 0) continue;
            if (comp.Kind == ComponentKind.Deduction) ded += v; else if (comp.Taxable) tax += v; else non += v;
            notes.Add($"{comp.Name}: {(comp.Kind == ComponentKind.Deduction ? "−" : "+")}{v:N0}");
        }
        decimal bonus = 0;
        foreach (var b in bonuses) { bonus += b.Amount; if (b.Taxable) tax += b.Amount; else non += b.Amount; notes.Add($"{(b.Description.StartsWith("Thưởng", StringComparison.OrdinalIgnoreCase) ? "" : "Thưởng ")}{b.Description}: +{b.Amount:N0}"); }
        decimal piece = pieces.Sum(p => p.Amount);
        if (piece != 0) { tax += piece; notes.Add($"Lương sản phẩm ({pieces.Sum(p => p.Quantity):0.##} SP): +{piece:N0}"); }
        return new Extras(tax, non, bonus, piece, ded, string.Join("; ", notes));
    }
}
