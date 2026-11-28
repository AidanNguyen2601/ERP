using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services.Reporting;

/// <summary>Nhân sự: hồ sơ nhân viên đang có / đã nghỉ</summary>
public class EmployeeDataset : Dataset
{
    public override string Key => "employees";
    public override string Title => "Nhân sự (hồ sơ nhân viên)";
    public override string Group => "Nhân sự";
    public override string Description => "Danh sách nhân viên: phòng ban, chức vụ, lương, thâm niên, giới tính, trạng thái.";
    public override string Roles => AppRoles.FinanceView;
    public override DateMode DateMode => DateMode.None;
    private static readonly Field[] F =
    {
        new("code", "Mã nhân viên", FieldKind.Text), new("name", "Họ tên", FieldKind.Text), new("dept", "Phòng ban", FieldKind.Text), new("position", "Chức vụ", FieldKind.Text),
        new("gender", "Giới tính", FieldKind.Text), new("status", "Trạng thái", FieldKind.Text), new("startDate", "Ngày vào làm", FieldKind.Date), new("years", "Thâm niên (năm)", FieldKind.Qty),
        new("baseSalary", "Lương cơ bản", FieldKind.Money), new("insSalary", "Lương đóng BH", FieldKind.Money), new("dependents", "Người phụ thuộc", FieldKind.Number), new("headcount", "Số nhân viên", FieldKind.Number),
        new("contract", "Loại hợp đồng hiện hành", FieldKind.Text), new("contractEnd", "Hợp đồng hết hạn", FieldKind.Date),
    };
    public override IReadOnlyList<Field> Fields { get; } = F;
    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var emps = await db.Employees.AsNoTracking().ToListAsync();
        var cons = (await db.LaborContracts.AsNoTracking().Where(c => c.Status == LaborContractStatus.Active).ToListAsync()).GroupBy(c => c.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.StartDate).First());
        return emps.Select(e =>
        {
            cons.TryGetValue(e.Id, out var c);
            return new Row
            {
                ["code"] = e.Code, ["name"] = e.FullName, ["dept"] = e.Department, ["position"] = e.Position, ["gender"] = e.Gender?.GetDisplayName() ?? "Chưa rõ", ["status"] = e.IsActive ? "Đang làm" : "Đã nghỉ",
                ["startDate"] = e.StartDate.Date, ["years"] = Math.Round((decimal)((e.ResignDate ?? DateTime.Today) - e.StartDate).TotalDays / 365.25m, 1), ["baseSalary"] = e.BaseSalary, ["insSalary"] = e.InsuranceSalary,
                ["dependents"] = (decimal)e.Dependents, ["headcount"] = 1m, ["contract"] = c?.Type.GetDisplayName() ?? "Chưa có", ["contractEnd"] = c?.EndDate
            };
        }).ToList();
    }
}

/// <summary>Chấm công theo ngày: giờ làm, đi muộn, tăng ca, nghỉ phép</summary>
public class AttendanceDataset : Dataset
{
    public override string Key => "attendance";
    public override string Title => "Chấm công theo ngày";
    public override string Group => "Nhân sự";
    public override string Description => "Kết quả chấm công từng ngày: ca, công, đi muộn, về sớm, tăng ca, nghỉ phép.";
    public override string Roles => AppRoles.FinanceView;
    public override string DateLabel => "Ngày công";
    private static readonly Field[] F =
    {
        new("date", "Ngày", FieldKind.Date), new("code", "Mã nhân viên", FieldKind.Text), new("name", "Họ tên", FieldKind.Text), new("dept", "Phòng ban", FieldKind.Text), new("shift", "Ca", FieldKind.Text),
        new("kind", "Loại ngày công", FieldKind.Text), new("workHours", "Giờ làm", FieldKind.Qty), new("lateMin", "Đi muộn (phút)", FieldKind.Number), new("earlyMin", "Về sớm (phút)", FieldKind.Number), new("otHours", "Giờ tăng ca", FieldKind.Qty),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);
    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.AttendanceDays.AsNoTracking().AsQueryable();
        if (from.HasValue) q = q.Where(a => a.Date >= from.Value);
        if (to.HasValue) q = q.Where(a => a.Date <= to.Value);
        var list = await q.Select(a => new { a.Date, Code = a.Employee!.Code, Name = a.Employee.FullName, Dept = a.Employee.Department, Shift = a.Shift != null ? a.Shift.Name : null, a.Kind, a.WorkedHours, a.LateMinutes, a.EarlyLeaveMinutes, a.OvertimeHours }).ToListAsync();
        return list.Select(a => Rb.Time(new Row
        {
            ["code"] = a.Code, ["name"] = a.Name, ["dept"] = a.Dept, ["shift"] = a.Shift ?? "—", ["kind"] = a.Kind.GetDisplayName(), ["workHours"] = a.WorkedHours,
            ["lateMin"] = (decimal)a.LateMinutes, ["earlyMin"] = (decimal)a.EarlyLeaveMinutes, ["otHours"] = a.OvertimeHours
        }, "date", a.Date)).ToList();
    }
}

