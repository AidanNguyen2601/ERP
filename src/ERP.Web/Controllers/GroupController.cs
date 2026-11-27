using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Hợp nhất báo cáo nhiều pháp nhân (loại trừ giao dịch nội bộ) và bảng cân đối theo chuẩn mực / chế độ kế toán khác</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class GroupController : ErpControllerBase
{
    public GroupController(ErpDbContext db) : base(db) { }

    public async Task<IActionResult> Consolidation(DateTime? from, DateTime? to, int[]? companyIds, bool excel, [FromServices] ConsolidationService svc)
    {
        var (f, t) = Range(from ?? new DateTime(DateTime.Today.Year, 1, 1), to);
        var all = await Db.Companies.Where(c => c.IsActive).OrderByDescending(c => c.IsParent).ThenBy(c => c.Code).ToListAsync();
        var ids = companyIds is { Length: > 0 } ? companyIds : all.Select(c => c.Id).ToArray();
        ViewBag.From = f; ViewBag.To = t; ViewBag.All = all; ViewBag.Ids = ids;
        var res = await svc.RunAsync(f, t, ids);
        if (excel)
        {
            var cols = new List<ReportExport.Col> { new("Số hiệu TK", FieldKind.Text), new("Tên tài khoản", FieldKind.Text) };
            cols.AddRange(res.Companies.Select(c => new ReportExport.Col(c.Code, FieldKind.Money)));
            cols.AddRange(new[] { new ReportExport.Col("Cộng gộp", FieldKind.Money), new ReportExport.Col("Loại trừ", FieldKind.Money), new ReportExport.Col("Hợp nhất", FieldKind.Money) });
            var data = res.Rows.Select(r => new object?[] { r.Code, r.Name }.Concat(res.Companies.Select(c => (object?)r.ByCompany.GetValueOrDefault(c.Id))).Concat(new object?[] { r.Total, r.Elimination, r.Consolidated }).ToArray()).ToList();
            return File(ReportExport.Xlsx("Hợp nhất", "BẢNG TỔNG HỢP SỐ DƯ HỢP NHẤT", new[] { $"Từ {f:dd/MM/yyyy} đến {t:dd/MM/yyyy}", "Số dư Nợ dương, Có âm" }, cols, data, null),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Hop_nhat_{t:yyyyMMdd}.xlsx");
        }
        return View(res);
    }

    public async Task<IActionResult> Standards([FromServices] StandardMappingService svc, string standard = "TT133", DateTime? from = null, DateTime? to = null)
    {
        var (f, t) = Range(from ?? new DateTime(DateTime.Today.Year, 1, 1), to);
        ViewBag.From = f; ViewBag.To = t; ViewBag.Standard = standard;
        ViewBag.Standards = await Db.AccountMappings.Select(m => m.Standard).Distinct().ToListAsync();
        ViewBag.Mappings = await Db.AccountMappings.Where(m => m.Standard == standard).OrderBy(m => m.FromPrefix).ToListAsync();
        var (rows, unmapped) = await svc.TrialBalanceAsync(standard, f, t);
        ViewBag.Unmapped = unmapped;
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> SaveMapping(string standard, string fromPrefix, string toCode, string toName, int sign = 1)
    {
        if (string.IsNullOrWhiteSpace(standard) || string.IsNullOrWhiteSpace(fromPrefix) || string.IsNullOrWhiteSpace(toCode)) { Error("Nhập đủ chuẩn mực, TK nguồn, TK đích."); return RedirectToAction(nameof(Standards), new { standard }); }
        standard = standard.Trim().ToUpperInvariant(); fromPrefix = fromPrefix.Trim();
        var m = await Db.AccountMappings.FirstOrDefaultAsync(x => x.Standard == standard && x.FromPrefix == fromPrefix);
        if (m == null) Db.AccountMappings.Add(m = new AccountMapping { Standard = standard, FromPrefix = fromPrefix });
        m.ToCode = toCode.Trim(); m.ToName = string.IsNullOrWhiteSpace(toName) ? toCode.Trim() : toName.Trim(); m.Sign = sign == -1 ? -1 : 1;
        await Db.SaveChangesAsync();
        Success($"Đã lưu ánh xạ {fromPrefix} → {m.ToCode} ({standard}).");
        return RedirectToAction(nameof(Standards), new { standard });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> DeleteMapping(int id)
    {
        var m = await Db.AccountMappings.FindAsync(id);
        if (m != null) { Db.AccountMappings.Remove(m); await Db.SaveChangesAsync(); Success("Đã xóa ánh xạ."); }
        return RedirectToAction(nameof(Standards), new { standard = m?.Standard });
    }
}
