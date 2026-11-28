using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using Xunit;

namespace ERP.Tests;

/// <summary>
/// Kiểm thử tích hợp trên SQL Server thật: tạo database tạm, nạp dữ liệu mẫu, chạy ứng dụng đầy đủ.
/// Nguồn SQL Server (theo thứ tự):
///   1. Biến môi trường ERP_TEST_CONN = chuỗi kết nối tới máy chủ SQL có quyền tạo database;
///   2. Không có biến → tự chạy container SQL Server tạm qua Docker (Testcontainers), xong thì xóa;
///   3. Không có Docker → bỏ qua kiểm thử tích hợp (ghi lý do ra console). Đặt ERP_TEST_REQUIRE_DB=1 để báo lỗi thay vì bỏ qua.
/// </summary>
public class ErpFixture : IAsyncLifetime
{
    public string? Server { get; private set; } = Environment.GetEnvironmentVariable("ERP_TEST_CONN");
    public bool Enabled => !string.IsNullOrWhiteSpace(Server);
    public string DbName { get; } = "ERP_TEST_" + Guid.NewGuid().ToString("N")[..8];
    public WebApplicationFactory<Program>? Factory { get; private set; }
    private Testcontainers.MsSql.MsSqlContainer? _container;

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            try
            {
                _container = new Testcontainers.MsSql.MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await _container.StartAsync();
                var b = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "", MultipleActiveResultSets = true, TrustServerCertificate = true };
                Server = b.ConnectionString;
            }
            catch (Exception ex)
            {
                _container = null;
                if (Environment.GetEnvironmentVariable("ERP_TEST_REQUIRE_DB") == "1") throw new InvalidOperationException("Không có SQL Server cho kiểm thử tích hợp (đặt ERP_TEST_CONN hoặc bật Docker).", ex);
                Console.WriteLine("[ERP.Tests] Bỏ qua kiểm thử tích hợp: không có ERP_TEST_CONN và không chạy được Docker – " + ex.Message);
                return;
            }
        }
        var cs = Server!.TrimEnd(';') + ";Database=" + DbName;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:ErpConnection", cs);
            b.UseSetting("Database:SeedOnStartup", "true");
        });
        _ = Factory.Server;   // khởi động ứng dụng → tạo database và nạp dữ liệu mẫu
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        if (_container != null) { await _container.DisposeAsync(); return; }   // container tạm: xóa cả máy chủ
        if (!Enabled) return;
        try
        {
            await using var c = new SqlConnection(Server);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{DbName}') IS NOT NULL BEGIN ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DbName}]; END";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* dọn dẹp best-effort */ }
    }

    public HttpClient NewClient() => Factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public async Task<HttpClient> LoginAsync(string user)
    {
        var c = NewClient();
        var page = await c.GetStringAsync("/Account/Login");
        var r = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Username"] = user, ["Password"] = "Abc@123", ["__RequestVerificationToken"] = Token(page) }));
        Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"Đăng nhập {user} thất bại: {r.StatusCode}");
        return c;
    }

    public static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>Rút nội dung lỗi từ trang lỗi dành cho nhà phát triển (để thông báo kiểm thử thất bại có nguyên nhân)</summary>
    public static string ErrorText(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline), "<[^>]+>", " "));
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > 1500 ? text[..1500] : text;
    }

    public async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string url, params (string, string)[] data)
    {
        var tok = Token(await c.GetStringAsync(formPage));
        var d = data.ToDictionary(x => x.Item1, x => x.Item2); d["__RequestVerificationToken"] = tok;
        return await c.PostAsync(url, new FormUrlEncodedContent(d));
    }

    public async Task<T> WithDbAsync<T>(Func<ErpDbContext, Task<T>> fn)
    {
        using var scope = Factory!.Services.CreateScope();
        return await fn(scope.ServiceProvider.GetRequiredService<ErpDbContext>());
    }
}

[CollectionDefinition("erp")]
public class ErpCollection : ICollectionFixture<ErpFixture> { }

[Collection("erp")]
public class SmokeAndAccessTests
{
    private readonly ErpFixture _f;
    public SmokeAndAccessTests(ErpFixture f) => _f = f;