/// <summary>CRM: cơ hội bán hàng</summary>
public class OpportunityDataset : Dataset
{
    public override string Key => "crm-opps";
    public override string Title => "CRM – cơ hội bán hàng";
    public override string Group => "Bán hàng";
    public override string Description => "Cơ hội bán hàng: giai đoạn, giá trị, xác suất, giá trị có trọng số, người phụ trách, nguồn khách hàng.";
    public override string Roles => AppRoles.SalesTeam + "," + AppRoles.FinanceView;
    public override string DateLabel => "Ngày tạo cơ hội";
    private static readonly Field[] F =
    {
        new("date", "Ngày tạo", FieldKind.Date), new("code", "Mã cơ hội", FieldKind.Text), new("title", "Cơ hội", FieldKind.Text), new("customer", "Khách hàng", FieldKind.Text), new("stage", "Giai đoạn", FieldKind.Text),
        new("owner", "Người phụ trách", FieldKind.Text), new("source", "Nguồn khách hàng", FieldKind.Text), new("expectedClose", "Dự kiến chốt", FieldKind.Date), new("amount", "Giá trị", FieldKind.Money),
        new("weighted", "Giá trị có trọng số", FieldKind.Money), new("probability", "Xác suất (%)", FieldKind.Percent), new("count", "Số cơ hội", FieldKind.Number), new("won", "Số cơ hội thắng", FieldKind.Number),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);
    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.Opportunities.AsNoTracking().AsQueryable();
        if (from.HasValue) q = q.Where(o => o.CreatedAt >= from.Value);
        if (to.HasValue) q = q.Where(o => o.CreatedAt < to.Value.AddDays(1));
        var list = await q.Select(o => new
        {
            o.CreatedAt, o.Code, o.Title, Cust = o.Partner != null ? o.Partner.Name : (o.Lead != null ? o.Lead.Name : null), o.Stage, Owner = o.Owner != null ? o.Owner.FullName : null,
            Src = o.Lead != null ? (LeadSource?)o.Lead.Source : null, o.ExpectedClose, o.Amount, o.Probability
        }).ToListAsync();
        return list.Select(o => Rb.Time(new Row
        {
            ["code"] = o.Code, ["title"] = o.Title, ["customer"] = o.Cust, ["stage"] = o.Stage.GetDisplayName(), ["owner"] = o.Owner ?? "Chưa phân công", ["source"] = o.Src?.GetDisplayName() ?? "Trực tiếp",
            ["expectedClose"] = o.ExpectedClose.Date, ["amount"] = o.Amount, ["weighted"] = Math.Round(o.Amount * o.Probability / 100m, 0), ["probability"] = (decimal)o.Probability, ["count"] = 1m, ["won"] = o.Stage == OppStage.Won ? 1m : 0m
        }, "date", o.CreatedAt)).ToList();
    }
}
