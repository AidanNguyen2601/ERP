using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

/// <summary>Phiên bản cơ sở dữ liệu: danh sách migration, trạng thái, áp dụng bước còn thiếu, hoàn tác bước mới nhất</summary>
[Authorize(Roles = AppRoles.Admin)]
public class SchemaController : ErpControllerBase
{
    public SchemaController(ErpDbContext db) : base(db) { }

    public record Row(Migration Def, SchemaMigration? Rec);

    public async Task<IActionResult> Index()
    {
        var recs = await Db.SchemaMigrations.ToDictionaryAsync(r => r.Id);
        var rows = SchemaMigrations.All.OrderByDescending(m => m.Id, StringComparer.Ordinal).Select(m => new Row(m, recs.GetValueOrDefault(m.Id))).ToList();
        ViewBag.Unknown = recs.Values.Where(r => SchemaMigrations.All.All(m => m.Id != r.Id)).ToList();
        ViewBag.Version = rows.FirstOrDefault(r => r.Rec?.IsApplied == true)?.Def.Id ?? "(chưa có)";
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken, NoTransaction]
    public async Task<IActionResult> Apply()
    {
        var done = await SchemaMigrations.ApplyPendingAsync(Db, null, User.GetFullName(), includeRolledBack: true);
        var failed = await Db.SchemaMigrations.AsNoTracking().Where(m => m.Error != null).Select(m => m.Id).ToListAsync();
        if (failed.Count > 0) Error("Migration lỗi: " + string.Join(", ", failed));
        else Success(done.Count == 0 ? "Không có migration nào cần áp dụng." : "Đã áp dụng: " + string.Join(", ", done));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, NoTransaction]
    public async Task<IActionResult> Rollback()
    {
        var id = await SchemaMigrations.RollbackLatestAsync(Db, User.GetFullName());
        Success($"Đã hoàn tác migration {id}. Bấm \"Áp dụng\" để chạy lại.");
        return RedirectToAction(nameof(Index));
    }
}