    public static readonly string[] Pages =
    {
        "/", "/Journals", "/Reports/TrialBalance", "/Reconciliation", "/Invoices", "/Partners", "/Items", "/Stock/Alerts", "/SalesOrders", "/PurchaseOrders", "/Quotes", "/Production",
        "/QcInspections", "/Equipment", "/MaintenancePlans", "/Payroll", "/Payroll/Employees", "/Hr/Contracts", "/Hr/Shifts", "/Hr/TimeClock", "/Hr/Leave", "/Hr/Components",
        "/Hr/Bonuses", "/Hr/Insurance", "/Hr/PitFinalization", "/Crm", "/Crm/Leads", "/Crm/Pipeline", "/Crm/Activities", "/Executive", "/Executive/Data", "/Alerts", "/Alerts/Count",
        "/ReportSchedules", "/DynamicReports", "/ReportCenter", "/Approvals", "/Audit", "/Org/Permissions", "/Settings"
    };

    [Fact]
    public async Task Admin_CanOpenAllMainPages()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var bad = new List<string>();
        foreach (var p in Pages)
        {
            var r = await c.GetAsync(p);
            if (r.StatusCode != HttpStatusCode.OK) bad.Add($"{p} → {(int)r.StatusCode}");
        }
        Assert.True(bad.Count == 0, "Trang lỗi: " + string.Join("; ", bad));
    }

    [Theory]
    [InlineData("kinhdoanh", "/Hr/Contracts")]
    [InlineData("kinhdoanh", "/Payroll")]
    [InlineData("thukho", "/Executive")]
    [InlineData("thukho", "/Journals")]
    [InlineData("muahang", "/Payroll")]
    [InlineData("qc", "/Hr/PitFinalization")]
    public async Task RestrictedPages_AreBlockedForOtherRoles(string user, string url)
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync(user);
        var r = await c.GetAsync(url);
        Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Forbidden, $"{user} vào {url} được phép ({(int)r.StatusCode})");
        if (r.Headers.Location != null) Assert.Contains("AccessDenied", r.Headers.Location.ToString());
    }

    [Fact]
    public async Task AnonymousUser_IsRedirectedToLogin()
    {
        if (!_f.Enabled) return;
        var r = await _f.NewClient().GetAsync("/Journals");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Sales_CanUseCrm()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("kinhdoanh");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Crm")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Crm/Pipeline")).StatusCode);
    }
}

[Collection("erp")]
public class AccountingInvariantTests
{
    private readonly ErpFixture _f;
    public AccountingInvariantTests(ErpFixture f) => _f = f;

    [Fact]
    public async Task EveryJournalEntry_IsBalanced()
    {
        if (!_f.Enabled) return;
        var unbalanced = await _f.WithDbAsync(db => db.JournalLines.GroupBy(l => l.JournalEntryId)
            .Where(g => Math.Abs(g.Sum(l => l.Debit) - g.Sum(l => l.Credit)) > 0.5m).Select(g => g.Key).ToListAsync());
        Assert.True(unbalanced.Count == 0, "Bút toán lệch: " + string.Join(",", unbalanced.Take(10)));
    }

    [Fact]
    public async Task TrialBalance_TotalDebitEqualsTotalCredit()
    {
        if (!_f.Enabled) return;
        var (d, cr) = await _f.WithDbAsync(async db => (await db.JournalLines.SumAsync(l => l.Debit), await db.JournalLines.SumAsync(l => l.Credit)));
        Assert.Equal(d, cr);
    }

    [Fact]
    public async Task Reconciliation_AllModulesMatchLedger()
    {
        if (!_f.Enabled) return;
        using var scope = _f.Factory!.Services.CreateScope();
        var checks = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync();
        var bad = checks.Where(x => !x.Ok).Select(x => x.Code + " " + x.Title).ToList();
        Assert.True(bad.Count == 0, "Đối chiếu lệch: " + string.Join("; ", bad));
    }

    [Fact]
    public async Task DocumentNumbers_AreUnique()
    {
        if (!_f.Enabled) return;
        var dup = await _f.WithDbAsync(db => db.JournalEntries.GroupBy(j => j.Number).Where(g => g.Count() > 1).Select(g => g.Key).ToListAsync());
        Assert.Empty(dup);
    }
}

[Collection("erp")]
public class WorkflowTests
{
    private readonly ErpFixture _f;
    public WorkflowTests(ErpFixture f) => _f = f;

