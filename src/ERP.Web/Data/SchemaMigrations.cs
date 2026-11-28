using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ERP.Web.Data;

/// <summary>Lịch sử áp dụng thay đổi cấu trúc cơ sở dữ liệu (migration có phiên bản)</summary>
public class SchemaMigration
{
    [Key, StringLength(60)] public string Id { get; set; } = string.Empty;
    [StringLength(300)] public string Description { get; set; } = string.Empty;
    public DateTime AppliedAt { get; set; } = DateTime.Now;
    [StringLength(150)] public string? AppliedBy { get; set; }
    public int DurationMs { get; set; }
    public DateTime? RolledBackAt { get; set; }
    [StringLength(150)] public string? RolledBackBy { get; set; }
    [StringLength(500)] public string? Error { get; set; }
    public bool IsApplied => RolledBackAt == null && Error == null;
}

/// <param name="Id">Mã duy nhất, sắp theo thứ tự áp dụng: yyyyMMdd_NN_ten</param>
/// <param name="Up">Các lệnh SQL áp dụng (nên viết idempotent: IF NOT EXISTS ...)</param>
/// <param name="Down">Các lệnh SQL hoàn tác (rỗng = không hoàn tác được)</param>
public record Migration(string Id, string Description, string[] Up, string[] Down);

