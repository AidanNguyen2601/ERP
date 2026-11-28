using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Models.Entities
{
    public enum PermissionMode
    {
        [Display(Name = "Không truy cập")] Deny = 1,
        [Display(Name = "Chỉ xem (không thêm / sửa / xóa / duyệt)")] ReadOnly = 2
    }

    /// <summary>
    /// Ghi đè quyền trên một chức năng (mục menu) cho một vai trò hoặc một người dùng. Quyền gốc vẫn theo vai trò;
    /// ghi đè chỉ thu hẹp thêm. Ghi đè theo người dùng được ưu tiên hơn theo vai trò. Quản trị hệ thống không bị ghi đè.
    /// </summary>
    public class PermissionOverride
    {
        public int Id { get; set; }
        [StringLength(20)] public string? Role { get; set; }
        public int? UserId { get; set; }
        public AppUser? User { get; set; }
        [Required, StringLength(40)] public string MenuKey { get; set; } = string.Empty;
        public PermissionMode Mode { get; set; } = PermissionMode.Deny;
        [StringLength(200)] public string? Note { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}

namespace ERP.Web.Services
{
    public class PermissionService
    {
        private const string CacheKey = "perm-overrides";
        private readonly ErpDbContext _db;
        private readonly IMemoryCache _cache;
        public PermissionService(ErpDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

        public void Invalidate() => _cache.Remove(CacheKey);

        private async Task<List<PermissionOverride>> AllAsync() =>
            (await _cache.GetOrCreateAsync(CacheKey, async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                try { return await _db.PermissionOverrides.AsNoTracking().ToListAsync(); } catch { return new List<PermissionOverride>(); }
            }))!;

        /// <summary>Mức hạn chế hiệu lực của người dùng trên từng chức năng (đã gộp vai trò → người dùng)</summary>
        public async Task<Dictionary<string, PermissionMode>> EffectiveAsync(ClaimsPrincipal u)
        {
            var res = new Dictionary<string, PermissionMode>();
            if (u.Identity?.IsAuthenticated != true || u.IsAdmin()) return res;
            var id = u.GetUserId();
            var all = await AllAsync();
            foreach (var o in all.Where(o => o.UserId == null && o.Role != null && u.IsInRole(o.Role))) res[o.MenuKey] = Max(res.GetValueOrDefault(o.MenuKey), o.Mode);
            foreach (var g in all.Where(o => o.UserId == id).GroupBy(o => o.MenuKey)) res[g.Key] = g.Min(o => o.Mode);   // theo người: ghi đè hẳn theo vai trò
            return res;
        }

        private static PermissionMode Max(PermissionMode a, PermissionMode b) => a == 0 ? b : (PermissionMode)Math.Min((int)a, (int)b);   // Deny (1) mạnh hơn ReadOnly (2)

        public async Task<HashSet<string>> DeniedKeysAsync(ClaimsPrincipal u) =>
            (await EffectiveAsync(u)).Where(x => x.Value == PermissionMode.Deny).Select(x => x.Key).ToHashSet();
    }

    /// <summary>Áp dụng ghi đè quyền cho mọi request: chức năng bị cấm → từ chối; chỉ xem → chặn thao tác ghi (POST/PUT/DELETE)</summary>
    public class PermissionFilter : IAsyncActionFilter
    {
        private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase) { "Account", "Home", "Preferences", "Org" };
        private readonly PermissionService _perm;
        public PermissionFilter(PermissionService perm) => _perm = perm;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;
            var ctl = context.RouteData.Values["controller"]?.ToString() ?? "";
            if (http.User.Identity?.IsAuthenticated != true || http.User.IsAdmin() || Exempt.Contains(ctl) || context.Controller is not Microsoft.AspNetCore.Mvc.Controller) { await next(); return; }
            var eff = await _perm.EffectiveAsync(http.User);
            if (eff.Count == 0) { await next(); return; }
            var ctx = new MenuContext { Controller = ctl, Action = context.RouteData.Values["action"]?.ToString() ?? "", Query = http.Request.Query };
            var keys = Menu.Groups.SelectMany(g => g.Items).Where(i => SafeActive(i, ctx)).Select(i => i.Key).ToList();
            // chức năng không khớp mục menu nào theo Active (trang con) → khớp theo controller của mục menu
            if (keys.Count == 0) keys = Menu.Groups.SelectMany(g => g.Items).Where(i => string.Equals(i.Controller, ctl, StringComparison.OrdinalIgnoreCase)).Select(i => i.Key).ToList();
            var modes = keys.Where(eff.ContainsKey).Select(k => eff[k]).ToList();
            if (modes.Count == 0) { await next(); return; }
            var deny = keys.All(k => eff.GetValueOrDefault(k) == PermissionMode.Deny);   // chỉ chặn hẳn khi mọi mục khớp đều bị cấm
            var write = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method);
            if (deny || (write && modes.Any(m => m is PermissionMode.ReadOnly or PermissionMode.Deny)))
            {
                context.Result = new Microsoft.AspNetCore.Mvc.RedirectToActionResult("AccessDenied", "Account", new { reason = deny ? "deny" : "readonly" });
                return;
            }
            await next();
        }

        private static bool SafeActive(MenuItem i, MenuContext c) { try { return i.Active(c); } catch { return false; } }
    }
}
