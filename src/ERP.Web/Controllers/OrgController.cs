using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Đa công ty / đa chi nhánh: danh mục pháp nhân, chi nhánh; chọn chi nhánh đang làm việc</summary>
public class OrgController : ErpControllerBase
{
    public OrgController(ErpDbContext db) : base(db) { }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Accountant)]
    public async Task<IActionResult> Index()
    {
        ViewBag.Counts = (await Db.JournalEntries.IgnoreQueryFilters().GroupBy(j => j.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key ?? 0, x => x.N);
        ViewBag.Users = (await Db.Users.GroupBy(u => u.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key ?? 0, x => x.N);
        ViewBag.Warehouses = await Db.Warehouses.OrderBy(w => w.Code).ToListAsync();
        ViewBag.CashAccounts = await Db.CashAccounts.OrderBy(w => w.Code).ToListAsync();
        return View(await Db.Companies.Include(c => c.Branches).OrderByDescending(c => c.IsParent).ThenBy(c => c.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveCompany(Company m)
    {
        var c = m.Id == 0 ? new Company() : await Db.Companies.FindAsync(m.Id);
        if (c == null) return NotFound();
        if (await Db.Companies.AnyAsync(x => x.Code == m.Code && x.Id != m.Id)) { Error("Mã công ty đã tồn tại."); return RedirectToAction(nameof(Index)); }
        c.Code = m.Code.Trim().ToUpperInvariant(); c.Name = m.Name.Trim(); c.TaxCode = m.TaxCode; c.Address = m.Address; c.IsParent = m.IsParent; c.OwnershipPct = m.OwnershipPct; c.IsActive = m.IsActive; c.AcquisitionDate = m.AcquisitionDate; c.RetainedAtAcquisition = m.RetainedAtAcquisition;
        if (c.Id == 0) Db.Companies.Add(c);
        if (c.IsParent) foreach (var o in await Db.Companies.Where(x => x.Id != c.Id && x.IsParent).ToListAsync()) o.IsParent = false;
        await Db.SaveChangesAsync();
        Success($"Đã lưu công ty {c.Code}.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveBranch(Branch m)
    {
        var b = m.Id == 0 ? new Branch() : await Db.Branches.FindAsync(m.Id);
        if (b == null) return NotFound();
        if (await Db.Branches.AnyAsync(x => x.Code == m.Code && x.Id != m.Id)) { Error("Mã chi nhánh đã tồn tại."); return RedirectToAction(nameof(Index)); }
        b.CompanyId = m.CompanyId; b.Code = m.Code.Trim().ToUpperInvariant(); b.Name = m.Name.Trim(); b.Address = m.Address; b.IsActive = m.IsActive;
        if (b.Id == 0) Db.Branches.Add(b);
        await Db.SaveChangesAsync();
        Success($"Đã lưu chi nhánh {b.Code}.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Gán kho / quỹ cho chi nhánh (phiếu kho, phiếu thu chi tự nhận chi nhánh theo kho / quỹ)</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Assign(string kind, int id, int branchId)
    {
        if (kind == "wh") { var w = await Db.Warehouses.FindAsync(id); if (w != null) w.BranchId = branchId; }
        else { var c = await Db.CashAccounts.FindAsync(id); if (c != null) c.BranchId = branchId; }
        await Db.SaveChangesAsync();
        Success("Đã cập nhật chi nhánh.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Chọn chi nhánh đang xem / làm việc (0 = tất cả chi nhánh được phép)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Switch(int branchId, string? returnUrl)
    {
        var allowed = BranchScope.Allowed(User);
        if (branchId == 0) Response.Cookies.Delete(BranchScope.ViewCookie);
        else if (allowed == null || allowed.Contains(branchId))
            Response.Cookies.Append(BranchScope.ViewCookie, branchId.ToString(), new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.Now.AddDays(30) });
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction("Index", "Home");
    }

    /// <summary>Đổi ngôn ngữ giao diện (vi / en) và lưu vào hồ sơ người dùng</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Language(string lang, string? returnUrl)
    {
        lang = lang == "en" ? "en" : "vi";
        Response.Cookies.Append(Lang.Cookie, lang, new CookieOptions { IsEssential = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.Now.AddYears(1) });
        var u = await Db.Users.FindAsync(Me);
        if (u != null && u.Language != lang) { u.Language = lang; await Db.SaveChangesAsync(); }
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction("Index", "Home");
    }

    /// <summary>Ma trận phân quyền: chức năng × vai trò (theo khai báo menu) và phạm vi chi nhánh của từng người dùng</summary>
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> Permissions()
    {
        ViewBag.Users = await Db.Users.Include(u => u.Branch).OrderBy(u => u.Role).ThenBy(u => u.Username).ToListAsync();
        ViewBag.Overrides = await Db.PermissionOverrides.Include(o => o.User).OrderBy(o => o.MenuKey).ThenBy(o => o.Role).ToListAsync();
        return View();
    }

    /// <summary>Thêm ghi đè quyền: target = "role:Sales" hoặc "user:12"</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> AddOverride(string target, string menuKey, PermissionMode mode, string? note, [FromServices] ERP.Web.Services.PermissionService perm)
    {
        if (Menu.Groups.SelectMany(g => g.Items).All(i => i.Key != menuKey)) throw new BusinessException("Chọn chức năng.");
        string? role = null; int? userId = null;
        if (target?.StartsWith("role:") == true && AppRoles.All.Contains(target[5..])) role = target[5..];
        else if (target?.StartsWith("user:") == true && int.TryParse(target[5..], out var uid) && await Db.Users.AnyAsync(u => u.Id == uid)) userId = uid;
        else throw new BusinessException("Chọn vai trò hoặc người dùng.");
        if (role == AppRoles.Admin || (userId != null && await Db.Users.AnyAsync(u => u.Id == userId && u.Role == AppRoles.Admin))) throw new BusinessException("Không ghi đè quyền của Quản trị hệ thống.");
        var o = await Db.PermissionOverrides.FirstOrDefaultAsync(x => x.MenuKey == menuKey && x.Role == role && x.UserId == userId);
        if (o == null) Db.PermissionOverrides.Add(o = new PermissionOverride { MenuKey = menuKey, Role = role, UserId = userId });
        o.Mode = mode; o.Note = note;
        await Db.SaveChangesAsync();
        perm.Invalidate();
        Success("Đã lưu ghi đè quyền.");
        return RedirectToAction(nameof(Permissions));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteOverride(int id, [FromServices] ERP.Web.Services.PermissionService perm)
    {
        var o = await Db.PermissionOverrides.FindAsync(id);
        if (o != null) { Db.PermissionOverrides.Remove(o); await Db.SaveChangesAsync(); perm.Invalidate(); }
        return RedirectToAction(nameof(Permissions));
    }
}

/// <summary>Nhật ký thao tác (audit log)</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class AuditController : ErpControllerBase
{
    public AuditController(ErpDbContext db) : base(db) { }

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string? user, string? type, string? action, string? q, string? entityId, int page = 1)
    {
        var t = (to ?? DateTime.Today).Date.AddDays(1); var f = (from ?? DateTime.Today.AddDays(-30)).Date;
        var query = Db.AuditLogs.Where(a => a.At >= f && a.At < t);
        if (!string.IsNullOrWhiteSpace(user)) query = query.Where(a => a.UserName!.Contains(user));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(a => a.EntityType == type);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(entityId)) query = query.Where(a => a.EntityId == entityId);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(a => a.DocNumber!.Contains(q) || a.Changes!.Contains(q));
        const int size = 100;
        var total = await query.CountAsync();
        ViewBag.Types = await Db.AuditLogs.Select(a => new { a.EntityType, a.EntityName }).Distinct().OrderBy(x => x.EntityName).ToListAsync();
        ViewBag.Actions = await Db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.From = f; ViewBag.To = t.AddDays(-1); ViewBag.User = user; ViewBag.Type = type; ViewBag.Action = action; ViewBag.Q = q; ViewBag.EntityId = entityId;
        ViewBag.Page = page; ViewBag.Pages = (int)Math.Ceiling(total / (double)size); ViewBag.Total = total;
        return View(await query.OrderByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync());
    }
}
