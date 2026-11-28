using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Lịch sử sao lưu cơ sở dữ liệu (tệp .bak nằm trên máy chủ SQL Server)</summary>
public class BackupRecord
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(260)] public string FilePath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool Verified { get; set; }
    public bool Automatic { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(500)] public string? Error { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(150)] public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int DurationMs { get; set; }
    /// <summary>Thời điểm tệp đã được xóa khỏi máy chủ SQL theo chính sách giữ tệp</summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Sao lưu cơ sở dữ liệu bằng lệnh BACKUP DATABASE (tệp ghi trên máy chủ SQL Server, thư mục lấy từ cài đặt Backup.Path),
/// kiểm tra lại bằng RESTORE VERIFYONLY và lưu lịch sử. Tài khoản kết nối cần quyền sao lưu (db_owner / db_backupoperator).
/// </summary>
public class BackupService
{
    private readonly ErpDbContext _db;
    private readonly SettingsService _set;
    public BackupService(ErpDbContext db, SettingsService set) { _db = db; _set = set; }

    public async Task<string> FolderAsync() => (await _set.GetAsync("Backup.Path", "/var/opt/mssql/data")).Trim();

    public async Task<BackupRecord> RunAsync(string? by, bool automatic)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dbName = _db.Database.GetDbConnection().Database;
        var folder = (await FolderAsync()).TrimEnd('/', '\\');
        var sep = folder.Contains('\\') ? "\\" : "/";
        var file = $"{folder}{sep}{dbName}_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var rec = new BackupRecord { FilePath = file, CreatedBy = by, Automatic = automatic };
        try
        {
            if (dbName.Contains(']') || file.Contains('\'')) throw new InvalidOperationException("Tên cơ sở dữ liệu hoặc đường dẫn không hợp lệ.");
            await _db.Database.ExecuteSqlRawAsync($"BACKUP DATABASE [{dbName}] TO DISK = N'{file}' WITH INIT, CHECKSUM");
            rec.Success = true;
            try { await _db.Database.ExecuteSqlRawAsync($"RESTORE VERIFYONLY FROM DISK = N'{file}' WITH CHECKSUM"); rec.Verified = true; }
            catch (Exception vx) { rec.Error = "Đã sao lưu nhưng kiểm tra tệp thất bại: " + Trim(vx.Message); }
        }
        catch (Exception ex) { rec.Success = false; rec.Error = Trim(ex.Message); }
        rec.DurationMs = (int)sw.ElapsedMilliseconds;
        _db.BackupRecords.Add(rec);
        await _db.SaveChangesAsync();
        if (rec.Success) await PruneAsync();
        return rec;
    }

    public async Task<int> KeepDaysAsync() => (int)await _set.DecAsync("Backup.KeepDays", 0);

    /// <summary>Các tệp sao lưu quá hạn giữ (chưa xóa). Luôn giữ lại ít nhất bản sao lưu thành công mới nhất.</summary>
    public async Task<List<BackupRecord>> ExpiredAsync()
    {
        var days = await KeepDaysAsync();
        if (days <= 0) return new();
        var cut = DateTime.Now.AddDays(-days);
        var latest = await _db.BackupRecords.Where(b => b.Success).OrderByDescending(b => b.CreatedAt).Select(b => (int?)b.Id).FirstOrDefaultAsync();
        return await _db.BackupRecords.Where(b => b.Success && b.DeletedAt == null && b.CreatedAt < cut && b.Id != latest).OrderBy(b => b.CreatedAt).ToListAsync();
    }

    /// <summary>
    /// Xóa tệp quá hạn bằng master.dbo.xp_delete_file (cần quyền sysadmin trên SQL Server). Không đủ quyền thì giữ nguyên, trả về thông báo;
    /// màn hình Sao lưu hiển thị kịch bản để quản trị SQL chạy thủ công.
    /// </summary>
    public async Task<(int Deleted, string? Error)> PruneAsync()
    {
        var list = await ExpiredAsync();
        var n = 0;
        foreach (var b in list)
        {
            if (b.FilePath.Contains('\'')) continue;
            try { await _db.Database.ExecuteSqlRawAsync($"EXEC master.dbo.xp_delete_file 0, N'{b.FilePath}'"); b.DeletedAt = DateTime.Now; n++; }
            catch (Exception ex) { await _db.SaveChangesAsync(); await _set.SetAsync("Backup.PruneError", Trim(ex.Message)); return (n, ex.Message); }
        }
        await _db.SaveChangesAsync();
        await _set.SetAsync("Backup.PruneError", "");
        return (n, null);
    }

    /// <summary>Kịch bản xóa thủ công cho quản trị SQL (khi tài khoản ứng dụng không đủ quyền)</summary>
    public static string ManualScript(IEnumerable<BackupRecord> files) =>
        string.Join("\n", files.Select(f => $"EXEC master.dbo.xp_delete_file 0, N'{f.FilePath}';"));

    /// <summary>Đánh dấu đã xóa (sau khi quản trị SQL xóa thủ công)</summary>
    public async Task<int> MarkDeletedAsync()
    {
        var list = await ExpiredAsync();
        foreach (var b in list) b.DeletedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return list.Count;
    }

    private static string Trim(string s) => s.Length > 480 ? s[..480] : s;
}
