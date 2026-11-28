using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Nhân sự chuyên sâu: tuyển dụng → hồ sơ nhân viên, đánh giá KPI → thưởng, đào tạo → quá trình công tác</summary>
public class TalentService
{
    private readonly ErpDbContext _db; private readonly CodeService _codes; private readonly HrService _hr;
    public TalentService(ErpDbContext db, CodeService codes, HrService hr) { _db = db; _codes = codes; _hr = hr; }

    /// <summary>Nhận việc: tạo hồ sơ nhân viên từ ứng viên, ghi quá trình "Tuyển dụng", đóng vị trí khi đủ số lượng</summary>
    public async Task<Employee> HireAsync(Candidate c, decimal baseSalary, DateTime startDate)
    {
        if (c.Stage == CandidateStage.Hired) throw new BusinessException("Ứng viên đã được tuyển.");
        var job = c.JobOpening ?? await _db.JobOpenings.FindAsync(c.JobOpeningId) ?? throw new BusinessException("Không tìm thấy vị trí tuyển dụng.");
        var tpl = await _db.Employees.Where(e => e.Department == job.Department).Select(e => e.ExpenseAccount).FirstOrDefaultAsync() ?? "6421";
        var e = new Employee
        {
            Code = await _codes.NextAsync("employee") ?? ("NV" + DateTime.Now.ToString("yyMMddHHmm")), FullName = c.FullName, Phone = c.Phone, Email = c.Email,
            Department = job.Department ?? "", Position = job.Title, BaseSalary = baseSalary, InsuranceSalary = baseSalary, StartDate = startDate.Date, IsActive = true, ExpenseAccount = tpl
        };
        _db.Employees.Add(e);
        await _db.SaveChangesAsync();
        _hr.LogHistory(e.Id, HistoryKind.Hired, null, $"{e.Position} – {e.Department}", $"Tuyển dụng qua {job.Code} {job.Title}", startDate);
        c.Stage = CandidateStage.Hired; c.EmployeeId = e.Id;
        var hired = await _db.Candidates.CountAsync(x => x.JobOpeningId == job.Id && x.Stage == CandidateStage.Hired && x.Id != c.Id) + 1;
        if (hired >= job.Quantity) job.Status = JobStatus.Closed;
        await _db.SaveChangesAsync();
        return e;
    }

    public static void Recalc(Appraisal a, IEnumerable<AppraisalCriterion> criteria)
    {
        var list = criteria.ToList();
        var w = list.Sum(c => c.Weight);
        a.Total = w == 0 ? 0 : Math.Round(a.Scores.Sum(s => s.Score * (list.FirstOrDefault(c => c.Id == s.AppraisalCriterionId)?.Weight ?? 0)) / (decimal)w, 1);
        a.Rating = Appraisal.RatingOf(a.Total);
    }

    public static Dictionary<string, decimal> BonusTable(string spec) =>
        spec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Where(p => p.Length == 2 && decimal.TryParse(p[1], out _))
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => decimal.Parse(p[1]));

    /// <summary>Chuyển thưởng hiệu suất của kỳ đánh giá đã chốt vào thưởng tháng (tính trong bảng lương)</summary>
    public async Task<int> PushBonusesAsync(AppraisalCycle cycle, int year, int month)
    {
        var table = BonusTable(cycle.BonusPct);
        var list = await _db.Appraisals.Include(a => a.Employee).Where(a => a.AppraisalCycleId == cycle.Id && a.Status == AppraisalStatus.Approved).ToListAsync();
        var desc = $"Thưởng hiệu suất {cycle.Name}";
        var n = 0;
        foreach (var a in list)
        {
            var pct = table.GetValueOrDefault(a.Rating ?? "D");
            if (pct <= 0 || a.Employee == null) continue;
            if (await _db.PayrollBonuses.AnyAsync(b => b.EmployeeId == a.EmployeeId && b.Year == year && b.Month == month && b.Description == desc)) continue;
            _db.PayrollBonuses.Add(new PayrollBonus { EmployeeId = a.EmployeeId, Year = year, Month = month, Description = desc, Amount = Math.Round(a.Employee.BaseSalary * pct / 100, 0), Taxable = true });
            n++;
        }
        await _db.SaveChangesAsync();
        return n;
    }

    public async Task CompleteTrainingAsync(TrainingAttendee a, TrainingCourse c, TrainingStatus status, int? score, string? cert)
    {
        var was = a.Status;
        a.Status = status; a.Score = score; a.CertificateNo = cert;
        if (status == TrainingStatus.Completed && was != TrainingStatus.Completed)
            _hr.LogHistory(a.EmployeeId, HistoryKind.Training, null, c.Title, $"{c.Hours:0.#} giờ" + (cert != null ? $" · chứng chỉ {cert}" : ""), c.ToDate);
        await _db.SaveChangesAsync();
    }
}

