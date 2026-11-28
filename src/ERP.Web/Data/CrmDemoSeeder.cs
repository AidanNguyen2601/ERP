using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>Dữ liệu mẫu CRM (khách hàng tiềm năng, cơ hội, tương tác) và quy tắc cảnh báo mặc định</summary>
public static class CrmDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        try { await sp.GetRequiredService<AlertService>().EnsureRulesAsync(); }
        catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Quy tắc cảnh báo chưa nạp được: {Msg}", ex.Message); }
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedProcessDemo", true)) return;
        try
        {
            if (await db.Leads.AnyAsync()) return;
            var users = await db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Admin)).OrderBy(u => u.Id).ToListAsync();
            int? o1 = users.FirstOrDefault()?.Id, o2 = users.Skip(1).FirstOrDefault()?.Id ?? o1;
            var customers = await db.Partners.Where(p => p.IsCustomer).OrderBy(p => p.Id).Take(3).ToListAsync();
            var today = DateTime.Today;
            var leads = new[]
            {
                new Lead { Code = "KHTN-0001", Name = "Công ty TNHH Thực phẩm Bình An", ContactName = "Nguyễn Văn Hòa", ContactTitle = "Giám đốc mua hàng", Phone = "0903111222", Email = "hoa@binhan.example", Source = LeadSource.Website, Stage = LeadStage.New, OwnerId = o1, EstimatedValue = 250_000_000, Note = "Hỏi báo giá bao bì thực phẩm 50.000 sp/tháng", CreatedAt = today.AddDays(-2) },
                new Lead { Code = "KHTN-0002", Name = "Siêu thị Minh Phát", ContactName = "Trần Thị Mai", ContactTitle = "Trưởng phòng kinh doanh", Phone = "0912333444", Email = "mai@minhphat.example", Source = LeadSource.Event, Stage = LeadStage.Contacted, OwnerId = o1, EstimatedValue = 480_000_000, Note = "Gặp tại hội chợ, quan tâm hợp đồng cung cấp dài hạn", CreatedAt = today.AddDays(-12), LastContactAt = today.AddDays(-5) },
                new Lead { Code = "KHTN-0003", Name = "Nhà hàng Hải Sản Biển Đông", ContactName = "Lê Quang Vinh", Phone = "0987555666", Source = LeadSource.Referral, Stage = LeadStage.Qualified, OwnerId = o2, EstimatedValue = 120_000_000, Note = "Khách hiện hữu giới thiệu", CreatedAt = today.AddDays(-20), LastContactAt = today.AddDays(-3) },
                new Lead { Code = "KHTN-0004", Name = "Công ty CP Xây dựng Hoàng Gia", ContactName = "Phạm Đức Thành", Phone = "0933777888", Source = LeadSource.Phone, Stage = LeadStage.Lost, OwnerId = o2, EstimatedValue = 90_000_000, LostReason = "Đã ký với nhà cung cấp khác", CreatedAt = today.AddDays(-40), LastContactAt = today.AddDays(-30) },
            };
            db.Leads.AddRange(leads);
            await db.SaveChangesAsync();

            var opps = new List<Opportunity>();
            if (customers.Count > 0)
            {
                opps.Add(new Opportunity { Code = "CH-0001", Title = "Hợp đồng cung cấp quý IV", PartnerId = customers[0].Id, Stage = OppStage.Negotiation, Amount = 650_000_000, Probability = 75, ExpectedClose = today.AddDays(10), OwnerId = o1, CreatedAt = today.AddDays(-25) });
                if (customers.Count > 1) opps.Add(new Opportunity { Code = "CH-0002", Title = "Mở rộng dòng sản phẩm mới", PartnerId = customers[1].Id, Stage = OppStage.Proposal, Amount = 320_000_000, Probability = 50, ExpectedClose = today.AddDays(25), OwnerId = o2, CreatedAt = today.AddDays(-14) });
                if (customers.Count > 2) opps.Add(new Opportunity { Code = "CH-0003", Title = "Đơn hàng khuyến mại cuối năm", PartnerId = customers[2].Id, Stage = OppStage.Won, Amount = 210_000_000, Probability = 100, ExpectedClose = today.AddDays(-5), OwnerId = o1, CreatedAt = today.AddDays(-30), ClosedAt = today.AddDays(-5) });
                opps.Add(new Opportunity { Code = "CH-0004", Title = "Dự án nhượng quyền phân phối", PartnerId = customers[0].Id, Stage = OppStage.Lost, Amount = 150_000_000, Probability = 0, ExpectedClose = today.AddDays(-15), OwnerId = o2, LostReason = "Giá cao hơn đối thủ", CreatedAt = today.AddDays(-50), ClosedAt = today.AddDays(-15) });
            }
            opps.Add(new Opportunity { Code = "CH-0005", Title = "Cung cấp cho chuỗi nhà hàng Biển Đông", LeadId = leads[2].Id, Stage = OppStage.Qualified, Amount = 120_000_000, Probability = 30, ExpectedClose = today.AddDays(40), OwnerId = o2, CreatedAt = today.AddDays(-3) });
            db.Opportunities.AddRange(opps);
            await db.SaveChangesAsync();

            var acts = new List<CrmActivity>
            {
                new() { Kind = ActivityKind.Call, Subject = "Gọi giới thiệu năng lực công ty", Outcome = "Khách hẹn gặp tuần sau", LeadId = leads[1].Id, OwnerId = o1, Date = today.AddDays(-5).AddHours(9), DurationMinutes = 12 },
                new() { Kind = ActivityKind.Meeting, Subject = "Gặp khảo sát nhu cầu tại cửa hàng", Outcome = "Cần báo giá theo 3 mức số lượng", LeadId = leads[2].Id, OwnerId = o2, Date = today.AddDays(-3).AddHours(14), DurationMinutes = 60 },
                new() { Kind = ActivityKind.Task, Subject = "Liên hệ lần đầu khách đăng ký trên website", LeadId = leads[0].Id, OwnerId = o1, Date = today.AddDays(-1).AddHours(17), Done = false },
                new() { Kind = ActivityKind.Task, Subject = "Gửi báo giá theo 3 mức số lượng", LeadId = leads[2].Id, OwnerId = o2, Date = today.AddDays(2).AddHours(10), Done = false },
            };
            foreach (var o in opps.Take(3))
            {
                acts.Add(new() { Kind = ActivityKind.Email, Subject = "Gửi hồ sơ năng lực và bảng giá", OpportunityId = o.Id, PartnerId = o.PartnerId, OwnerId = o.OwnerId, Date = today.AddDays(-9).AddHours(10) });
                acts.Add(new() { Kind = ActivityKind.Offer, Subject = "Gửi báo giá chính thức", Outcome = $"Giá trị {o.Amount:N0} đ", OpportunityId = o.Id, PartnerId = o.PartnerId, OwnerId = o.OwnerId, Date = today.AddDays(-6).AddHours(15) });
            }
            if (opps.Count > 0) acts.Add(new() { Kind = ActivityKind.Task, Subject = "Họp chốt điều khoản thanh toán", OpportunityId = opps[0].Id, PartnerId = opps[0].PartnerId, OwnerId = opps[0].OwnerId, Date = today.AddDays(3).AddHours(9), Done = false });
            db.CrmActivities.AddRange(acts);
            await db.SaveChangesAsync();
            log.LogInformation("Đã nạp dữ liệu mẫu CRM.");
        }
        catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Dữ liệu mẫu CRM chưa nạp được: {Msg}", ex.Message); }
    }
}
