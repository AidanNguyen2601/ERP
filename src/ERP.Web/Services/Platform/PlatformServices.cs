using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Cài đặt hệ thống dạng khóa – giá trị (bảng SystemSettings)</summary>
public class SettingsService
{
    private readonly ErpDbContext _db;
    private Dictionary<string, string>? _cache;
    public SettingsService(ErpDbContext db) => _db = db;

    private async Task<Dictionary<string, string>> AllAsync()
    {
        if (_cache != null) return _cache;
        try { _cache = await _db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value); }
        catch { _cache = new(); }
        return _cache;
    }
    public async Task<string> GetAsync(string key, string def = "") => (await AllAsync()).TryGetValue(key, out var v) ? v : def;
    public async Task<bool> BoolAsync(string key, bool def = false) => bool.TryParse(await GetAsync(key, def.ToString()), out var b) ? b : def;
    public async Task<decimal> DecAsync(string key, decimal def = 0) =>
        decimal.TryParse(await GetAsync(key, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : def;

    public async Task SetAsync(string key, string value)
    {
        var s = await _db.SystemSettings.FindAsync(key);
        if (s == null) _db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        else { s.Value = value; s.UpdatedAt = DateTime.Now; }
        await _db.SaveChangesAsync();
        _cache = null;
    }
}

/// <summary>Phạm vi chi nhánh của người dùng hiện tại (đặt cho DbContext ở đầu mỗi request)</summary>
public static class BranchScope
{
    public const string AllowedClaim = "erp:branches";   // "*" hoặc "1,2"
    public const string HomeClaim = "erp:branch";
    public const string ViewCookie = "erp.branch";

    public static IEnumerable<Claim> ClaimsFor(AppUser u)
    {
        yield return new Claim(AllowedClaim, u.AllBranches || u.BranchId == null ? "*" : u.BranchId.Value.ToString());
        if (u.BranchId.HasValue) yield return new Claim(HomeClaim, u.BranchId.Value.ToString());
    }

    /// <summary>null = mọi chi nhánh; ngược lại danh sách chi nhánh được phép</summary>
    public static int[]? Allowed(ClaimsPrincipal user)
    {
        var v = user.FindFirstValue(AllowedClaim);
        if (string.IsNullOrEmpty(v) || v == "*") return null;
        return v.Split(',').Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).ToArray();
    }

    public static int? Home(ClaimsPrincipal user) => int.TryParse(user.FindFirstValue(HomeClaim), out var i) ? i : null;

    /// <summary>Chi nhánh đang chọn xem (cookie), đã kiểm tra quyền; null = tất cả được phép</summary>
    public static int? Selected(HttpContext ctx)
    {
        if (!int.TryParse(ctx.Request.Cookies[ViewCookie], out var b)) return null;
        var allowed = Allowed(ctx.User);
        return allowed == null || allowed.Contains(b) ? b : null;
    }

    public static void Apply(HttpContext ctx, ErpDbContext db)
    {
        var allowed = Allowed(ctx.User);
        var selected = Selected(ctx);
        if (selected.HasValue) { db.BranchFilterOn = true; db.ScopeBranchIds = new[] { selected.Value }; }
        else if (allowed != null) { db.BranchFilterOn = true; db.ScopeBranchIds = allowed; }
        db.DefaultBranchId = selected ?? Home(ctx.User) ?? allowed?.FirstOrDefault();
        db.AuditUserId = ctx.User.GetUserId();
        db.AuditUserName = ctx.User.GetFullName();
        db.AuditIp = ctx.Connection.RemoteIpAddress?.ToString();
    }
}

/// <summary>Thông báo phát sinh trong service (VD cảnh báo vượt ngân sách) – bộ lọc toàn cục chuyển sang TempData["Warning"]</summary>
public class UserNotices
{
    public List<string> Warnings { get; } = new();
    public void Warn(string msg) { if (!Warnings.Contains(msg)) Warnings.Add(msg); }
}

