using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;

namespace ERP.Web.Services;

/// <summary>Lỗi nghiệp vụ: hiển thị thông báo cho người dùng, không phải lỗi hệ thống</summary>
public class BusinessException : Exception
{
    public BusinessException(string message) : base(message) { }
}

/// <summary>Chứng từ đã được gửi duyệt (không phải lỗi): thông báo dạng cảnh báo, dữ liệu vẫn được lưu</summary>
public class PendingApprovalException : BusinessException
{
    public PendingApprovalException(string message) : base(message) { }
}

/// <summary>Cấu hình nghiệp vụ (appsettings.json → "Erp")</summary>
public class ErpSettings
{
    public string CompanyName { get; set; } = "Công ty Cổ phần Demo ERP";
    public string CompanyTaxCode { get; set; } = "0312345678";
    public string CompanyAddress { get; set; } = "TP. Hồ Chí Minh";
    /// <summary>Ký hiệu hóa đơn điện tử bán ra</summary>
    public string InvoiceSeries { get; set; } = "1C26TAA";
    /// <summary>Đơn mua có tổng tiền từ ngưỡng này phải được Ban Giám đốc duyệt</summary>
    public decimal PoApprovalThreshold { get; set; } = 100_000_000;
}

/// <summary>Sinh số chứng từ dạng PREFIX + yyMM + "-" + 4 chữ số, tăng dần theo tháng</summary>
public static class DocNumbers
{
    public static async Task<string> NextAsync(IQueryable<string> existing, string prefix, DateTime date)
    {
        var head = $"{prefix}{date:yyMM}-";
        var numbers = await existing.IgnoreQueryFilters().Where(n => n.StartsWith(head)).ToListAsync();   // số chứng từ duy nhất trên mọi chi nhánh
        var max = numbers.Select(n => int.TryParse(n[head.Length..], out var k) ? k : 0).DefaultIfEmpty(0).Max();
        return $"{head}{max + 1:0000}";
    }
}

/// <summary>Người dùng hiện tại (dùng trong service)</summary>
public class CurrentUser
{
    private readonly IHttpContextAccessor _http;
    public CurrentUser(IHttpContextAccessor http) => _http = http;
    public int? Id => _http.HttpContext?.User.Identity?.IsAuthenticated == true ? _http.HttpContext.User.GetUserId() : null;
    public string Name => _http.HttpContext?.User.GetFullName() ?? "Hệ thống";
    public System.Security.Claims.ClaimsPrincipal? Principal => _http.HttpContext?.User.Identity?.IsAuthenticated == true ? _http.HttpContext.User : null;
}
