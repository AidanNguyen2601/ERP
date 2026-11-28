using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using Xunit;

namespace ERP.Tests;

public class DepthLogicTests
{
    private static readonly WorkCenter Wc = new() { Id = 1, Code = "WC", Name = "Máy", HoursPerDay = 8, Efficiency = 100 };

    [Fact]
    public void Scheduler_AddsHoursWithinWorkingDay()
    {
        var start = new DateTime(2026, 11, 30, 8, 0, 0);   // thứ Hai
        Assert.Equal(new DateTime(2026, 11, 30, 12, 0, 0), CapacityScheduler.Add(start, 4, Wc));
    }

    [Fact]
    public void Scheduler_SpillsToNextDay_AndSkipsSunday()
    {
        var sat = new DateTime(2026, 12, 5, 14, 0, 0);     // thứ Bảy 14h, còn 2 giờ trong ngày
        Assert.Equal(new DateTime(2026, 12, 7, 12, 0, 0), CapacityScheduler.Add(sat, 6, Wc));   // 2h thứ Bảy + 4h thứ Hai (bỏ Chủ nhật)
    }

    [Fact]
    public void Scheduler_EfficiencyReducesDailyCapacity()
    {
        var wc = new WorkCenter { HoursPerDay = 8, Efficiency = 50 };
        Assert.Equal(new DateTime(2026, 12, 1, 12, 0, 0), CapacityScheduler.Add(new DateTime(2026, 11, 30, 8, 0, 0), 8, wc));   // 4 giờ/ngày → 2 ngày
    }

    [Theory]
    [InlineData(95, "A")] [InlineData(90, "A")] [InlineData(80, "B")] [InlineData(60, "C")] [InlineData(30, "D")]
    public void Appraisal_Rating(decimal total, string rating) => Assert.Equal(rating, Appraisal.RatingOf(total));

    [Fact]
    public void Appraisal_WeightedTotal()
    {
        var c1 = new AppraisalCriterion { Id = 1, Weight = 60 }; var c2 = new AppraisalCriterion { Id = 2, Weight = 40 };
        var a = new Appraisal();
        a.Scores.Add(new AppraisalScore { AppraisalCriterionId = 1, Score = 100 });
        a.Scores.Add(new AppraisalScore { AppraisalCriterionId = 2, Score = 50 });
        TalentService.Recalc(a, new[] { c1, c2 });
        Assert.Equal(80, a.Total);
        Assert.Equal("B", a.Rating);
    }

    [Fact]
    public void BonusTable_Parses() => Assert.Equal(50, TalentService.BonusTable("A:100;B:50;C:0")["B"]);

    [Theory]
    [InlineData(TicketPriority.Urgent, 4)] [InlineData(TicketPriority.High, 8)] [InlineData(TicketPriority.Normal, 24)] [InlineData(TicketPriority.Low, 72)]
    public void Ticket_Sla(TicketPriority p, int hours) => Assert.Equal(hours, ServiceTicket.SlaHours(p));
}

