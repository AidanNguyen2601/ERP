using Microsoft.AspNetCore.DataProtection;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- Database (SQL Server) ----------
builder.Services.AddDbContext<ErpDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ErpConnection"), o => o.UseCompatibilityLevel(120)));

// ---------- Authentication: cookie + role ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });

// Mặc định mọi trang đều yêu cầu đăng nhập (trừ [AllowAnonymous]); lỗi nghiệp vụ hiển thị thành thông báo
builder.Services.AddControllersWithViews(o =>
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.Filters.Add(new AuthorizeFilter(policy));
    o.Filters.Add<ERP.Web.Infrastructure.BusinessExceptionFilter>();
    o.Filters.Add<ERP.Web.Infrastructure.TransactionFilter>();
    o.Filters.Add<ERP.Web.Services.NoticeFilter>();
});

// Lưu khóa mã hóa cookie ra thư mục (volume Docker) để restart container không bị đăng xuất
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("ERP");
}

// ---------- Services ----------
builder.Services.Configure<ERP.Web.Services.ErpSettings>(builder.Configuration.GetSection("Erp"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ERP.Web.Services.CurrentUser>();
builder.Services.AddScoped<ERP.Web.Services.AccountingService>();
builder.Services.AddScoped<ERP.Web.Services.InventoryService>();
builder.Services.AddScoped<ERP.Web.Services.InvoiceService>();
builder.Services.AddScoped<ERP.Web.Services.PurchaseService>();
builder.Services.AddScoped<ERP.Web.Services.SalesService>();
builder.Services.AddScoped<ERP.Web.Services.CashService>();
builder.Services.AddScoped<ERP.Web.Services.AssetService>();
builder.Services.AddScoped<ERP.Web.Services.ProductionService>();
builder.Services.AddScoped<ERP.Web.Services.PeriodService>();
builder.Services.AddScoped<ERP.Web.Services.ReportService>();
builder.Services.AddScoped<ERP.Web.Services.PreferenceService>();
builder.Services.AddScoped<ERP.Web.Services.CatalogService>();
builder.Services.AddScoped<ERP.Web.Services.PurchaseReturnService>();
builder.Services.AddScoped<ERP.Web.Services.StandardReportService>();
builder.Services.AddScoped<ERP.Web.Services.SettingsService>();
builder.Services.AddScoped<ERP.Web.Services.WorkflowService>();
builder.Services.AddScoped<ERP.Web.Services.PayrollService>();
builder.Services.AddScoped<ERP.Web.Services.FxService>();
builder.Services.AddScoped<ERP.Web.Services.EInvoiceService>();
builder.Services.AddScoped<ERP.Web.Services.BudgetService>();
builder.Services.AddScoped<ERP.Web.Services.RecurringService>();
builder.Services.AddScoped<ERP.Web.Services.ToolService>();
builder.Services.AddScoped<ERP.Web.Services.OffsetService>();
builder.Services.AddScoped<ERP.Web.Services.BankService>();
builder.Services.AddScoped<ERP.Web.Services.CostingService>();
builder.Services.AddScoped<ERP.Web.Services.TaxService>();
builder.Services.AddScoped<ERP.Web.Services.ConsolidationService>();
builder.Services.AddScoped<ERP.Web.Services.RequisitionService>();
builder.Services.AddScoped<ERP.Web.Services.SupplierService>();
builder.Services.AddScoped<ERP.Web.Services.ThreeWayMatchService>();
builder.Services.AddScoped<ERP.Web.Services.LandedCostService>();
builder.Services.AddScoped<ERP.Web.Services.PricingService>();
builder.Services.AddScoped<ERP.Web.Services.QuoteService>();
builder.Services.AddScoped<ERP.Web.Services.CommissionService>();
builder.Services.AddScoped<ERP.Web.Services.StockReportService>();
builder.Services.AddScoped<ERP.Web.Services.StocktakeService>();
builder.Services.AddScoped<ERP.Web.Services.StandardMappingService>();
builder.Services.AddScoped<ERP.Web.Services.UserNotices>();
builder.Services.AddScoped<ERP.Web.Services.CodeService>();
builder.Services.AddScoped<ERP.Web.Services.DuplicateService>();
builder.Services.AddScoped<ERP.Web.Services.SalesCreditService>();
builder.Services.AddScoped<ERP.Web.Services.QualityService>();
builder.Services.AddScoped<ERP.Web.Services.ReconciliationService>();
// Lời gọi fetch() từ JavaScript gửi token chống giả mạo qua header
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseMiddleware<ERP.Web.Infrastructure.TranslationMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
// Phạm vi chi nhánh + người thao tác cho nhật ký: gán vào DbContext của request
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true) ERP.Web.Services.BranchScope.Apply(ctx, ctx.RequestServices.GetRequiredService<ERP.Web.Data.ErpDbContext>());
    await next();
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Tạo database + dữ liệu mẫu ở lần chạy đầu.
// Thử lại nhiều lần vì khi chạy bằng Docker, SQL Server có thể khởi động chậm hơn web app.
if (app.Configuration.GetValue("Database:SeedOnStartup", true))
{
    var retries = app.Configuration.GetValue("Database:StartupRetries", 12);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await DbSeeder.SeedAsync(app.Services);
            break;
        }
        catch (Exception ex) when (attempt < retries)
        {
            app.Logger.LogWarning("Chưa kết nối được database (lần {Attempt}/{Max}): {Message}. Thử lại sau 5 giây...",
                attempt, retries, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Không khởi tạo được database. Kiểm tra ConnectionStrings:ErpConnection");
            throw;
        }
    }
}

app.Run();