/// <summary>Đề xuất mức tồn kho an toàn / điểm đặt hàng lại từ lịch sử xuất kho và thời gian giao hàng của nhà cung cấp</summary>
public record ReorderRow(Item Item, decimal OnHand, decimal OnOrder, decimal DailyUsage, decimal StdDev, int LeadDays, decimal Safety, decimal ReorderPoint, decimal MaxLevel, decimal SuggestQty, int Days);

public class StockPlanningService
{
    private readonly ErpDbContext _db; private readonly SettingsService _set; private readonly CurrentUser _user;
    public StockPlanningService(ErpDbContext db, SettingsService set, CurrentUser user) { _db = db; _set = set; _user = user; }

    /// <summary>
    /// Nhu cầu bình quân / ngày và độ lệch chuẩn theo tuần từ phiếu xuất kho đã ghi sổ trong N ngày;
    /// tồn an toàn = z × σ(ngày) × √(thời gian giao hàng); điểm đặt hàng = nhu cầu × thời gian giao hàng + tồn an toàn;
    /// mức tối đa = điểm đặt hàng + nhu cầu × chu kỳ đặt hàng.
    /// </summary>
    public async Task<List<ReorderRow>> SuggestAsync(int days = 90, decimal z = 1.65m, int reviewDays = 14)
    {
        var to = DateTime.Today; var from = to.AddDays(-days);
        var issues = await _db.InventoryDocLines.Where(l => l.InventoryDoc!.Status == DocStatus.Posted && l.InventoryDoc.Type == InvDocType.Issue && l.InventoryDoc.Date >= from && l.InventoryDoc.Date <= to)
            .Select(l => new { l.ItemId, l.InventoryDoc!.Date, l.Quantity }).ToListAsync();
        var defLead = (int)await _set.DecAsync("Stock.DefaultLeadDays", 7);
        // thời gian giao hàng thực tế: ngày đơn mua → ngày nhập kho đầu tiên theo đơn đó
        var receipts = await _db.InventoryDocs.Where(d => d.Origin == DocOrigin.Purchase && d.Status == DocStatus.Posted && d.OriginId != null)
            .Select(d => new { d.OriginId, d.Date, Items = d.Lines.Select(l => l.ItemId) }).ToListAsync();
        var poDates = await _db.PurchaseOrders.Select(p => new { p.Id, p.Date }).ToDictionaryAsync(p => p.Id, p => p.Date);
        var lead = receipts.Where(r => poDates.ContainsKey(r.OriginId!.Value)).SelectMany(r => r.Items.Select(i => (Item: i, Days: Math.Max(1, (r.Date - poDates[r.OriginId!.Value]).Days))))
            .GroupBy(x => x.Item).ToDictionary(g => g.Key, g => (int)Math.Ceiling(g.Average(x => x.Days)));
        var stock = await _db.StockLevels.GroupBy(s => s.ItemId).Select(g => new { g.Key, Q = g.Sum(s => s.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        var onOrder = (await _db.PurchaseOrderLines.Where(l => (l.PurchaseOrder!.Status == PoStatus.Approved || l.PurchaseOrder.Status == PoStatus.PartiallyReceived))
            .Select(l => new { l.ItemId, Open = l.Quantity - l.ReceivedQty }).ToListAsync()).Where(x => x.Open > 0).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Open));
        var items = await _db.Items.Where(i => i.IsActive && i.Type != ItemType.Service).ToListAsync();
        var weeks = Math.Max(1, days / 7);
        var rows = new List<ReorderRow>();
        foreach (var i in items)
        {
            var mine = issues.Where(x => x.ItemId == i.Id).ToList();
            if (mine.Count == 0) continue;
            var total = mine.Sum(x => x.Quantity);
            var daily = total / days;
            var weekly = Enumerable.Range(0, weeks).Select(w => mine.Where(x => (x.Date - from).Days / 7 == w).Sum(x => x.Quantity)).ToList();
            var mean = weekly.Average();
            var sdWeek = (decimal)Math.Sqrt((double)(weekly.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, weekly.Count - 1)));
            var sdDay = sdWeek / (decimal)Math.Sqrt(7);
            var ld = lead.GetValueOrDefault(i.Id, defLead);
            var safety = Math.Ceiling(z * sdDay * (decimal)Math.Sqrt(ld));
            var rop = Math.Ceiling(daily * ld + safety);
            var max = Math.Ceiling(rop + daily * reviewDays);
            var oh = stock.GetValueOrDefault(i.Id); var oo = onOrder.GetValueOrDefault(i.Id);
            var suggest = oh + oo <= rop ? Math.Max(0, max - oh - oo) : 0;
            rows.Add(new ReorderRow(i, oh, oo, Math.Round(daily, 3), Math.Round(sdDay, 3), ld, safety, rop, max, suggest, days));
        }
        return rows.OrderByDescending(r => r.SuggestQty > 0).ThenBy(r => r.Item.Code).ToList();
    }

