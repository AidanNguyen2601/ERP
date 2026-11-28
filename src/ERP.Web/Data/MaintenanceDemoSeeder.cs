using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu bảo dưỡng thiết bị: người dùng bảo trì, loại bảo dưỡng, hồ sơ thiết bị (liên kết tài sản cố định / chuyền), kế hoạch định kỳ và TPM,
/// lịch sử sửa chữa 6 tháng (dừng máy, chi phí), phiếu đang chờ duyệt / đang sửa có xuất phụ tùng thật. Mỗi bước chạy một lần.
/// </summary>
public static class MaintenanceDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedProcessDemo", true)) return;
        async Task Run(string name, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Dữ liệu mẫu bảo dưỡng \"{Name}\" chưa nạp được: {Msg}", name, ex.Message); }
        }
        await Run("Người dùng & quy tắc duyệt", () => UserAndRules(sp, db));
        await Run("Loại & thiết bị", () => Equipments(db));
        await Run("Kế hoạch", () => Plans(db));
        await Run("Lịch sử", () => History(db));
        await Run("Phiếu đang xử lý", () => Live(sp, db));
        log.LogInformation("Đã nạp dữ liệu mẫu bảo dưỡng thiết bị.");
    }

    private static async Task UserAndRules(IServiceProvider sp, ErpDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.Role == AppRoles.Maintenance))
        {
            var hasher = sp.GetRequiredService<IPasswordHasher<AppUser>>();
            var u = new AppUser { Username = "baotri", FullName = "Phan Văn Khánh", Role = AppRoles.Maintenance, Department = "Bảo trì thiết bị", Email = "baotri@noithatdemo.vn" };
            u.PasswordHash = hasher.HashPassword(u, "Abc@123");
            db.Users.Add(u);
            if (!await db.CatalogItems.AnyAsync(c => c.Type == CatalogType.Department && c.Name == u.Department))
                db.CatalogItems.Add(new CatalogItem { Type = CatalogType.Department, Code = "PBBT", Name = u.Department, SortOrder = 910 });
            await db.SaveChangesAsync();
        }
        if (!await db.ApprovalRules.AnyAsync(r => r.DocType == ApprovalDocType.MaintenanceOrder))
            db.ApprovalRules.Add(new ApprovalRule { DocType = ApprovalDocType.MaintenanceOrder, Name = "Ban Giám đốc duyệt sửa chữa lớn", Level = 1, MinAmount = 10_000_000, ApproverRole = AppRoles.Director });
        if (!await db.ApprovalRules.AnyAsync(r => r.DocType == ApprovalDocType.MaintenancePlan))
            db.ApprovalRules.Add(new ApprovalRule { DocType = ApprovalDocType.MaintenancePlan, Name = "Ban Giám đốc duyệt kế hoạch bảo dưỡng lớn", Level = 1, MinAmount = 30_000_000, ApproverRole = AppRoles.Director });
        await db.SaveChangesAsync();
    }

    private static async Task Equipments(ErpDbContext db)
    {
        if (await db.MaintenanceTypes.AnyAsync()) return;
        db.MaintenanceTypes.AddRange(
            new MaintenanceType { Code = "BD1", Name = "Bảo dưỡng cấp 1 (hằng tháng)", Kind = MaintenanceKind.Preventive, DefaultIntervalDays = 30, Note = "Vệ sinh, bôi trơn, kiểm tra siết chặt" },
            new MaintenanceType { Code = "BD2", Name = "Bảo dưỡng cấp 2 (6 tháng)", Kind = MaintenanceKind.Preventive, DefaultIntervalDays = 180, Note = "Thay thế chi tiết hao mòn, kiểm tra độ chính xác" },
            new MaintenanceType { Code = "DAU", Name = "Thay dầu bôi trơn", Kind = MaintenanceKind.Preventive, DefaultIntervalDays = 90 },
            new MaintenanceType { Code = "TPM", Name = "TPM – vận hành viên kiểm tra hằng ngày", Kind = MaintenanceKind.Autonomous, DefaultIntervalDays = 1 },
            new MaintenanceType { Code = "HC", Name = "Kiểm định / hiệu chuẩn định kỳ", Kind = MaintenanceKind.Calibration, DefaultIntervalDays = 365 });
        var wc = await db.WorkCenters.ToDictionaryAsync(w => w.Code, w => w.Id);
        var assets = await db.FixedAssets.ToDictionaryAsync(a => a.Code, a => a.Id);
        int? A(string c) => assets.TryGetValue(c, out var id) ? id : null;
        int? W(string c) => wc.TryGetValue(c, out var id) ? id : null;
        var today = DateTime.Today;
        db.Equipments.AddRange(
            new Equipment { Code = "TB-CNC01", Name = "Máy CNC cắt ván 2 đầu", Model = "CNC-2H", Manufacturer = "Homag", SerialNo = "CNC-2H-2024", FixedAssetId = A("TS002"), WorkCenterId = W("CAT"), Department = "Xưởng sản xuất", Location = "Xưởng 1 – khu cắt", Criticality = Criticality.A, InstallDate = today.AddMonths(-29), WarrantyUntil = today.AddMonths(-5) },
            new Equipment { Code = "TB-CN01", Name = "Máy chà nhám thùng", Model = "CN-1300", Manufacturer = "Sangdo", SerialNo = "CN-1300", FixedAssetId = A("TS003"), WorkCenterId = W("LR"), Department = "Xưởng sản xuất", Location = "Xưởng 1 – khu hoàn thiện", Criticality = Criticality.B, InstallDate = today.AddMonths(-19), WarrantyUntil = today.AddDays(40) },
            new Equipment { Code = "TB-XT01", Name = "Xe tải 2,5 tấn", Model = "HD72", Manufacturer = "Hyundai", SerialNo = "51D-123.45", FixedAssetId = A("TS004"), Department = "Kho vận", Location = "Bãi xe", Criticality = Criticality.B, InstallDate = today.AddMonths(-38) },
            new Equipment { Code = "TB-NK01", Name = "Máy nén khí trục vít 15kW", Model = "GA15", Manufacturer = "Atlas Copco", SerialNo = "AC-15-8891", WorkCenterId = W("SON"), Department = "Xưởng sản xuất", Location = "Phòng máy nén khí", Criticality = Criticality.A, InstallDate = today.AddMonths(-14), WarrantyUntil = today.AddMonths(10) },
            new Equipment { Code = "TB-SON01", Name = "Buồng sơn PU & quạt hút", Model = "BS-2000", Manufacturer = "Nội địa", Department = "Xưởng sản xuất", Location = "Xưởng 2 – khu sơn", Criticality = Criticality.B, InstallDate = today.AddMonths(-20), WorkCenterId = W("SON") });
        await db.SaveChangesAsync();
        var eq = await db.Equipments.ToDictionaryAsync(e => e.Code);
        db.EquipmentDocuments.AddRange(
            new EquipmentDocument { EquipmentId = eq["TB-CNC01"].Id, Title = "Hướng dẫn vận hành & bảo dưỡng", DocType = "Hướng dẫn", Number = "HD-CNC-01" },
            new EquipmentDocument { EquipmentId = eq["TB-XT01"].Id, Title = "Giấy kiểm định an toàn kỹ thuật", DocType = "Kiểm định", Number = "KD-2026-0415", IssuedDate = today.AddDays(-335), ExpiryDate = today.AddDays(30) },
            new EquipmentDocument { EquipmentId = eq["TB-NK01"].Id, Title = "Kiểm định bình chịu áp lực", DocType = "Kiểm định", Number = "KDA-0098", IssuedDate = today.AddMonths(-10), ExpiryDate = today.AddMonths(26) },
            new EquipmentDocument { EquipmentId = eq["TB-CN01"].Id, Title = "Phiếu bảo hành", DocType = "Bảo hành", Number = "BH-CN-2025", ExpiryDate = today.AddDays(40) });
        await db.SaveChangesAsync();
    }

    private static async Task Plans(ErpDbContext db)
    {
        if (await db.MaintenancePlans.AnyAsync()) return;
        var eq = await db.Equipments.ToDictionaryAsync(e => e.Code);
        var ty = await db.MaintenanceTypes.ToDictionaryAsync(t => t.Code);
        if (eq.Count == 0 || ty.Count == 0) return;
        var today = DateTime.Today; var n = 0;
        MaintenancePlan P(string e, string t, string title, int days, int nextIn, decimal hours, decimal cost, string who, string checklist, MaintenancePlanStatus st = MaintenancePlanStatus.Active, int lead = 3) => new()
        {
            Code = $"KHBD{today:yyMM}-{++n:0000}", EquipmentId = eq[e].Id, MaintenanceTypeId = ty[t].Id, Kind = ty[t].Kind, Title = title, IntervalDays = days, NextDueDate = today.AddDays(nextIn),
            LastDoneDate = today.AddDays(nextIn - days), EstimatedHours = hours, EstimatedCost = cost, LeadDays = lead, Responsible = who, Checklist = checklist, Status = st, ApprovedAt = st == MaintenancePlanStatus.Active ? today.AddMonths(-3) : null, BranchId = eq[e].BranchId
        };
        db.MaintenancePlans.AddRange(
            P("TB-CNC01", "BD1", "Bảo dưỡng cấp 1 máy CNC", 30, 2, 3, 1_500_000, "Tổ bảo trì", "Vệ sinh bụi gỗ hệ thống hút\nBôi trơn ray dẫn hướng\nKiểm tra siết bu lông trục chính\nKiểm tra dao cắt và thay nếu mòn"),
            P("TB-CNC01", "TPM", "TPM: kiểm tra hằng ngày máy CNC", 1, 0, 0.25m, 0, "Vận hành viên CNC", "Kiểm tra mức dầu bôi trơn\nKiểm tra áp suất khí nén\nLàm sạch bàn máy cuối ca", MaintenancePlanStatus.Active, 0),
            P("TB-CN01", "BD1", "Bảo dưỡng cấp 1 máy chà nhám", 30, -5, 2, 900_000, "Tổ bảo trì", "Thay băng nhám\nKiểm tra dây curoa\nBôi trơn con lăn"),
            P("TB-NK01", "DAU", "Thay dầu máy nén khí", 90, 10, 2, 6_500_000, "Tổ bảo trì", "Thay dầu và lọc dầu\nThay lọc gió\nKiểm tra van an toàn"),
            P("TB-XT01", "HC", "Kiểm định an toàn kỹ thuật xe tải", 365, 30, 4, 3_000_000, "Văn phòng", "Đăng kiểm tại trung tâm\nNộp phí và nhận giấy chứng nhận"),
            P("TB-SON01", "BD2", "Bảo dưỡng cấp 2 buồng sơn", 180, 25, 6, 38_000_000, "Tổ bảo trì", "Thay toàn bộ lọc bụi\nKiểm tra quạt hút và động cơ\nKiểm tra hệ thống điện", MaintenancePlanStatus.Draft));
        await db.SaveChangesAsync();
    }

    private static async Task History(ErpDbContext db)
    {
        if (await db.MaintenanceOrders.AnyAsync()) return;
        var eq = await db.Equipments.ToDictionaryAsync(e => e.Code);
        var plans = await db.MaintenancePlans.ToListAsync();
        if (eq.Count == 0) return;
        var today = DateTime.Today; var n = 0;
        MaintenanceOrder O(string e, MaintenanceKind k, string title, int daysAgo, decimal downtime, decimal parts, decimal labor, decimal outside, string? cause = null, string? res = null, MaintenancePlan? plan = null)
        {
            var d = today.AddDays(-daysAgo);
            return new MaintenanceOrder
            {
                Number = $"{MaintenanceService.Prefix(k)}{d:yyMM}-{9000 + ++n:0000}", Kind = k, EquipmentId = eq[e].Id, PlanId = plan?.Id, Title = title, Status = MaintenanceStatus.Accepted, DueDate = d, ReportedAt = d.AddHours(8),
                StartedAt = d.AddHours(9), CompletedAt = d.AddHours(9 + (double)Math.Max(downtime, 1)), DowntimeHours = downtime, LaborHours = labor / 60000, LaborCost = labor, PartsCost = parts, OutsideCost = outside, EstimatedCost = parts + labor + outside,
                Cause = cause, Resolution = res, RequestedBy = "Quản đốc xưởng", Technician = "Phan Văn Khánh", AcceptedBy = "Quản đốc xưởng", AcceptedAt = d.AddDays(1), AcceptanceNote = "Chạy thử đạt", BranchId = eq[e].BranchId
            };
        }
        var cncPlan = plans.FirstOrDefault(p => p.EquipmentId == eq["TB-CNC01"].Id && p.Kind == MaintenanceKind.Preventive);
        db.MaintenanceOrders.AddRange(
            O("TB-CNC01", MaintenanceKind.Breakdown, "Máy CNC dừng, báo lỗi trục chính", 150, 9.5m, 2_400_000, 540_000, 0, "Dây curoa trục chính đứt do mòn", "Thay dây curoa, căn chỉnh lại độ căng"),
            O("TB-CNC01", MaintenanceKind.Preventive, "Bảo dưỡng cấp 1 máy CNC", 118, 3, 350_000, 180_000, 0, null, "Hoàn thành theo danh mục", cncPlan),
            O("TB-CNC01", MaintenanceKind.Preventive, "Bảo dưỡng cấp 1 máy CNC", 88, 3, 420_000, 180_000, 0, null, "Hoàn thành theo danh mục", cncPlan),
            O("TB-CNC01", MaintenanceKind.Breakdown, "CNC cắt lệch kích thước", 62, 4m, 1_150_000, 300_000, 0, "Dao cắt mòn, sai số kích thước", "Thay bộ dao, hiệu chuẩn lại"),
            O("TB-CNC01", MaintenanceKind.Preventive, "Bảo dưỡng cấp 1 máy CNC", 33, 3, 380_000, 180_000, 0, null, "Hoàn thành theo danh mục", cncPlan),
            O("TB-CN01", MaintenanceKind.Breakdown, "Máy chà nhám không chạy", 100, 14m, 0, 420_000, 3_200_000, "Hỏng biến tần", "Thuê đơn vị ngoài thay biến tần"),
            O("TB-CN01", MaintenanceKind.Breakdown, "Băng nhám bị rách giữa ca", 41, 1.5m, 260_000, 60_000, 0, "Băng nhám quá hạn sử dụng", "Thay băng nhám mới"),
            O("TB-NK01", MaintenanceKind.Breakdown, "Máy nén khí báo quá nhiệt", 75, 6m, 1_800_000, 360_000, 0, "Két làm mát tắc bụi", "Vệ sinh két, thay lọc"),
            O("TB-XT01", MaintenanceKind.Breakdown, "Xe tải hỏng hệ thống phanh", 120, 24m, 0, 300_000, 4_500_000, "Má phanh mòn", "Sửa tại gara ngoài"));
        await db.SaveChangesAsync();
    }

    private static async Task Live(IServiceProvider sp, ErpDbContext db)
    {
        if (await db.MaintenanceOrders.AnyAsync(o => o.Status == MaintenanceStatus.InProgress || o.Status == MaintenanceStatus.PendingApproval)) return;
        var svc = sp.GetRequiredService<MaintenanceService>();
        var eq = await db.Equipments.ToDictionaryAsync(e => e.Code);
        if (eq.Count == 0) return;
        var wh = await db.Warehouses.FirstOrDefaultAsync(w => w.Code == "KNVL") ?? await db.Warehouses.FirstAsync(w => w.IsActive);
        // Phiếu bảo dưỡng định kỳ sinh từ kế hoạch đến hạn
        await svc.GenerateDueAsync(DateTime.Today);
        // Sửa chữa lớn: chờ duyệt theo quy tắc (dự kiến ≥ 10 triệu)
        var big = await svc.CreateAsync(new MaintenanceOrder { Kind = MaintenanceKind.Breakdown, EquipmentId = eq["TB-SON01"].Id, Title = "Quạt hút buồng sơn rung mạnh, kêu to", Symptom = "Quạt rung, tiếng ồn bất thường, lưu lượng hút giảm", Priority = MaintenancePriority.Urgent, DueDate = DateTime.Today.AddDays(2), EstimatedCost = 15_000_000, WarehouseId = wh.Id });
        await svc.SubmitAsync(big);
        // Sửa chữa đang thực hiện có xuất phụ tùng
        var cur = await svc.CreateAsync(new MaintenanceOrder { Kind = MaintenanceKind.Breakdown, EquipmentId = eq["TB-NK01"].Id, Title = "Máy nén khí rò khí ở khớp nối", Symptom = "Áp suất tụt nhanh, nghe tiếng xì khí", Priority = MaintenancePriority.Urgent, DueDate = DateTime.Today, EstimatedCost = 1_200_000, WarehouseId = wh.Id, ReportedAt = DateTime.Now.AddHours(-3) });
        await svc.SubmitAsync(cur);
        var full = await db.MaintenanceOrders.Include(o => o.Parts).Include(o => o.Tasks).FirstAsync(o => o.Id == cur.Id);
        await svc.StartAsync(full);
        var bolt = await db.Items.FirstOrDefaultAsync(i => i.Code == "NVL-OCVIT");
        if (bolt != null)
        {
            var part = await svc.AddPartAsync(full, bolt.Id, 3);
            await svc.IssuePartAsync(full, part.Id, 3, DateTime.Today);
            await svc.ReturnPartAsync(full, part.Id, 1, DateTime.Today);
        }
        db.MaintenanceTasks.AddRange(new MaintenanceTask { MaintenanceOrderId = full.Id, Seq = 1, Description = "Xả áp, ngắt nguồn máy nén khí", Done = true, Result = "Đã khóa nguồn theo quy trình LOTO" },
            new MaintenanceTask { MaintenanceOrderId = full.Id, Seq = 2, Description = "Thay gioăng và siết lại khớp nối" }, new MaintenanceTask { MaintenanceOrderId = full.Id, Seq = 3, Description = "Chạy thử, kiểm tra rò khí bằng nước xà phòng" });
        await db.SaveChangesAsync();
    }
}
