using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

public record EquipmentKpi(Equipment Equipment, int Breakdowns, decimal DowntimeHours, decimal RepairHours, decimal PlannedHours, int PmDue, int PmOnTime,
    decimal PartsCost, decimal LaborCost, decimal OutsideCost, int Orders)
{
    public decimal TotalCost => PartsCost + LaborCost + OutsideCost;
    /// <summary>Thời gian trung bình giữa hai lần hỏng (giờ)</summary>
    public decimal Mtbf => Breakdowns == 0 ? 0 : Math.Round((PlannedHours - DowntimeHours) / Breakdowns, 1);
    /// <summary>Thời gian sửa chữa trung bình (giờ)</summary>
    public decimal Mttr => Breakdowns == 0 ? 0 : Math.Round(RepairHours / Breakdowns, 1);
    public decimal Availability => PlannedHours <= 0 ? 100 : Math.Round(Math.Max(0, PlannedHours - DowntimeHours) * 100 / PlannedHours, 1);
    public decimal PmCompliance => PmDue == 0 ? 100 : Math.Round(PmOnTime * 100m / PmDue, 1);
}

/// <summary>
/// Bảo dưỡng thiết bị: kế hoạch định kỳ → phiếu bảo dưỡng; sửa chữa đột xuất (yêu cầu → duyệt → thực hiện → nghiệm thu);
/// phụ tùng xuất kho ghi chi phí (Nợ chi phí bảo dưỡng / Có kho), hoàn trả phụ tùng chưa dùng; thời gian dừng máy; chỉ số MTBF, MTTR.
/// </summary>
public class MaintenanceService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly AccountingService _acc;
    private readonly WorkflowService _wf;
    private readonly CurrentUser _user;
    public MaintenanceService(ErpDbContext db, InventoryService inv, AccountingService acc, WorkflowService wf, CurrentUser user) { _db = db; _inv = inv; _acc = acc; _wf = wf; _user = user; }

    public static string Prefix(MaintenanceKind k) => k switch { MaintenanceKind.Breakdown => "SC", MaintenanceKind.Autonomous => "TPM", MaintenanceKind.Calibration => "HC", _ => "BD" };

    public Task<string> NextNumberAsync(MaintenanceKind kind, DateTime date) => DocNumbers.NextAsync(_db.MaintenanceOrders.Select(x => x.Number), Prefix(kind), date);
    public Task<string> NextPlanCodeAsync(DateTime date) => DocNumbers.NextAsync(_db.MaintenancePlans.Select(x => x.Code), "KHBD", date);

    // ------------------------------------------------------------------ Kế hoạch
    public async Task<string?> SubmitPlanAsync(MaintenancePlan p)
    {
        if (p.Status != MaintenancePlanStatus.Draft) throw new BusinessException("Chỉ gửi duyệt kế hoạch nháp.");
        if (await _wf.HasRulesAsync(ApprovalDocType.MaintenancePlan))
        {
            var yearly = p.EstimatedCost * Math.Max(1, 365 / Math.Max(1, p.IntervalDays));
            var req = await _wf.StartAsync(ApprovalDocType.MaintenancePlan, p.Id, p.Code, yearly, $"Kế hoạch bảo dưỡng {p.Code} – {p.Title}", p.BranchId);
            if (req != null) { p.Status = MaintenancePlanStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Kế hoạch {p.Code} đã gửi duyệt – {WorkflowService.Describe(req)}."; }
        }
        p.Status = MaintenancePlanStatus.Active; p.ApprovedById = _user.Id; p.ApprovedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>Ngày đến hạn của kế hoạch trong khoảng (bắt đầu từ lần đến hạn kế tiếp, mỗi chu kỳ một lần)</summary>
    public static IEnumerable<DateTime> Occurrences(MaintenancePlan p, DateTime from, DateTime to)
    {
        if (p.Status != MaintenancePlanStatus.Active || p.IntervalDays <= 0) yield break;
        var d = p.NextDueDate.Date;
        var guard = 0;
        while (d <= to && guard++ < 4000) { if (d >= from) yield return d; d = d.AddDays(p.IntervalDays); }
    }

    /// <summary>Tạo phiếu bảo dưỡng cho các kế hoạch đã đến hạn (trước hạn LeadDays ngày) và chưa có phiếu đang mở</summary>
    public async Task<List<MaintenanceOrder>> GenerateDueAsync(DateTime date)
    {
        var plans = await _db.MaintenancePlans.Include(p => p.Equipment).Where(p => p.Status == MaintenancePlanStatus.Active).ToListAsync();
        var openPlanIds = (await _db.MaintenanceOrders.Where(o => o.PlanId != null && o.Status != MaintenanceStatus.Accepted && o.Status != MaintenanceStatus.Cancelled).Select(o => o.PlanId!.Value).ToListAsync()).ToHashSet();
        var made = new List<MaintenanceOrder>();
        foreach (var p in plans.Where(p => !openPlanIds.Contains(p.Id) && p.NextDueDate.Date.AddDays(-p.LeadDays) <= date.Date && p.Equipment is { Status: not EquipmentStatus.Retired }))
        {
            var o = new MaintenanceOrder
            {
                Kind = p.Kind, EquipmentId = p.EquipmentId, PlanId = p.Id, Title = p.Title, DueDate = p.NextDueDate.Date, Status = MaintenanceStatus.Approved, EstimatedCost = p.EstimatedCost,
                Technician = p.Responsible, RequestedBy = "Kế hoạch " + p.Code, Priority = MaintenancePriority.Normal, CreatedById = _user.Id, BranchId = p.BranchId,
                Number = await NextNumberAsync(p.Kind, date)
            };
            var seq = 0;
            foreach (var line in (p.Checklist ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
                o.Tasks.Add(new MaintenanceTask { Seq = ++seq, Description = line });
            _db.MaintenanceOrders.Add(o);
            await _db.SaveChangesAsync();   // lưu từng phiếu để số phiếu kế tiếp không trùng
            made.Add(o);
        }
        return made;
    }

    // ------------------------------------------------------------------ Phiếu
    public async Task<MaintenanceOrder> CreateAsync(MaintenanceOrder o)
    {
        var eq = await _db.Equipments.FindAsync(o.EquipmentId) ?? throw new BusinessException("Chọn thiết bị.");
        if (eq.Status == EquipmentStatus.Retired) throw new BusinessException("Thiết bị đã ngưng sử dụng.");
        o.Number = await NextNumberAsync(o.Kind, o.ReportedAt);
        o.Status = MaintenanceStatus.Draft; o.CreatedById = _user.Id; o.BranchId = eq.BranchId;
        o.RequestedBy ??= _user.Name;
        _db.MaintenanceOrders.Add(o);
        await _db.SaveChangesAsync();
        return o;
    }

    /// <summary>Gửi duyệt theo quy tắc phê duyệt (giá trị = chi phí dự kiến); trả về thông báo nếu phải chờ duyệt, null nếu được duyệt ngay</summary>
    public async Task<string?> SubmitAsync(MaintenanceOrder o)
    {
        if (o.Status != MaintenanceStatus.Draft) throw new BusinessException("Chỉ gửi duyệt phiếu nháp.");
        if (await _wf.HasRulesAsync(ApprovalDocType.MaintenanceOrder))
        {
            var est = Math.Max(o.EstimatedCost, 0);
            var req = await _wf.StartAsync(ApprovalDocType.MaintenanceOrder, o.Id, o.Number, est, $"{o.Kind.GetDisplayName()} {o.Number} – {o.Title}", o.BranchId);
            if (req != null) { o.Status = MaintenanceStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Phiếu {o.Number} đã gửi duyệt – {WorkflowService.Describe(req)}."; }
        }
        o.Status = MaintenanceStatus.Approved;
        await _db.SaveChangesAsync();
        return null;
    }

    private async Task SyncEquipmentAsync(int equipmentId)
    {
        var eq = await _db.Equipments.FindAsync(equipmentId);
        if (eq == null || eq.Status == EquipmentStatus.Retired) return;
        var active = await _db.MaintenanceOrders.Where(o => o.EquipmentId == equipmentId && o.Status == MaintenanceStatus.InProgress && o.Kind != MaintenanceKind.Autonomous).ToListAsync();
        eq.Status = active.Any(o => o.Kind == MaintenanceKind.Breakdown) ? EquipmentStatus.Down : active.Any() ? EquipmentStatus.UnderMaintenance : EquipmentStatus.Running;
        await _db.SaveChangesAsync();
    }

    public async Task StartAsync(MaintenanceOrder o)
    {
        if (o.Status != MaintenanceStatus.Approved) throw new BusinessException("Chỉ bắt đầu thực hiện phiếu đã duyệt.");
        o.Status = MaintenanceStatus.InProgress; o.StartedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SyncEquipmentAsync(o.EquipmentId!.Value);
    }

    // ------------------------------------------------------------------ Phụ tùng
    public async Task<MaintenancePart> AddPartAsync(MaintenanceOrder o, int itemId, decimal qty)
    {
        if (o.Status is MaintenanceStatus.Accepted or MaintenanceStatus.Cancelled or MaintenanceStatus.Completed) throw new BusinessException("Phiếu đã kết thúc, không thêm phụ tùng.");
        if (qty <= 0) throw new BusinessException("Số lượng phải lớn hơn 0.");
        var item = await _db.Items.FindAsync(itemId) ?? throw new BusinessException("Không tìm thấy phụ tùng.");
        if (!item.IsStocked) throw new BusinessException("Chỉ chọn hàng có quản lý tồn kho.");
        var part = await _db.MaintenanceParts.FirstOrDefaultAsync(p => p.MaintenanceOrderId == o.Id && p.ItemId == itemId);
        if (part == null) { part = new MaintenancePart { MaintenanceOrderId = o.Id, ItemId = itemId }; _db.MaintenanceParts.Add(part); }
        part.PlannedQty += qty;
        await _db.SaveChangesAsync();
        return part;
    }

    private async Task<InventoryDoc> PostPartDocAsync(MaintenanceOrder o, InvDocType type, DateTime date, int itemId, decimal qty, decimal unitCost, string reason)
    {
        var eq = await _db.Equipments.FindAsync(o.EquipmentId);
        var acc = eq?.ExpenseAccountId ?? await _acc.RuleAsync(PostingCatalog.MtExpense);
        var doc = new InventoryDoc
        {
            Type = type, Date = date.Date, WarehouseId = o.WarehouseId!.Value, Origin = DocOrigin.Maintenance, OriginRef = o.Number, OriginId = o.Id,
            CounterAccountId = acc, Reason = reason, CreatedById = _user.Id, Number = await _inv.NextNumberAsync(type, date.Date)
        };
        doc.Lines.Add(new InventoryDocLine { ItemId = itemId, Quantity = qty, UnitCost = unitCost });
        await _inv.PostAsync(doc);
        return doc;
    }

    /// <summary>Xuất phụ tùng cho phiếu: Nợ chi phí bảo dưỡng / Có kho theo giá vốn bình quân</summary>
    public async Task<InventoryDoc> IssuePartAsync(MaintenanceOrder o, int partId, decimal qty, DateTime date)
    {
        if (o.Status is not (MaintenanceStatus.Approved or MaintenanceStatus.InProgress)) throw new BusinessException("Chỉ xuất phụ tùng cho phiếu đã duyệt hoặc đang thực hiện.");
        if (o.WarehouseId == null) throw new BusinessException("Chọn kho xuất phụ tùng trên phiếu.");
        var part = await _db.MaintenanceParts.Include(p => p.Item).FirstOrDefaultAsync(p => p.Id == partId && p.MaintenanceOrderId == o.Id) ?? throw new BusinessException("Không tìm thấy dòng phụ tùng.");
        if (qty <= 0) throw new BusinessException("Số lượng xuất phải lớn hơn 0.");
        var doc = await PostPartDocAsync(o, InvDocType.Issue, date, part.ItemId, qty, 0, $"Xuất phụ tùng cho phiếu bảo dưỡng {o.Number}");
        part.IssuedQty += qty; part.Cost += doc.Lines.Sum(l => l.Amount);
        if (part.PlannedQty < part.IssuedQty) part.PlannedQty = part.IssuedQty;
        o.PartsCost = await SumPartsAsync(o.Id, part);
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Hoàn trả phụ tùng chưa dùng về kho: Nợ kho / Có chi phí bảo dưỡng theo giá đã xuất</summary>
    public async Task<InventoryDoc> ReturnPartAsync(MaintenanceOrder o, int partId, decimal qty, DateTime date)
    {
        if (o.Status is MaintenanceStatus.Accepted or MaintenanceStatus.Cancelled) throw new BusinessException("Phiếu đã kết thúc.");
        var part = await _db.MaintenanceParts.FirstOrDefaultAsync(p => p.Id == partId && p.MaintenanceOrderId == o.Id) ?? throw new BusinessException("Không tìm thấy dòng phụ tùng.");
        if (qty <= 0 || qty > part.UsedQty) throw new BusinessException($"Số lượng hoàn trả phải từ 0 đến {part.UsedQty.ToQty()} (đã xuất chưa trả).");
        var unit = part.UsedQty <= 0 ? 0 : part.Cost / part.UsedQty;
        var doc = await PostPartDocAsync(o, InvDocType.Receipt, date, part.ItemId, qty, unit, $"Hoàn trả phụ tùng từ phiếu bảo dưỡng {o.Number}");
        part.ReturnedQty += qty; part.Cost -= doc.Lines.Sum(l => l.Amount);
        o.PartsCost = await SumPartsAsync(o.Id, part);
        await _db.SaveChangesAsync();
        return doc;
    }

    private async Task<decimal> SumPartsAsync(int orderId, MaintenancePart changed) =>
        (await _db.MaintenanceParts.Where(p => p.MaintenanceOrderId == orderId && p.Id != changed.Id).SumAsync(p => (decimal?)p.Cost) ?? 0) + changed.Cost;

    public async Task RecoverOldPartAsync(MaintenanceOrder o, int partId, decimal qty)
    {
        var part = await _db.MaintenanceParts.FirstOrDefaultAsync(p => p.Id == partId && p.MaintenanceOrderId == o.Id) ?? throw new BusinessException("Không tìm thấy dòng phụ tùng.");
        if (qty < 0) throw new BusinessException("Số lượng không âm.");
        part.RecoveredOldQty = qty;
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ Hoàn thành / nghiệm thu
    public async Task CompleteAsync(MaintenanceOrder o, string? cause, string? resolution, decimal laborHours, decimal laborRate, decimal outsideCost, decimal? downtimeHours, string? technician)
    {
        if (o.Status != MaintenanceStatus.InProgress) throw new BusinessException("Chỉ hoàn thành phiếu đang thực hiện.");
        if (laborHours < 0 || laborRate < 0 || outsideCost < 0) throw new BusinessException("Giờ công và chi phí không âm.");
        var tasks = await _db.MaintenanceTasks.Where(t => t.MaintenanceOrderId == o.Id).ToListAsync();
        if (tasks.Any(t => !t.Done)) throw new BusinessException("Còn hạng mục chưa hoàn thành trong danh mục công việc.");
        if (o.Kind == MaintenanceKind.Breakdown && string.IsNullOrWhiteSpace(cause)) throw new BusinessException("Nhập nguyên nhân hỏng.");
        var now = DateTime.Now;
        o.CompletedAt = now; o.Cause = cause; o.Resolution = resolution; o.Technician = string.IsNullOrWhiteSpace(technician) ? o.Technician : technician;
        o.LaborHours = laborHours; o.LaborCost = Math.Round(laborHours * laborRate, 0); o.OutsideCost = outsideCost;
        o.DowntimeHours = downtimeHours ?? (o.Kind == MaintenanceKind.Breakdown ? Math.Round((decimal)(now - o.ReportedAt).TotalHours, 1) : 0);
        if (o.DowntimeHours < 0) o.DowntimeHours = 0;
        o.Status = MaintenanceStatus.Completed;
        if (o.PlanId != null)
        {
            var plan = await _db.MaintenancePlans.FindAsync(o.PlanId);
            if (plan != null) { plan.LastDoneDate = now.Date; plan.NextDueDate = now.Date.AddDays(plan.IntervalDays); }
        }
        await _db.SaveChangesAsync();
        await SyncEquipmentAsync(o.EquipmentId!.Value);
    }

    public async Task AcceptAsync(MaintenanceOrder o, string by, string? note)
    {
        if (o.Status != MaintenanceStatus.Completed) throw new BusinessException("Chỉ nghiệm thu phiếu đã hoàn thành.");
        if (string.IsNullOrWhiteSpace(by)) throw new BusinessException("Nhập người nghiệm thu.");
        o.Status = MaintenanceStatus.Accepted; o.AcceptedBy = by.Trim(); o.AcceptedAt = DateTime.Now; o.AcceptanceNote = note;
        await _db.SaveChangesAsync();
    }

    public async Task CancelAsync(MaintenanceOrder o)
    {
        if (o.Status is not (MaintenanceStatus.Draft or MaintenanceStatus.PendingApproval or MaintenanceStatus.Approved)) throw new BusinessException("Phiếu đã thực hiện, không hủy được.");
        if (await _db.MaintenanceParts.AnyAsync(p => p.MaintenanceOrderId == o.Id && p.IssuedQty > 0)) throw new BusinessException("Đã xuất phụ tùng: hoàn trả hết phụ tùng trước khi hủy.");
        o.Status = MaintenanceStatus.Cancelled;
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ Chỉ số (KPI)
    /// <summary>MTBF, MTTR, độ sẵn sàng, tuân thủ bảo dưỡng định kỳ và chi phí theo thiết bị trong khoảng ngày (giờ kế hoạch = ngày làm việc T2–T7 × giờ/ngày)</summary>
    public async Task<List<EquipmentKpi>> KpiAsync(DateTime from, DateTime to, decimal hoursPerDay = 8)
    {
        var equipments = await _db.Equipments.Where(e => e.Status != EquipmentStatus.Retired || _db.MaintenanceOrders.Any(o => o.EquipmentId == e.Id)).OrderBy(e => e.Code).ToListAsync();
        var orders = await _db.MaintenanceOrders.Where(o => o.Status != MaintenanceStatus.Cancelled && o.Status != MaintenanceStatus.Draft && o.Status != MaintenanceStatus.PendingApproval
            && ((o.CompletedAt != null && o.CompletedAt >= from && o.CompletedAt < to.AddDays(1)) || (o.CompletedAt == null && o.DueDate >= from && o.DueDate <= to))).ToListAsync();
        var days = Enumerable.Range(0, (to.Date - from.Date).Days + 1).Count(i => from.Date.AddDays(i).DayOfWeek != DayOfWeek.Sunday);
        var planned = days * hoursPerDay;
        var res = new List<EquipmentKpi>();
        foreach (var e in equipments)
        {
            var os = orders.Where(o => o.EquipmentId == e.Id).ToList();
            var done = os.Where(o => o.Status is MaintenanceStatus.Completed or MaintenanceStatus.Accepted).ToList();
            var bd = done.Where(o => o.Kind == MaintenanceKind.Breakdown).ToList();
            var pm = os.Where(o => o.PlanId != null && o.DueDate >= from && o.DueDate <= to).ToList();
            var onTime = pm.Count(o => o.Status is MaintenanceStatus.Completed or MaintenanceStatus.Accepted && o.CompletedAt != null && o.CompletedAt.Value.Date <= o.DueDate.Date.AddDays(3));
            if (os.Count == 0 && e.Status == EquipmentStatus.Retired) continue;
            res.Add(new EquipmentKpi(e, bd.Count, done.Sum(o => o.DowntimeHours), bd.Sum(o => o.DowntimeHours), planned, pm.Count, onTime,
                done.Sum(o => o.PartsCost), done.Sum(o => o.LaborCost), done.Sum(o => o.OutsideCost), os.Count));
        }
        return res;
    }
}