[Collection("erp")]
public class DepthIntegrationTests
{
    private readonly ErpFixture _f;
    public DepthIntegrationTests(ErpFixture f) => _f = f;
    private static int IdFrom(HttpResponseMessage r, string pattern)
    {
        Assert.True(r.Headers.Location != null, $"Không chuyển hướng: {(int)r.StatusCode}");
        var loc = r.Headers.Location!.ToString();
        var m = Regex.Match(loc, pattern);
        if (!m.Success) m = Regex.Match(loc, @"(\d+)\D*$");   // id dạng /Controller/Action/5
        Assert.True(m.Success, "Không đọc được id từ " + loc);
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public async Task Recruitment_HireCreatesEmployeeAndHistory_ClosesJob()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("ketoan");
        var r = await _f.PostAsync(c, "/Talent/Jobs", "/Talent/SaveJob", ("Id", "0"), ("Title", "Thợ mộc bậc 3"), ("Department", "Xưởng sản xuất"), ("Quantity", "1"), ("SalaryFrom", "9000000"), ("SalaryTo", "12000000"), ("Deadline", DateTime.Today.AddDays(30).ToString("yyyy-MM-dd")));
        var jobId = IdFrom(r, @"id=(\d+)");
        await _f.PostAsync(c, "/Talent/Jobs", "/Talent/AddCandidate", ("JobOpeningId", jobId.ToString()), ("FullName", "Ứng Viên Thử"), ("Phone", "0900000001"), ("Email", "uv@example.com"), ("ExpectedSalary", "10000000"));
        var cand = await _f.WithDbAsync(db => db.Candidates.FirstAsync(x => x.JobOpeningId == jobId));
        await _f.PostAsync(c, "/Talent/Jobs", "/Talent/MoveCandidate", ("id", cand.Id.ToString()), ("stage", ((int)CandidateStage.Offer).ToString()), ("score", "85"));
        r = await _f.PostAsync(c, "/Talent/Jobs", "/Talent/Hire", ("id", cand.Id.ToString()), ("baseSalary", "10000000"), ("startDate", DateTime.Today.ToString("yyyy-MM-dd")));
        Assert.Contains("/Hr/Profile/", r.Headers.Location!.ToString());
        var empId = IdFrom(r, @"/Hr/Profile/(\d+)");
        var (emp, hist, job, cd) = await _f.WithDbAsync(async db => (await db.Employees.FindAsync(empId), await db.EmployeeHistories.AnyAsync(h => h.EmployeeId == empId && h.Kind == HistoryKind.Hired),
            await db.JobOpenings.FindAsync(jobId), await db.Candidates.FindAsync(cand.Id)));
        Assert.Equal("Ứng Viên Thử", emp!.FullName); Assert.Equal(10_000_000, emp.BaseSalary); Assert.True(hist);
        Assert.Equal(JobStatus.Closed, job!.Status); Assert.Equal(CandidateStage.Hired, cd!.Stage); Assert.Equal(empId, cd.EmployeeId);
    }

