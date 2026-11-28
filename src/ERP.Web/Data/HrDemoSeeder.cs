using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu nhân sự nâng cao: ca làm việc, loại nghỉ, hồ sơ cá nhân, hợp đồng (có hợp đồng sắp hết hạn), quá trình công tác,
/// thành phần lương theo công thức, thưởng, lương sản phẩm, lượt quẹt máy chấm công xử lý thành chấm công ngày, đơn nghỉ phép.
/// </summary>
public static class HrDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedProcessDemo", true)) return;
        async Task Run(string name, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Dữ liệu mẫu nhân sự \"{Name}\" chưa nạp được: {Msg}", name, ex.Message); }
        }
        await Run("Ca & loại nghỉ", () => Catalogs(db));
        await Run("Hồ sơ & hợp đồng", () => Profiles(db));
        await Run("Thành phần lương", () => Components(db));
        await Run("Chấm công", () => TimeClock(sp, db));
        await Run("Nghỉ phép", () => Leaves(sp, db));
        log.LogInformation("Đã nạp dữ liệu mẫu nhân sự nâng cao.");
    }

    private static async Task Catalogs(ErpDbContext db)
    {
        if (!await db.Shifts.AnyAsync())
            db.Shifts.AddRange(
                new Shift { Code = "HC", Name = "Hành chính", StartMinute = 8 * 60, EndMinute = 17 * 60, BreakMinutes = 60, GraceMinutes = 5, OtRate = 1.5m },
                new Shift { Code = "CA1", Name = "Ca sáng xưởng", StartMinute = 6 * 60, EndMinute = 14 * 60, BreakMinutes = 30, GraceMinutes = 5, OtRate = 1.5m },
                new Shift { Code = "CA2", Name = "Ca chiều xưởng", StartMinute = 14 * 60, EndMinute = 22 * 60, BreakMinutes = 30, GraceMinutes = 5, OtRate = 1.5m },
                new Shift { Code = "CA3", Name = "Ca đêm", StartMinute = 22 * 60, EndMinute = 6 * 60, BreakMinutes = 30, GraceMinutes = 5, IsNight = true, OtRate = 2m });
        if (!await db.LeaveTypes.AnyAsync())
            db.LeaveTypes.AddRange(
                new LeaveType { Code = "PN", Name = "Phép năm", IsPaid = true, DeductsAnnualBalance = true },
                new LeaveType { Code = "OM", Name = "Nghỉ ốm (hưởng BHXH)", IsPaid = false, BenefitKind = BenefitKind.Sickness },
                new LeaveType { Code = "TS", Name = "Nghỉ thai sản (hưởng BHXH)", IsPaid = false, BenefitKind = BenefitKind.Maternity },
                new LeaveType { Code = "VR", Name = "Việc riêng có lương (cưới, tang)", IsPaid = true },
                new LeaveType { Code = "KL", Name = "Nghỉ không lương", IsPaid = false });
        await db.SaveChangesAsync();
    }

    private static async Task Profiles(ErpDbContext db)
    {
        if (await db.LaborContracts.AnyAsync()) return;
        var emps = await db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).ToListAsync();
        var shifts = await db.Shifts.ToDictionaryAsync(s => s.Code);
        var today = DateTime.Today; var i = 0;
        string[] streets = { "12 Nguyễn Văn Trỗi, Phú Nhuận, TP.HCM", "45 Lê Văn Sỹ, Quận 3, TP.HCM", "88 Cộng Hòa, Tân Bình, TP.HCM", "21 Quang Trung, Gò Vấp, TP.HCM", "5 Phan Văn Trị, Bình Thạnh, TP.HCM" };
        foreach (var e in emps)
        {
            i++;
            e.DateOfBirth ??= new DateTime(1985 + i % 12, 1 + i % 12, 3 + i % 20); e.Gender ??= i % 3 == 0 ? Gender.Female : Gender.Male;
            e.Phone ??= $"09{i % 10}{7000000 + i * 13579:0000000}"; e.Email ??= $"{e.Code.ToLowerInvariant()}@noithatdemo.vn"; e.Address ??= streets[i % streets.Length];
            e.Education ??= i % 4 == 0 ? "Đại học" : i % 4 == 1 ? "Cao đẳng" : i % 4 == 2 ? "Trung cấp nghề" : "Đại học"; e.MaritalStatus ??= i % 2 == 0 ? "Đã kết hôn" : "Độc thân";
            e.InsuranceBookNo ??= $"7920{1000 + i * 37}"; e.EmergencyContact ??= $"Người thân – 09{i % 10}1234{500 + i}";
            var workshop = (e.Department ?? "").Contains("Xưởng") || (e.Position ?? "").Contains("Thợ");
            e.DefaultShiftId = shifts[workshop ? "CA1" : "HC"].Id;
            var months = i % 5 == 0 ? 6 : i % 4 == 0 ? 12 : 24;
            var start = e.StartDate;
            if (i % 5 == 0) db.LaborContracts.Add(new LaborContract { EmployeeId = e.Id, Number = $"HĐ-{e.Code}-01", Type = LaborContractType.Probation, SignedDate = today.AddMonths(-3), StartDate = today.AddMonths(-3), EndDate = today.AddDays(i * 2 + 6), Position = e.Position, Salary = e.BaseSalary, Status = LaborContractStatus.Active, Note = "Thử việc – sắp hết hạn" });
            else if (i % 4 == 0) db.LaborContracts.Add(new LaborContract { EmployeeId = e.Id, Number = $"HĐ-{e.Code}-02", Type = LaborContractType.FixedTerm, SignedDate = today.AddMonths(-months + 1), StartDate = today.AddMonths(-months + 1), EndDate = today.AddMonths(1).AddDays(-i), Position = e.Position, Salary = e.BaseSalary, Status = LaborContractStatus.Active });
            else db.LaborContracts.Add(new LaborContract { EmployeeId = e.Id, Number = $"HĐ-{e.Code}-03", Type = LaborContractType.Indefinite, SignedDate = start, StartDate = start, Position = e.Position, Salary = e.BaseSalary, Status = LaborContractStatus.Active });
            db.EmployeeHistories.Add(new EmployeeHistory { EmployeeId = e.Id, Kind = HistoryKind.Hired, Date = start, ToValue = $"{e.Position} – {e.Department}", Note = "Tuyển dụng" });
            if (i % 3 == 0) db.EmployeeHistories.Add(new EmployeeHistory { EmployeeId = e.Id, Kind = HistoryKind.SalaryChange, Date = today.AddMonths(-8), FromValue = (e.BaseSalary * 0.9m).ToString("N0"), ToValue = e.BaseSalary.ToString("N0"), Note = "Điều chỉnh lương định kỳ" });
            if (i % 4 == 1) db.EmployeeHistories.Add(new EmployeeHistory { EmployeeId = e.Id, Kind = HistoryKind.Reward, Date = today.AddMonths(-5), ToValue = "Nhân viên xuất sắc quý", Note = "Khen thưởng" });
        }
        await db.SaveChangesAsync();
    }

    private static async Task Components(ErpDbContext db)
    {
        if (await db.SalaryComponents.AnyAsync()) return;
        db.SalaryComponents.AddRange(
            new SalaryComponent { Code = "XANGXE", Name = "Phụ cấp xăng xe", Kind = ComponentKind.Earning, Taxable = false, Note = "Cố định theo nhân viên, miễn thuế trong mức quy định" },
            new SalaryComponent { Code = "CHUYENCAN", Name = "Thưởng chuyên cần", Kind = ComponentKind.Earning, Taxable = true, Formula = "IF(WorkDays >= StdDays; 500000; 0)", Note = "Đủ công chuẩn trong tháng" },
            new SalaryComponent { Code = "THAMNIEN", Name = "Phụ cấp thâm niên", Kind = ComponentKind.Earning, Taxable = true, Formula = "MIN(Years; 10) * 100000", Note = "100.000 đ mỗi năm, tối đa 10 năm" },
            new SalaryComponent { Code = "DOANPHI", Name = "Đoàn phí công đoàn", Kind = ComponentKind.Deduction, Taxable = false, Formula = "ROUND(BaseSalary * 0.01)", Note = "1% lương cơ bản" });
        await db.SaveChangesAsync();
        var comps = await db.SalaryComponents.ToDictionaryAsync(c => c.Code);
        var emps = await db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).ToListAsync();
        foreach (var e in emps)
        {
            db.EmployeeSalaryComponents.Add(new EmployeeSalaryComponent { EmployeeId = e.Id, SalaryComponentId = comps["XANGXE"].Id, Amount = 500_000 });
            db.EmployeeSalaryComponents.Add(new EmployeeSalaryComponent { EmployeeId = e.Id, SalaryComponentId = comps["CHUYENCAN"].Id });
            db.EmployeeSalaryComponents.Add(new EmployeeSalaryComponent { EmployeeId = e.Id, SalaryComponentId = comps["THAMNIEN"].Id });
            db.EmployeeSalaryComponents.Add(new EmployeeSalaryComponent { EmployeeId = e.Id, SalaryComponentId = comps["DOANPHI"].Id });
        }
        var today = DateTime.Today;
        foreach (var e in emps.Take(2)) db.PayrollBonuses.Add(new PayrollBonus { EmployeeId = e.Id, Year = today.Year, Month = today.Month, Description = "Thưởng hoàn thành đơn hàng dự án", Amount = 1_000_000, Taxable = true });
        var item = await db.Items.FirstOrDefaultAsync(i => i.Code == "TP-BLV120");
        foreach (var e in emps.Where(x => (x.Department ?? "").Contains("Xưởng")).Take(3))
            db.PieceRateEntries.Add(new PieceRateEntry { EmployeeId = e.Id, Date = today.AddDays(-2), ItemId = item?.Id, Description = "Sơn hoàn thiện bàn làm việc", Quantity = 12, Rate = 45_000 });
        await db.SaveChangesAsync();
    }

    private static async Task TimeClock(IServiceProvider sp, ErpDbContext db)
    {
        if (await db.TimeClockPunches.AnyAsync()) return;
        var hr = sp.GetRequiredService<HrService>();
        var emps = await db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).ToListAsync();
        var shifts = await db.Shifts.ToDictionaryAsync(s => s.Id);
        var rnd = new Random(2611); var today = DateTime.Today;
        foreach (var e in emps)
            for (var d = 11; d >= 1; d--)
            {
                var day = today.AddDays(-d);
                if (day.DayOfWeek == DayOfWeek.Sunday || day.Month != today.Month) continue;
                var sh = e.DefaultShiftId != null && shifts.TryGetValue(e.DefaultShiftId.Value, out var s) ? s : shifts.Values.First();
                if (rnd.Next(20) == 0) continue;   // vắng
                var late = rnd.Next(12) == 0 ? rnd.Next(8, 30) : rnd.Next(-8, 4);
                var inT = day.AddMinutes(sh.StartMinute + late);
                var ot = rnd.Next(5) == 0 ? rnd.Next(1, 4) * 30 : 0;
                var outT = day.AddMinutes(sh.EndMinute + ot + rnd.Next(0, 10));
                db.TimeClockPunches.Add(new TimeClockPunch { EmployeeCode = e.Code, PunchTime = inT, Device = "Máy vân tay cổng chính" });
                if (rnd.Next(30) != 0) db.TimeClockPunches.Add(new TimeClockPunch { EmployeeCode = e.Code, PunchTime = outT, Device = "Máy vân tay cổng chính" });
            }
        await db.SaveChangesAsync();
        await hr.ProcessPunchesAsync();
    }

    private static async Task Leaves(IServiceProvider sp, ErpDbContext db)
    {
        if (await db.LeaveRequests.AnyAsync()) return;
        var hr = sp.GetRequiredService<HrService>();
        var types = await db.LeaveTypes.ToDictionaryAsync(t => t.Code);
        var emps = await db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Take(4).ToListAsync();
        if (emps.Count < 3) return;
        var today = DateTime.Today;
        var a = await hr.RequestLeaveAsync(new LeaveRequest { EmployeeId = emps[0].Id, LeaveTypeId = types["PN"].Id, FromDate = today.AddDays(-20), ToDate = today.AddDays(-19), Days = 2, Reason = "Về quê" });
        await hr.DecideLeaveAsync(a, true, "Đồng ý");
        var b = await hr.RequestLeaveAsync(new LeaveRequest { EmployeeId = emps[1].Id, LeaveTypeId = types["OM"].Id, FromDate = today.AddDays(-12), ToDate = today.AddDays(-12), Days = 1, Reason = "Sốt, có giấy khám" });
        await hr.DecideLeaveAsync(b, true, null);
        await hr.RequestLeaveAsync(new LeaveRequest { EmployeeId = emps[2].Id, LeaveTypeId = types["PN"].Id, FromDate = today.AddDays(8), ToDate = today.AddDays(10), Days = 3, Reason = "Du lịch gia đình" });
        await hr.RequestLeaveAsync(new LeaveRequest { EmployeeId = emps[3].Id, LeaveTypeId = types["KL"].Id, FromDate = today.AddDays(15), ToDate = today.AddDays(15), Days = 1, Reason = "Việc cá nhân" });
    }
}