    /// <summary>Ghi mức tồn tối thiểu (= điểm đặt hàng) và tối đa vào danh mục hàng</summary>
    public async Task<int> ApplyAsync(IEnumerable<ReorderRow> rows)
    {
        var n = 0;
        foreach (var r in rows) { var i = await _db.Items.FindAsync(r.Item.Id); if (i == null) continue; i.MinStock = r.ReorderPoint; i.MaxStock = r.MaxLevel; n++; }
        await _db.SaveChangesAsync();
        return n;
    }

    /// <summary>Lập đề nghị mua hàng cho các mặt hàng dưới điểm đặt hàng (số lượng = đến mức tối đa, trừ hàng đang về)</summary>
    public async Task<PurchaseRequisition?> CreateRequisitionAsync(IEnumerable<ReorderRow> rows)
    {
        var list = rows.Where(r => r.SuggestQty > 0).ToList();
        if (list.Count == 0) return null;
        var pr = new PurchaseRequisition
        {
            Number = await DocNumbers.NextAsync(_db.PurchaseRequisitions.Select(p => p.Number), "DNM", DateTime.Today), Date = DateTime.Today,
            RequiredDate = DateTime.Today.AddDays(list.Min(r => r.LeadDays)), Department = "Kho", Purpose = "Bổ sung tồn kho theo điểm đặt hàng", Source = "Điểm đặt hàng", RequesterId = _user.Id
        };
        foreach (var r in list) pr.Lines.Add(new PurchaseRequisitionLine { ItemId = r.Item.Id, Quantity = r.SuggestQty, EstimatedPrice = r.Item.AvgCost });
        _db.PurchaseRequisitions.Add(pr);
        await _db.SaveChangesAsync();
        return pr;
    }
}

/// <summary>Một khối lịch: công đoạn của lệnh sản xuất trên trung tâm sản xuất</summary>
public record ScheduleSlot(ProductionOrder Order, ProductionOperation Op, WorkCenter Center, DateTime Start, DateTime End, bool Late);

/// <summary>
/// Lập lịch sản xuất theo năng lực hữu hạn: xếp lần lượt các công đoạn chưa xong của lệnh đang mở (ưu tiên hạn hoàn thành sớm),
/// mỗi trung tâm làm một việc một lúc, số giờ làm mỗi ngày = giờ/ngày × hiệu suất, bỏ ngày Chủ nhật; công đoạn sau bắt đầu khi công đoạn trước xong.
/// </summary>
public class CapacityScheduler
{
    private readonly ErpDbContext _db;
    public CapacityScheduler(ErpDbContext db) => _db = db;

    public static readonly ProductionStatus[] OpenStatuses = { ProductionStatus.Draft, ProductionStatus.Approved, ProductionStatus.Released, ProductionStatus.InProgress };