public class NoticeFilter : Microsoft.AspNetCore.Mvc.Filters.IAsyncResultFilter
{
    private readonly UserNotices _n;
    private readonly Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory _td;
    public NoticeFilter(UserNotices n, Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory td) { _n = n; _td = td; }
    public async Task OnResultExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.ResultExecutionDelegate next)
    {
        if (_n.Warnings.Count > 0)
        {
            var td = _td.GetTempData(context.HttpContext);
            var prev = td.Peek("Warning") as string;
            td["Warning"] = string.Join(" ", new[] { prev }.Concat(_n.Warnings).Where(s => !string.IsNullOrEmpty(s)));
        }
        await next();
    }
}

/// <summary>Sinh mã theo quy tắc đặt mã (tiền tố + số tăng dần)</summary>
public class CodeService
{
    private readonly ErpDbContext _db;
    public CodeService(ErpDbContext db) => _db = db;

    public static string Target(Partner p) => p.IsCustomer ? "customer" : "vendor";
    public static string Target(ItemType t) => "item." + t;

    public async Task<CodeRule?> RuleAsync(string target) => await _db.CodeRules.AsNoTracking().FirstOrDefaultAsync(r => r.Target == target);

    public async Task<string?> NextAsync(string target)
    {
        var r = await RuleAsync(target);
        if (r == null) return null;
        IQueryable<string> codes = target switch
        {
            "customer" or "vendor" => _db.Partners.Select(p => p.Code),
            "employee" => _db.Employees.IgnoreQueryFilters().Select(e => e.Code),
            "tool" => _db.ToolItems.IgnoreQueryFilters().Select(e => e.Code),
            var s when s.StartsWith("contract") => _db.Contracts.IgnoreQueryFilters().Select(e => e.Number),
            _ => _db.Items.Select(i => i.Code)
        };
        var list = await codes.Where(c => c.StartsWith(r.Prefix)).ToListAsync();
        var max = list.Select(c => int.TryParse(c[r.Prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return r.Prefix + (max + 1).ToString(new string('0', r.Digits));
    }

    /// <summary>Kiểm tra mã nhập tay theo quy tắc bắt buộc (nếu có)</summary>
    public async Task<string?> ValidateAsync(string target, string code)
    {
        var r = await RuleAsync(target);
        if (r == null || !r.Enforce) return null;
        return code.StartsWith(r.Prefix) && code.Length == r.Prefix.Length + r.Digits && code[r.Prefix.Length..].All(char.IsDigit)
            ? null : $"Mã phải theo quy tắc {r.Prefix}{new string('9', r.Digits)}";
    }
}

/// <summary>Kiểm tra trùng dữ liệu chủ: mã số thuế, tên đối tượng</summary>
public class DuplicateService
{
    private readonly ErpDbContext _db;
    public DuplicateService(ErpDbContext db) => _db = db;
    /// <summary>Người được phép xác nhận lưu dù trùng (cấp duyệt)</summary>
    public static bool CanOverride(ClaimsPrincipal u) => u.InAny(AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director);

    public async Task<List<string>> PartnerAsync(Partner p)
    {
        var w = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.TaxCode))
        {
            var same = await _db.Partners.Where(x => x.TaxCode == p.TaxCode.Trim() && x.Id != p.Id).Select(x => x.Code + " – " + x.Name).ToListAsync();
            if (same.Any()) w.Add($"Mã số thuế {p.TaxCode} đã có ở: {string.Join("; ", same)}.");
        }
        var name = (p.Name ?? "").Trim();
        if (name.Length > 0)
        {
            var sameName = await _db.Partners.Where(x => x.Name == name && x.Id != p.Id).Select(x => x.Code).ToListAsync();
            if (sameName.Any()) w.Add($"Tên \"{name}\" trùng với đối tượng {string.Join(", ", sameName)}.");
        }
        return w;
    }

    public async Task<List<string>> EmployeeAsync(Employee e)
    {
        var w = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.PersonalTaxCode) && await _db.Employees.IgnoreQueryFilters().AnyAsync(x => x.PersonalTaxCode == e.PersonalTaxCode && x.Id != e.Id))
            w.Add($"Mã số thuế cá nhân {e.PersonalTaxCode} đã có ở nhân viên khác.");
        if (!string.IsNullOrWhiteSpace(e.IdNumber) && await _db.Employees.IgnoreQueryFilters().AnyAsync(x => x.IdNumber == e.IdNumber && x.Id != e.Id))
            w.Add($"Số CCCD {e.IdNumber} đã có ở nhân viên khác.");
        return w;
    }
}
