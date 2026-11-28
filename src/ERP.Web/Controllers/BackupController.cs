using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Sao lưu cơ sở dữ liệu (chỉ quản trị)</summary>
[Authorize(Roles = AppRoles.Admin)]
public class BackupController : ErpControllerBase
{
    private readonly BackupService _svc; private readonly SettingsService _set;
    public BackupController(ErpDbContext db, BackupService svc, SettingsService set) : base(db) { _svc = svc; _set = set; }

    public async Task<IActionResult> Index()
    {
        ViewBag.Folder = await _svc.FolderAsync();
        ViewBag.AutoHour = (int)await _set.DecAsync("Backup.AutoHour", -1);
        ViewBag.Db = Db.Database.GetDbConnection().Database;
        ViewBag.KeepDays = await _svc.KeepDaysAsync();
        ViewBag.Expired = await _svc.ExpiredAsync();
        ViewBag.PruneError = await _set.GetAsync("Backup.PruneError");
        return View(await Db.BackupRecords.OrderByDescending(b => b.CreatedAt).Take(50).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [NoTransaction]
    public async Task<IActionResult> Run()
    {
        var r = await _svc.RunAsync(User.GetFullName(), false);
        if (r.Success) Success($"Đã sao lưu: {r.FilePath}" + (r.Verified ? " (đã kiểm tra tệp)" : "")); else Error("Sao lưu thất bại: " + r.Error);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, NoTransaction]
    public async Task<IActionResult> Prune()
    {
        var (n, err) = await _svc.PruneAsync();
        if (err == null) Success($"Đã xóa {n} tệp sao lưu quá hạn."); else Error($"Đã xóa {n} tệp; không xóa tiếp được (cần quyền sysadmin trên SQL Server): {err}");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkDeleted()
    {
        Success($"Đã đánh dấu {await _svc.MarkDeletedAsync()} tệp là đã xóa.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(string folder, int autoHour, int keepDays)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains('\'') || folder.Contains(';')) throw new BusinessException("Thư mục không hợp lệ.");
        await _set.SetAsync("Backup.Path", folder.Trim());
        await _set.SetAsync("Backup.AutoHour", Math.Clamp(autoHour, -1, 23).ToString());
        await _set.SetAsync("Backup.KeepDays", Math.Clamp(keepDays, 0, 3650).ToString());
        Success("Đã lưu cấu hình sao lưu.");
        return RedirectToAction(nameof(Index));
    }
}