    [Fact]
    public async Task Appraisal_ScoreApprove_PushesPerformanceBonusToPayroll()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("ketoan");
        var r = await _f.PostAsync(c, "/Talent/Appraisals", "/Talent/SaveCycle", ("name", "Đánh giá thử"), ("fromDate", "2026-01-01"), ("toDate", "2026-12-31"), ("bonusPct", "A:100;B:50;C:0;D:0"), ("criteria", "Khối lượng | 60 | KH\nChất lượng | 40 | lỗi"));
        var cycleId = IdFrom(r, @"cycleId=(\d+)");
        var (appr, crit) = await _f.WithDbAsync(async db => (await db.Appraisals.Include(a => a.Employee).Where(a => a.AppraisalCycleId == cycleId).OrderBy(a => a.Id).FirstAsync(),
            await db.AppraisalCriteria.Where(x => x.AppraisalCycleId == cycleId).OrderBy(x => x.Id).ToListAsync()));
        var sr = await _f.PostAsync(c, $"/Talent/Appraisal/{appr.Id}", $"/Talent/ScoreAppraisal/{appr.Id}", ($"score[{crit[0].Id}]", "90"), ($"score[{crit[1].Id}]", "80"), ("managerComment", "Tốt"), ("op", "approve"));
        Assert.True(sr.StatusCode == HttpStatusCode.Redirect, $"Chấm điểm: {(int)sr.StatusCode} {(await sr.Content.ReadAsStringAsync())[..Math.Min(400, (await sr.Content.ReadAsStringAsync()).Length)]}");
        var flash = WebUtility.HtmlDecode(await c.GetStringAsync(sr.Headers.Location!.ToString()));
        var a2 = await _f.WithDbAsync(db => db.Appraisals.FirstAsync(a => a.Id == appr.Id));
        Assert.True(a2.Total > 0, "Thông báo: " + Regex.Match(flash, "alert alert-[a-z]+[^>]*>([^<]*)").Groups[1].Value);
        Assert.Equal(86, a2.Total); Assert.Equal("B", a2.Rating); Assert.Equal(AppraisalStatus.Approved, a2.Status);
        var target = DateTime.Today.AddMonths(7);
        await _f.PostAsync(c, "/Talent/Appraisals", "/Talent/PushBonus", ("cycleId", cycleId.ToString()), ("year", target.Year.ToString()), ("month", target.Month.ToString()));
        var bonus = await _f.WithDbAsync(db => db.PayrollBonuses.FirstOrDefaultAsync(b => b.EmployeeId == appr.EmployeeId && b.Year == target.Year && b.Month == target.Month && b.Description.Contains("Đánh giá thử")));
        Assert.NotNull(bonus);
        Assert.Equal(Math.Round(appr.Employee!.BaseSalary * 0.5m, 0), bonus!.Amount);
        Assert.True(await _f.WithDbAsync(db => db.AppraisalCycles.Where(x => x.Id == cycleId).Select(x => x.IsClosed).FirstAsync()));
    }

    [Fact]
    public async Task Training_CompletionWritesWorkHistory_AssetsIssueAndReturn()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("ketoan");
        var emp = await _f.WithDbAsync(db => db.Employees.Where(e => e.IsActive).OrderBy(e => e.Id).Skip(2).FirstAsync());
        var r = await _f.PostAsync(c, "/Talent/Training", "/Talent/SaveCourse", ("Title", "An toàn vận hành máy cưa"), ("FromDate", "2026-11-01"), ("ToDate", "2026-11-02"), ("Hours", "16"), ("Cost", "3000000"),
            ("ValidMonths", "12"), ("Mandatory", "true"), ("employeeIds", emp.Id.ToString()));
        var courseId = IdFrom(r, @"id=(\d+)");
        var att = await _f.WithDbAsync(db => db.TrainingAttendees.FirstAsync(a => a.TrainingCourseId == courseId));
        await _f.PostAsync(c, "/Talent/Training", "/Talent/Result", ("id", att.Id.ToString()), ("status", ((int)TrainingStatus.Completed).ToString()), ("score", "90"), ("cert", "ATLD-01"));
        Assert.True(await _f.WithDbAsync(db => db.EmployeeHistories.AnyAsync(h => h.EmployeeId == emp.Id && h.Kind == HistoryKind.Training && h.ToValue == "An toàn vận hành máy cưa")));
        // tài sản
        await _f.PostAsync(c, "/Talent/Assets", "/Talent/Issue", ("EmployeeId", emp.Id.ToString()), ("Description", "Laptop Dell 5420"), ("SerialNo", "SN123"), ("Quantity", "1"), ("IssuedDate", DateTime.Today.ToString("yyyy-MM-dd")));
        var issue = await _f.WithDbAsync(db => db.EmployeeAssetIssues.OrderByDescending(x => x.Id).FirstAsync());
        Assert.Contains("Laptop Dell 5420", await c.GetStringAsync($"/Talent/Handover?employeeId={emp.Id}"));
        await _f.PostAsync(c, "/Talent/Assets", "/Talent/Return", ("id", issue.Id.ToString()), ("condition", "Tốt"));
        Assert.NotNull((await _f.WithDbAsync(db => db.EmployeeAssetIssues.FirstAsync(x => x.Id == issue.Id))).ReturnedDate);
    }

    [Fact]
    public async Task SelfService_ShowsOwnDataOnly_AndSubmitsLeave()
    {
        if (!_f.Enabled) return;
        var (uid, empId, otherLine) = await _f.WithDbAsync(async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Username == "thukho");
            var e = await db.Employees.Where(x => x.IsActive).OrderBy(x => x.Id).Skip(5).FirstAsync();
            u.EmployeeId = e.Id; await db.SaveChangesAsync();
            var other = await db.PayrollLines.Where(l => l.EmployeeId != e.Id).Select(l => l.Id).FirstOrDefaultAsync();
            return (u.Id, e.Id, other);
        });
        var c = await _f.LoginAsync("thukho");
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/My"));
        var emp = await _f.WithDbAsync(db => db.Employees.FirstAsync(e => e.Id == empId));
        Assert.Contains(emp.FullName, html);
        Assert.Contains("Phép năm", html);
        if (otherLine > 0) Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/My/Payslip?lineId={otherLine}")).StatusCode);
        var pl = await _f.WithDbAsync(db => db.LeaveTypes.FirstAsync(t => t.Code == "PN"));
        var day = DateTime.Today.AddDays(60); while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        await _f.PostAsync(c, "/My", "/My/RequestLeave", ("leaveTypeId", pl.Id.ToString()), ("fromDate", day.ToString("yyyy-MM-dd")), ("toDate", day.ToString("yyyy-MM-dd")), ("days", "1"), ("reason", "việc nhà"));
        var req = await _f.WithDbAsync(db => db.LeaveRequests.FirstOrDefaultAsync(l => l.EmployeeId == empId && l.FromDate == day.Date));
        Assert.NotNull(req); Assert.Equal(LeaveStatus.Pending, req!.Status);
        await _f.PostAsync(c, "/My", "/My/CancelLeave", ("id", req.Id.ToString()));
        Assert.Equal(LeaveStatus.Cancelled, (await _f.WithDbAsync(db => db.LeaveRequests.FirstAsync(l => l.Id == req.Id))).Status);
        // tài khoản chưa gắn
        var n = await _f.LoginAsync("sanxuat");
        await _f.WithDbAsync(async db => { var u = await db.Users.FirstAsync(x => x.Username == "sanxuat"); u.EmployeeId = null; u.Email = null; await db.SaveChangesAsync(); return 0; });
        Assert.Contains("chưa gắn", WebUtility.HtmlDecode(await n.GetStringAsync("/My")));
    }

    [Fact]
    public async Task Reorder_SuggestsAndCreatesRequisition()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Planning/Reorder?days=365")).StatusCode);
        var rows = await _f.WithDbAsync(async db =>
        {
            using var scope = _f.Factory!.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<StockPlanningService>().SuggestAsync(365);
        });
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.ReorderPoint >= r.Safety && r.MaxLevel >= r.ReorderPoint));
        var item = rows.First();
        // ép mặt hàng dưới điểm đặt hàng bằng cách tính với chu kỳ dài → mức tối đa cao
        var r2 = await _f.PostAsync(c, "/Planning/Reorder?days=365", "/Planning/CreateRequisition", ("days", "365"), ("z", "1.65"), ("review", "14"), ("itemIds", item.Item.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r2.StatusCode);
        if (item.SuggestQty > 0)
            Assert.True(await _f.WithDbAsync(db => db.PurchaseRequisitions.AnyAsync(p => p.Source == "Điểm đặt hàng" && p.Lines.Any(l => l.ItemId == item.Item.Id))));
        await _f.PostAsync(c, "/Planning/Reorder?days=365", "/Planning/ApplyLevels", ("days", "365"), ("z", "1.65"), ("review", "14"), ("itemIds", item.Item.Id.ToString()));
        Assert.Equal(item.ReorderPoint, await _f.WithDbAsync(db => db.Items.Where(i => i.Id == item.Item.Id).Select(i => i.MinStock).FirstAsync()));
    }

    [Fact]
    public async Task StocktakeScan_AccumulatesCount()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var wh = await _f.WithDbAsync(db => db.StockLevels.Where(s => s.Quantity > 0).Select(s => s.WarehouseId).FirstAsync());
        var r = await _f.PostAsync(c, "/Stock/Stocktakes", "/Stock/CreateStocktake", ("warehouseId", wh.ToString()), ("date", DateTime.Today.ToString("yyyy-MM-dd")), ("note", "quét thử"));
        var stId = IdFrom(r, @"/Stock/Stocktake/(\d+)");
        var item = await _f.WithDbAsync(db => db.StocktakeLines.Include(l => l.Item).Where(l => l.StocktakeId == stId).Select(l => l.Item!).FirstAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/Planning/Scan/{stId}")).StatusCode);
        var j1 = await (await _f.PostAsync(c, $"/Planning/Scan/{stId}", $"/Planning/ScanCount/{stId}", ("code", item.Barcode ?? item.Code), ("qty", "2"))).Content.ReadAsStringAsync();
        var j2 = await (await _f.PostAsync(c, $"/Planning/Scan/{stId}", $"/Planning/ScanCount/{stId}", ("code", item.Code), ("qty", "3"))).Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", j1); Assert.Contains("\"counted\":5", j2);
        var j3 = await (await _f.PostAsync(c, $"/Planning/Scan/{stId}", $"/Planning/ScanCount/{stId}", ("code", "KHONG-TON-TAI"), ("qty", "1"))).Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":false", j3);
    }

    [Fact]
    public async Task Schedule_IsCapacityFeasible_AndSaves()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Planning/Schedule")).StatusCode);
        await _f.PostAsync(c, "/Planning/Schedule", "/Planning/SaveSchedule", ("start", DateTime.Today.ToString("yyyy-MM-dd")));
        var ops = await _f.WithDbAsync(db => db.ProductionOperations.Where(o => o.PlannedStart != null && o.WorkCenterId != null).ToListAsync());
        if (ops.Count == 0) return;
        // không có hai công đoạn chồng giờ trên cùng trung tâm; công đoạn sau của cùng lệnh không bắt đầu trước công đoạn trước
        foreach (var g in ops.GroupBy(o => o.WorkCenterId))
        {
            var list = g.OrderBy(o => o.PlannedStart).ToList();
            for (var i = 1; i < list.Count; i++) Assert.True(list[i].PlannedStart >= list[i - 1].PlannedEnd, "Chồng lịch trên trung tâm " + g.Key);
        }
        foreach (var g in ops.GroupBy(o => o.ProductionOrderId))
        {
            var list = g.OrderBy(o => o.Seq).ToList();
            for (var i = 1; i < list.Count; i++) Assert.True(list[i].PlannedStart >= list[i - 1].PlannedEnd, "Sai thứ tự công đoạn lệnh " + g.Key);
        }
    }

    [Fact]
    public async Task Campaign_Roi_AndTicket_SlaWarranty()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("kinhdoanh");
        await _f.PostAsync(c, "/Service/Campaigns", "/Service/SaveCampaign", ("Name", "Hội chợ nội thất"), ("Channel", "3"), ("FromDate", "2026-10-01"), ("ToDate", "2026-10-05"), ("Budget", "20000000"), ("ActualCost", "10000000"), ("TargetLeads", "10"));
        var camp = await _f.WithDbAsync(db => db.Campaigns.OrderByDescending(x => x.Id).FirstAsync());
        await _f.WithDbAsync(async db =>
        {
            var l = new Lead { Code = "KHTN-T1", Name = "KH chiến dịch", CampaignId = camp.Id, Stage = LeadStage.Converted, CreatedAt = DateTime.Now };
            db.Leads.Add(l); await db.SaveChangesAsync();
            db.Opportunities.Add(new Opportunity { Code = "CH-T1", Title = "Đơn từ hội chợ", LeadId = l.Id, Stage = OppStage.Won, Amount = 30_000_000, Probability = 100, ClosedAt = DateTime.Now });
            await db.SaveChangesAsync(); return 0;
        });
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/Service/Campaigns"));
        Assert.Contains("200%", html);   // (30tr − 10tr) / 10tr
        // phiếu bảo hành
        var (partner, item) = await _f.WithDbAsync(async db =>
        {
            var line = await db.InvoiceLines.Include(l => l.Invoice).Where(l => l.ItemId != null && l.Invoice!.Direction == InvoiceDirection.Sales && l.Invoice.Status == InvoiceStatus.Issued).OrderByDescending(l => l.Invoice!.Date).FirstAsync();
            var it = await db.Items.FindAsync(line.ItemId); it!.WarrantyMonths = 24; await db.SaveChangesAsync();
            return (line.Invoice!.PartnerId!.Value, it.Id);
        });
        var r = await _f.PostAsync(c, "/Service/Tickets", "/Service/CreateTicket", ("PartnerId", partner.ToString()), ("Subject", "Ghế bị gãy chân"), ("Category", ((int)TicketCategory.Warranty).ToString()),
            ("Priority", ((int)TicketPriority.High).ToString()), ("ItemId", item.ToString()), ("Description", "Khách báo lỗi"));
        var tid = IdFrom(r, @"/Service/Ticket/(\d+)");
        var t = await _f.WithDbAsync(db => db.ServiceTickets.FirstAsync(x => x.Id == tid));
        Assert.Equal(8, Math.Round((t.DueAt - t.CreatedAt).TotalHours));
        Assert.Contains("Còn bảo hành", t.Description);
        Assert.NotNull(t.InvoiceId);
        // đóng phiếu phải có kết quả
        await _f.PostAsync(c, $"/Service/Ticket/{tid}", $"/Service/UpdateTicket/{tid}", ("status", ((int)TicketStatus.Resolved).ToString()), ("priority", ((int)TicketPriority.High).ToString()), ("resolution", ""));
        Assert.Equal(TicketStatus.New, (await _f.WithDbAsync(db => db.ServiceTickets.FirstAsync(x => x.Id == tid))).Status);
        await _f.PostAsync(c, $"/Service/Ticket/{tid}", $"/Service/UpdateTicket/{tid}", ("status", ((int)TicketStatus.Resolved).ToString()), ("priority", ((int)TicketPriority.High).ToString()), ("resolution", "Thay chân ghế"));
        var done = await _f.WithDbAsync(db => db.ServiceTickets.FirstAsync(x => x.Id == tid));
        Assert.Equal(TicketStatus.Resolved, done.Status); Assert.NotNull(done.ResolvedAt); Assert.False(done.IsLate);
    }
}