    [Fact]
    public async Task Payroll_CreateSubmitApprove_PostsBalancedEntry()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var now = DateTime.Today.AddMonths(2);
        var r = await _f.PostAsync(c, "/Payroll", "/Payroll/Create", ("year", now.Year.ToString()), ("month", now.Month.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var runId = int.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Payroll/Details/(\d+)").Groups[1].Value);
        var details = await c.GetStringAsync($"/Payroll/Details/{runId}");
        Assert.Contains("NV0001", details);

        r = await _f.PostAsync(c, $"/Payroll/Details/{runId}", "/Payroll/Submit", ("id", runId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var req = await _f.WithDbAsync(db => db.ApprovalRequests.Where(a => a.Status == ApprovalStatus.Pending && a.DocNumber.StartsWith("BL")).OrderByDescending(a => a.Id).FirstAsync());
        r = await _f.PostAsync(c, "/Approvals", "/Approvals/Act", ("id", req.Id.ToString()), ("approve", "true"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var (status, entryId) = await _f.WithDbAsync(async db => { var p = await db.PayrollRuns.FindAsync(runId); return (p!.Status, p.JournalEntryId); });
        Assert.NotNull(entryId);
        var sums = await _f.WithDbAsync(async db => (await db.JournalLines.Where(l => l.JournalEntryId == entryId).SumAsync(l => l.Debit), await db.JournalLines.Where(l => l.JournalEntryId == entryId).SumAsync(l => l.Credit)));
        Assert.True(sums.Item1 > 0);
        Assert.Equal(sums.Item1, sums.Item2);
    }

    [Fact]
    public async Task Crm_ConvertLead_CreatesCustomerAndOpportunity()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("kinhdoanh");
        var lead = await _f.WithDbAsync(db => db.Leads.Where(l => l.Stage == LeadStage.New || l.Stage == LeadStage.Contacted || l.Stage == LeadStage.Qualified).OrderByDescending(l => l.Id).FirstAsync());
        var r = await _f.PostAsync(c, $"/Crm/LeadDetails/{lead.Id}", "/Crm/ConvertLead", ("id", lead.Id.ToString()), ("oppTitle", "Test"), ("amount", "1000000"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var after = await _f.WithDbAsync(db => db.Leads.Include(l => l.Partner).FirstAsync(l => l.Id == lead.Id));
        Assert.Equal(LeadStage.Converted, after.Stage);
        Assert.NotNull(after.PartnerId);
        Assert.True(await _f.WithDbAsync(db => db.Opportunities.AnyAsync(o => o.LeadId == lead.Id && o.PartnerId == after.PartnerId)));
    }

    [Fact]
    public async Task Leave_DuplicateRequestIsRejected_ApprovalWritesAttendance()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var emp = await _f.WithDbAsync(db => db.Employees.Where(e => e.IsActive).OrderBy(e => e.Id).Skip(6).FirstAsync());
        var type = await _f.WithDbAsync(db => db.LeaveTypes.FirstAsync(t => t.IsPaid));
        var day = DateTime.Today.AddDays(40); while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        (string, string)[] form = { ("EmployeeId", emp.Id.ToString()), ("LeaveTypeId", type.Id.ToString()), ("Days", "1"), ("FromDate", day.ToString("yyyy-MM-dd")), ("ToDate", day.ToString("yyyy-MM-dd")), ("Reason", "test") };
        await _f.PostAsync(c, "/Hr/Leave", "/Hr/RequestLeave", form);
        await _f.PostAsync(c, "/Hr/Leave", "/Hr/RequestLeave", form);
        var reqs = await _f.WithDbAsync(db => db.LeaveRequests.Where(l => l.EmployeeId == emp.Id && l.FromDate == day.Date).ToListAsync());
        Assert.Single(reqs);
        await _f.PostAsync(c, "/Hr/Leave", "/Hr/DecideLeave", ("id", reqs[0].Id.ToString()), ("approve", "true"));
        Assert.True(await _f.WithDbAsync(db => db.AttendanceDays.AnyAsync(a => a.EmployeeId == emp.Id && a.Date == day.Date && a.Kind == AttendanceKind.Leave)));
    }

    [Fact]
    public async Task Alerts_AndExecutiveDashboard_ReturnData()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var json = await c.GetStringAsync("/Executive/Data");
        Assert.Contains("\"revenueMonth\"", json);
        Assert.Contains("\"pipeline\"", json);
        var cnt = await c.GetStringAsync("/Alerts/Count");
        Assert.Contains("\"total\"", cnt);
    }
}

[Collection("erp")]
public class PlatformTests
{
    private readonly ErpFixture _f;
    public PlatformTests(ErpFixture f) => _f = f;

    [Fact]
    public async Task Approval_NotifiesApproversThenRequester()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var now = DateTime.Today.AddMonths(3);
        var r = await _f.PostAsync(c, "/Payroll", "/Payroll/Create", ("year", now.Year.ToString()), ("month", now.Month.ToString()));
        var runId = int.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Payroll/Details/(\d+)").Groups[1].Value);
        var before = await _f.WithDbAsync(db => db.Notifications.CountAsync(n => n.Source == "Phê duyệt"));
        await _f.PostAsync(c, $"/Payroll/Details/{runId}", "/Payroll/Submit", ("id", runId.ToString()));
        var mid = await _f.WithDbAsync(db => db.Notifications.CountAsync(n => n.Source == "Phê duyệt"));
        Assert.True(mid > before, "Người duyệt phải nhận được thông báo khi có yêu cầu duyệt");
        var req = await _f.WithDbAsync(db => db.ApprovalRequests.Where(a => a.Status == ApprovalStatus.Pending).OrderByDescending(a => a.Id).FirstAsync());
        await _f.PostAsync(c, "/Approvals", "/Approvals/Act", ("id", req.Id.ToString()), ("approve", "true"));
        var done = await _f.WithDbAsync(db => db.Notifications.CountAsync(n => n.Source == "Phê duyệt" && n.Level == "success"));
        Assert.True(done > 0, "Người lập phải nhận thông báo kết quả duyệt");
    }

    [Fact]
    public async Task Notifications_CanBeReadAndMarked()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        Assert.Contains("\"unread\"", await c.GetStringAsync("/Notifications/Count"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Notifications")).StatusCode);
        var r = await _f.PostAsync(c, "/Notifications", "/Notifications/MarkAll");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("\"unread\":0", await c.GetStringAsync("/Notifications/Count"));
    }

    [Fact]
    public async Task Audit_RecordsFieldChangesOfNewModules()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("kinhdoanh");
        var lead = await _f.WithDbAsync(db => db.Leads.AsNoTracking().FirstAsync(l => l.Stage != LeadStage.Converted && l.Stage != LeadStage.Lost && l.EstimatedValue != 777777));
        await _f.PostAsync(c, $"/Crm/LeadForm/{lead.Id}", "/Crm/SaveLead", ("Id", lead.Id.ToString()), ("Name", lead.Name), ("Source", ((int)lead.Source).ToString()), ("Stage", ((int)lead.Stage).ToString()), ("EstimatedValue", "777777"));
        var log = await _f.WithDbAsync(db => db.AuditLogs.Where(a => a.EntityType == nameof(Lead) && a.EntityId == lead.Id.ToString() && a.Action == "Sửa").OrderByDescending(a => a.Id).FirstOrDefaultAsync());
        Assert.NotNull(log);
        Assert.Contains("EstimatedValue", log!.Changes);
        Assert.Contains("777777", log.Changes);
    }

    [Fact]
    public async Task Backup_OnlyAdmin_AndRecordsHistory()
    {
        if (!_f.Enabled) return;
        var other = await _f.LoginAsync("ketoan");
        var denied = await other.GetAsync("/Backup");
        Assert.True(denied.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden);
        var c = await _f.LoginAsync("admin");
        var bk = await c.GetAsync("/Backup");
        var bkBody = await bk.Content.ReadAsStringAsync();
        Assert.True(bk.StatusCode == HttpStatusCode.OK, "/Backup lỗi: " + ErpFixture.ErrorText(bkBody));
        await _f.PostAsync(c, "/Backup", "/Backup/Run");
        var rec = await _f.WithDbAsync(db => db.BackupRecords.OrderByDescending(b => b.Id).FirstOrDefaultAsync());
        Assert.NotNull(rec);
        if (Environment.GetEnvironmentVariable("ERP_TEST_BACKUP") == "1") { Assert.True(rec!.Success, rec.Error); Assert.True(rec.Verified, rec.Error); }
    }

    [Fact]
    public async Task PerformanceIndexes_Exist()
    {
        if (!_f.Enabled) return;
        var n = await _f.WithDbAsync(async db =>
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name IN (N'IX_JournalLines_Account_Cover', N'IX_Invoices_Direction_Status_DueDate', N'IX_ApprovalRequests_Status_RequestedAt', N'IX_AuditLogs_At')";
            return (int)(await cmd.ExecuteScalarAsync())!;
        });
        Assert.Equal(4, n);
    }

    [Fact]
    public async Task ExecutiveDashboard_StaysFastOnSeededData()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        await c.GetStringAsync("/Executive/Data");   // làm nóng
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await c.GetStringAsync("/Executive/Data"); await c.GetStringAsync("/"); await c.GetStringAsync("/Alerts");
        Assert.True(sw.ElapsedMilliseconds < 8000, $"Dashboard + trang chủ + cảnh báo mất {sw.ElapsedMilliseconds} ms");
    }
}