/// <summary>
/// Migration có phiên bản. Bảng / cột mới khai báo trong model vẫn được tạo tự động khi khởi động (EnsureCreated + bổ sung cột);
/// các thay đổi khác (chỉ mục, dữ liệu, đổi kiểu, ràng buộc...) khai báo ở đây theo thứ tự, mỗi bước chạy đúng một lần trong transaction,
/// được ghi vào bảng SchemaMigrations và có thể hoàn tác bước mới nhất từ màn hình Quản trị → Phiên bản cơ sở dữ liệu.
/// QUY TẮC: không sửa migration đã phát hành – thêm migration mới.
/// </summary>
public static class SchemaMigrations
{
    private static string Idx(string table, string name, string cols) =>
        $"IF OBJECT_ID(N'{table}', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'{table}')) CREATE INDEX [{name}] ON {table} {cols}";
    private static string DropIdx(string table, string name) =>
        $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'{table}')) DROP INDEX [{name}] ON {table}";

    public static readonly Migration[] All =
    {
        new("20261128_01_chi_muc_hieu_nang", "Chỉ mục hiệu năng: sổ cái theo tài khoản, hóa đơn theo hạn thanh toán, yêu cầu duyệt, nhật ký thao tác",
            new[]
            {
                Idx("dbo.JournalLines", "IX_JournalLines_Account_Cover", "(AccountId) INCLUDE (JournalEntryId, Debit, Credit)"),
                Idx("dbo.Invoices", "IX_Invoices_Direction_Status_DueDate", "(Direction, Status, DueDate)"),
                Idx("dbo.ApprovalRequests", "IX_ApprovalRequests_Status_RequestedAt", "(Status, RequestedAt)"),
                Idx("dbo.AuditLogs", "IX_AuditLogs_At", "(At)"),
            },
            new[]
            {
                DropIdx("dbo.JournalLines", "IX_JournalLines_Account_Cover"), DropIdx("dbo.Invoices", "IX_Invoices_Direction_Status_DueDate"),
                DropIdx("dbo.ApprovalRequests", "IX_ApprovalRequests_Status_RequestedAt"), DropIdx("dbo.AuditLogs", "IX_AuditLogs_At"),
            }),
        new("20261128_02_chi_muc_danh_sach", "Chỉ mục cho phân trang danh sách: đơn bán, đơn mua, phiếu thu chi, đối tác, hàng hóa",
            new[]
            {
                Idx("dbo.SalesOrders", "IX_SalesOrders_Date_Id", "(Date DESC, Id DESC)"),
                Idx("dbo.PurchaseOrders", "IX_PurchaseOrders_Date_Id", "(Date DESC, Id DESC)"),
                Idx("dbo.CashVouchers", "IX_CashVouchers_Date", "(Date)"),
                Idx("dbo.Invoices", "IX_Invoices_Direction_Date", "(Direction, Date)"),
            },
            new[]
            {
                DropIdx("dbo.SalesOrders", "IX_SalesOrders_Date_Id"), DropIdx("dbo.PurchaseOrders", "IX_PurchaseOrders_Date_Id"),
                DropIdx("dbo.CashVouchers", "IX_CashVouchers_Date"), DropIdx("dbo.Invoices", "IX_Invoices_Direction_Date"),
            }),
        new("20261129_01_che_do_bhxh", "Chế độ BHXH cho loại nghỉ: nghỉ ốm do BHXH chi trả (doanh nghiệp không trả lương), thêm loại nghỉ thai sản",
            new[]
            {
                "IF OBJECT_ID(N'dbo.LeaveTypes', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.LeaveTypes', N'BenefitKind') IS NOT NULL EXEC(N'UPDATE dbo.LeaveTypes SET BenefitKind = 1, IsPaid = 0 WHERE Code = N''OM'' AND BenefitKind = 0')",
                "IF OBJECT_ID(N'dbo.LeaveTypes', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.LeaveTypes', N'BenefitKind') IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.LeaveTypes) AND NOT EXISTS (SELECT 1 FROM dbo.LeaveTypes WHERE Code = N'TS') EXEC(N'INSERT INTO dbo.LeaveTypes (Code, Name, IsPaid, DeductsAnnualBalance, IsActive, BenefitKind) VALUES (N''TS'', N''Nghỉ thai sản (hưởng BHXH)'', 0, 0, 1, 2)')",
            },
            new[]
            {
                "IF OBJECT_ID(N'dbo.LeaveTypes', N'U') IS NOT NULL EXEC(N'UPDATE dbo.LeaveTypes SET BenefitKind = 0, IsPaid = 1 WHERE Code = N''OM''')",
                "IF OBJECT_ID(N'dbo.LeaveTypes', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.LeaveRequests', N'U') IS NOT NULL EXEC(N'DELETE t FROM dbo.LeaveTypes t WHERE t.Code = N''TS'' AND NOT EXISTS (SELECT 1 FROM dbo.LeaveRequests r WHERE r.LeaveTypeId = t.Id)')",
            }),
    };

    /// <summary>Áp dụng các migration chưa chạy (gọi khi khởi động, sau khi tạo bảng mới)</summary>
    /// <param name="includeRolledBack">false (khi khởi động): bước đã được hoàn tác thủ công giữ nguyên, chỉ chạy lại khi quản trị bấm "Áp dụng"</param>
    public static async Task<List<string>> ApplyPendingAsync(ErpDbContext db, ILogger? log = null, string? by = "Khởi động", bool includeRolledBack = false)
    {
        var done = (await db.SchemaMigrations.AsNoTracking().ToListAsync()).Where(m => m.IsApplied || (!includeRolledBack && m.RolledBackAt != null && m.Error == null)).Select(m => m.Id).ToHashSet();
        var applied = new List<string>();
        foreach (var m in All.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (done.Contains(m.Id)) continue;
            var err = await RunAsync(db, m.Up);
            var rec = await db.SchemaMigrations.FindAsync(m.Id) ?? db.SchemaMigrations.Add(new SchemaMigration { Id = m.Id }).Entity;
            rec.Description = m.Description; rec.AppliedAt = DateTime.Now; rec.AppliedBy = by; rec.RolledBackAt = null; rec.RolledBackBy = null;
            rec.DurationMs = err.Ms; rec.Error = err.Error;
            await db.SaveChangesAsync();
            if (err.Error != null) { log?.LogError("Migration {Id} lỗi: {Err}", m.Id, err.Error); break; }   // dừng để không chạy bước sau trên nền lỗi
            log?.LogInformation("Đã áp dụng migration {Id}", m.Id);
            applied.Add(m.Id);
        }
        return applied;
    }

    /// <summary>Hoàn tác migration mới nhất đã áp dụng</summary>
    public static async Task<string> RollbackLatestAsync(ErpDbContext db, string by)
    {
        var applied = (await db.SchemaMigrations.ToListAsync()).Where(m => m.IsApplied).Select(m => m.Id).ToHashSet();
        var m = All.Where(x => applied.Contains(x.Id)).OrderByDescending(x => x.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new ERP.Web.Services.BusinessException("Không có migration nào để hoàn tác.");
        if (m.Down.Length == 0) throw new ERP.Web.Services.BusinessException($"Migration {m.Id} không có lệnh hoàn tác.");
        var r = await RunAsync(db, m.Down);
        if (r.Error != null) throw new ERP.Web.Services.BusinessException($"Hoàn tác {m.Id} lỗi: {r.Error}");
        var rec = await db.SchemaMigrations.FindAsync(m.Id);
        rec!.RolledBackAt = DateTime.Now; rec.RolledBackBy = by;
        await db.SaveChangesAsync();
        return m.Id;
    }

    private static async Task<(int Ms, string? Error)> RunAsync(ErpDbContext db, string[] sql)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            foreach (var s in sql) await db.Database.ExecuteSqlRawAsync(s);
            await tx.CommitAsync();
            return ((int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return ((int)sw.ElapsedMilliseconds, ex.Message.Length > 480 ? ex.Message[..480] : ex.Message);
        }
    }
}
