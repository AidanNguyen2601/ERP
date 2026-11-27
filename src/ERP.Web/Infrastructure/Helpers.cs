using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using ERP.Web.Models.Entities;

namespace ERP.Web.Infrastructure;

public static class FormatExtensions
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    public static string ToMoney(this decimal v) => v.ToString("N0", Vi);
    public static string ToQty(this decimal v) => v.ToString("#,##0.###", Vi);
    public static string ToPct(this decimal v) => v.ToString("0.#", Vi) + "%";
    public static string ToShortMoney(this decimal v) =>
        Math.Abs(v) >= 1_000_000_000 ? (v / 1_000_000_000m).ToString("0.##", Vi) + " tỷ"
        : Math.Abs(v) >= 1_000_000 ? (v / 1_000_000m).ToString("0.#", Vi) + " tr"
        : v.ToString("N0", Vi);
    public static string ToDate(this DateTime v) => v.ToString("dd/MM/yyyy");
    public static string ToDate(this DateTime? v) => v?.ToString("dd/MM/yyyy") ?? "";
    public static string ToShortDate(this DateTime v) => v.ToString("dd/MM");
    public static string ToDateTime(this DateTime v) => v.ToString("dd/MM/yyyy HH:mm");
    /// <summary>Số không định dạng (dùng cho input type=number)</summary>
    public static string Raw(this decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Iso(this DateTime v) => v.ToString("yyyy-MM-dd");
    public static string GetDisplayName(this Enum value)
    {
        var m = value.GetType().GetMember(value.ToString()).FirstOrDefault();
        return m?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? value.ToString();
    }
}

public static class ClaimsExtensions
{
    public const string FullNameClaim = "FullName";
    public const string DepartmentClaim = "Department";
    public static int GetUserId(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    public static string GetFullName(this ClaimsPrincipal u) => u.FindFirstValue(FullNameClaim) ?? u.Identity?.Name ?? "";
    public static string GetDepartment(this ClaimsPrincipal u) => u.FindFirstValue(DepartmentClaim) ?? "";
    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole(AppRoles.Admin);
    public static bool InAny(this ClaimsPrincipal u, string roles) => roles.Split(',').Any(u.IsInRole);
    public static bool CanAccounting(this ClaimsPrincipal u) => u.InAny(AppRoles.Accounting);
    public static bool SeesFinance(this ClaimsPrincipal u) => u.InAny(AppRoles.FinanceView);
}

public class PagedList<T>
{
    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public PagedList(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    { Items = items; TotalCount = totalCount; Page = page; PageSize = pageSize; }
}

/// <summary>
/// Bắt BusinessException ở mọi action: báo lỗi nghiệp vụ (TempData["Error"]) và quay lại trang trước,
/// thay vì hiện trang lỗi hệ thống.
/// </summary>
public class BusinessExceptionFilter : Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter
{
    private readonly Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory _tempData;
    public BusinessExceptionFilter(Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory tempData) => _tempData = tempData;

    public void OnException(Microsoft.AspNetCore.Mvc.Filters.ExceptionContext context)
    {
        if (context.Exception is not ERP.Web.Services.BusinessException ex) return;
        var http = context.HttpContext;
        _tempData.GetTempData(http)[ex is ERP.Web.Services.PendingApprovalException ? "Warning" : "Error"] = ex.Message;
        var referer = http.Request.Headers.Referer.ToString();
        var back = Uri.TryCreate(referer, UriKind.Absolute, out var u) && u.Host == http.Request.Host.Host ? u.PathAndQuery : "/";
        context.Result = new Microsoft.AspNetCore.Mvc.LocalRedirectResult(back);
        context.ExceptionHandled = true;
    }
}

/// <summary>
/// Mỗi request ghi dữ liệu (POST) chạy trong một transaction: nghiệp vụ nhiều bước (phiếu kho + bút toán + cập nhật đơn hàng)
/// hoặc thành công trọn vẹn, hoặc rollback toàn bộ khi có lỗi.
/// </summary>
public class TransactionFilter : Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter
{
    private readonly ERP.Web.Data.ErpDbContext _db;
    public TransactionFilter(ERP.Web.Data.ErpDbContext db) => _db = db;

    public async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)) { await next(); return; }
        await using var tx = await _db.Database.BeginTransactionAsync();
        var executed = await next();
        if (executed.Exception == null || executed.ExceptionHandled) await tx.CommitAsync();
        else await tx.RollbackAsync();
    }
}

/// <summary>Đọc số tiền bằng chữ tiếng Việt (dùng trên phiếu thu / chi, hóa đơn)</summary>
public static class VietnameseNumber
{
    private static readonly string[] Digits = { "không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín" };
    private static readonly string[] Units = { "", " nghìn", " triệu", " tỷ", " nghìn tỷ", " triệu tỷ" };

    private static string Triple(int n, bool full)
    {
        int h = n / 100, t = n / 10 % 10, u = n % 10;
        var parts = new List<string>();
        if (full || h > 0) parts.Add(Digits[h] + " trăm");
        if (t == 0) { if (u > 0 && (full || h > 0)) parts.Add("lẻ"); }
        else if (t == 1) parts.Add("mười");
        else parts.Add(Digits[t] + " mươi");
        if (u > 0)
        {
            if (u == 1 && t > 1) parts.Add("mốt");
            else if (u == 5 && t > 0) parts.Add("lăm");
            else if (u == 4 && t > 1) parts.Add("tư");
            else parts.Add(Digits[u]);
        }
        return string.Join(" ", parts);
    }

    public static string ToWords(decimal amount)
    {
        var n = (long)Math.Round(Math.Abs(amount), 0);
        if (n == 0) return "Không đồng.";
        var groups = new List<int>();
        while (n > 0) { groups.Add((int)(n % 1000)); n /= 1000; }
        var parts = new List<string>();
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] == 0) continue;
            parts.Add(Triple(groups[i], i < groups.Count - 1) + Units[i]);
        }
        var s = string.Join(" ", parts).Trim();
        return (amount < 0 ? "Âm " : "") + char.ToUpper(s[0]) + s[1..] + " đồng.";
    }
}