    public async Task<List<ScheduleSlot>> ScheduleAsync(DateTime start, bool save)
    {
        var orders = await _db.ProductionOrders.Include(o => o.Item).Include(o => o.Operations).ThenInclude(op => op.WorkCenter)
            .Where(o => OpenStatuses.Contains(o.Status)).ToListAsync();
        var free = new Dictionary<int, DateTime>();   // trung tâm → thời điểm rảnh
        var slots = new List<ScheduleSlot>();
        foreach (var o in orders.OrderBy(o => o.DueDate).ThenBy(o => o.StartDate).ThenBy(o => o.Id))
        {
            var t = Max(start, o.StartDate);
            foreach (var op in o.Operations.Where(x => x.Status != OperationStatus.Done).OrderBy(x => x.Seq))
            {
                var wc = op.WorkCenter;
                if (wc == null) continue;
                var remainingMin = Math.Max(0, op.PlannedMinutes - op.ActualMinutes);
                var s = Max(t, free.GetValueOrDefault(wc.Id, start));
                s = NextWorking(s, wc);
                var e = Add(s, remainingMin / 60m, wc);
                free[wc.Id] = e;
                t = e;
                if (save) { op.PlannedStart = s; op.PlannedEnd = e; }
                slots.Add(new ScheduleSlot(o, op, wc, s, e, e.Date > o.DueDate.Date));
            }
        }
        if (save) await _db.SaveChangesAsync();
        return slots;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static decimal Hours(WorkCenter wc) => Math.Max(0.5m, wc.HoursPerDay * wc.Efficiency / 100m);
    private static DateTime DayStart(DateTime d) => d.Date.AddHours(8);

    private static DateTime NextWorking(DateTime t, WorkCenter wc)
    {
        if (t.DayOfWeek == DayOfWeek.Sunday) t = DayStart(t.AddDays(1));
        if (t < DayStart(t)) t = DayStart(t);
        var end = DayStart(t).AddHours((double)Hours(wc));
        if (t >= end) { t = DayStart(t.AddDays(1)); if (t.DayOfWeek == DayOfWeek.Sunday) t = t.AddDays(1); }
        return t;
    }

    /// <summary>Cộng số giờ làm việc theo lịch của trung tâm</summary>
    public static DateTime Add(DateTime s, decimal hours, WorkCenter wc)
    {
        var t = NextWorking(s, wc);
        var left = hours;
        while (left > 0)
        {
            var dayEnd = DayStart(t).AddHours((double)Hours(wc));
            var avail = (decimal)(dayEnd - t).TotalHours;
            if (left <= avail) return t.AddHours((double)left);
            left -= avail;
            t = NextWorking(dayEnd, wc);
        }
        return t;
    }
}

/// <summary>Bộ phận hỗ trợ khách hàng: SLA, tra cứu bảo hành theo hóa đơn bán</summary>
public class ServiceDesk
{
    private readonly ErpDbContext _db;
    public ServiceDesk(ErpDbContext db) => _db = db;

    public record WarrantyInfo(Invoice? Invoice, DateTime? Until, bool InWarranty, string Message);

    public async Task<WarrantyInfo> WarrantyAsync(int partnerId, int? itemId, string? serial)
    {
        if (itemId == null) return new(null, null, false, "Chưa chọn sản phẩm.");
        var item = await _db.Items.FindAsync(itemId);
        if (item == null) return new(null, null, false, "Không tìm thấy sản phẩm.");
        var line = await _db.InvoiceLines.Include(l => l.Invoice).Where(l => l.ItemId == itemId && l.Invoice!.PartnerId == partnerId && l.Invoice.Direction == InvoiceDirection.Sales && l.Invoice.Status == InvoiceStatus.Issued)
            .OrderByDescending(l => l.Invoice!.Date).FirstOrDefaultAsync();
        if (line == null) return new(null, null, false, "Không tìm thấy hóa đơn bán sản phẩm này cho khách hàng.");
        if (item.WarrantyMonths <= 0) return new(line.Invoice, null, false, $"Sản phẩm không khai báo thời hạn bảo hành (mua theo hóa đơn {line.Invoice!.InvoiceNo} ngày {line.Invoice.Date:dd/MM/yyyy}).");
        var until = line.Invoice!.Date.AddMonths(item.WarrantyMonths);
        var ok = until >= DateTime.Today;
        return new(line.Invoice, until, ok, ok ? $"Còn bảo hành đến {until:dd/MM/yyyy} (hóa đơn {line.Invoice.InvoiceNo})." : $"Hết bảo hành từ {until:dd/MM/yyyy} (hóa đơn {line.Invoice.InvoiceNo}).");
    }
}
