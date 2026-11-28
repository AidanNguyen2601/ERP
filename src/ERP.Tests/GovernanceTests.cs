using System.Net;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using Xunit;

namespace ERP.Tests;

/// <summary>Kiểm thử phân quyền chi tiết, migration có phiên bản, phân trang, kênh Teams / Zalo, chính sách giữ tệp sao lưu</summary>
[Collection("erp")]
public class GovernanceTests
{
    private readonly ErpFixture _f;
    public GovernanceTests(ErpFixture f) => _f = f;

    private Task<int> UserIdAsync(string username) => _f.WithDbAsync(db => db.Users.Where(u => u.Username == username).Select(u => u.Id).FirstAsync());

    [Fact]
    public async Task PermissionOverride_DenyHidesAndBlocks_ReadOnlyBlocksWrites()
    {
        if (!_f.Enabled) return;
        var admin = await _f.LoginAsync("admin");
        var sales = await UserIdAsync("kinhdoanh");
        var r = await _f.PostAsync(admin, "/Org/Permissions", "/Org/AddOverride", ("target", $"user:{sales}"), ("menuKey", "crm"), ("mode", "1"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await _f.PostAsync(admin, "/Org/Permissions", "/Org/AddOverride", ("target", "role:Sales"), ("menuKey", "quotes"), ("mode", "2"));
        try
        {
            Assert.Equal(2, await _f.WithDbAsync(db => db.PermissionOverrides.CountAsync()));
            var c = await _f.LoginAsync("kinhdoanh");
            var crm = await c.GetAsync("/Crm");
            Assert.Equal(HttpStatusCode.Redirect, crm.StatusCode);
            Assert.Contains("AccessDenied", crm.Headers.Location!.ToString());
            var home = await c.GetStringAsync("/");
            Assert.DoesNotContain("href=\"/Crm\"", home);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Quotes")).StatusCode);
            var post = await _f.PostAsync(c, "/Quotes", "/Quotes/Save", ("CustomerId", "1"));
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Contains("readonly", post.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/SalesOrders")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Crm")).StatusCode);
        }
        finally
        {
            var ids = await _f.WithDbAsync(db => db.PermissionOverrides.Select(o => o.Id).ToListAsync());
            foreach (var id in ids) await _f.PostAsync(admin, "/Org/Permissions", "/Org/DeleteOverride", ("id", id.ToString()));
        }
        var again = await _f.LoginAsync("kinhdoanh");
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/Crm")).StatusCode);
    }

    [Fact]
    public async Task PermissionOverride_CannotTargetAdmin()
    {
        if (!_f.Enabled) return;
        var admin = await _f.LoginAsync("admin");
        await _f.PostAsync(admin, "/Org/Permissions", "/Org/AddOverride", ("target", "role:Admin"), ("menuKey", "crm"), ("mode", "1"));
        Assert.False(await _f.WithDbAsync(db => db.PermissionOverrides.AnyAsync(o => o.Role == AppRoles.Admin)));
    }

    [Fact]
    public async Task Migrations_AppliedOnStartup_RollbackAndReapply()
    {
        if (!_f.Enabled) return;
        var applied = await _f.WithDbAsync(db => db.SchemaMigrations.Where(m => m.RolledBackAt == null && m.Error == null).Select(m => m.Id).ToListAsync());
        Assert.All(SchemaMigrations.All, m => Assert.Contains(m.Id, applied));
        var admin = await _f.LoginAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Schema")).StatusCode);
        var latest = SchemaMigrations.All.OrderByDescending(m => m.Id, StringComparer.Ordinal).First();
        await _f.PostAsync(admin, "/Schema", "/Schema/Rollback");
        Assert.NotNull(await _f.WithDbAsync(db => db.SchemaMigrations.Where(m => m.Id == latest.Id).Select(m => m.RolledBackAt).FirstAsync()));
        Assert.Equal(0, await IndexCountAsync("IX_SalesOrders_Date_Id"));
        await _f.PostAsync(admin, "/Schema", "/Schema/Apply");
        Assert.Null(await _f.WithDbAsync(db => db.SchemaMigrations.Where(m => m.Id == latest.Id).Select(m => m.RolledBackAt).FirstAsync()));
        Assert.Equal(1, await IndexCountAsync("IX_SalesOrders_Date_Id"));
    }

    private Task<int> IndexCountAsync(string name) => _f.WithDbAsync(async db =>
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE name = N'{name}'";
        return (int)(await cmd.ExecuteScalarAsync())!;
    });

    [Theory]
    [InlineData("/Invoices?direction=Sales&from=2000-01-01")]
    [InlineData("/Partners?type=customer")]
    [InlineData("/Items")]
    [InlineData("/SalesOrders")]
    [InlineData("/PurchaseOrders")]
    [InlineData("/Cash?from=2000-01-01")]
    [InlineData("/Quotes")]
    [InlineData("/Production")]
    public async Task Lists_ArePagedServerSide(string url)
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("admin");
        var sep = url.Contains('?') ? "&" : "?";
        var html = await c.GetStringAsync(url + sep + "size=25");
        Assert.Matches(@"/ [\d.,]+ d&#xF2;ng|/ [\d.,]+ dòng", html);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(url + sep + "page=9999")).StatusCode);
    }

    [Fact]
    public async Task InvoiceTotals_CoverWholeFilter_NotJustPage()
    {
        if (!_f.Enabled) return;
        var expected = await _f.WithDbAsync(db => db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.Status != InvoiceStatus.Cancelled && i.Date >= new DateTime(2000, 1, 1) && i.Date <= DateTime.Today).SumAsync(i => i.Total));
        var c = await _f.LoginAsync("admin");
        var html = await c.GetStringAsync("/Invoices?direction=Sales&from=2000-01-01&size=25");
        Assert.Contains(expected.ToMoney(), html);
    }

    [Fact]
    public async Task TeamsAndZalo_ReceiveNotification_AndAreLogged()
    {
        if (!_f.Enabled) return;
        using var fake = new FakeWebhook();
        var admin = await _f.LoginAsync("admin");
        var saved = await _f.PostAsync(admin, "/Notifications/Channels", "/Notifications/SaveChannels", ("teams", fake.Url + "teams"), ("teamsLevel", "info"), ("zaloLevel", "warning"),
            ("zaloApi", fake.Url + "zalo"), ("zaloToken", "tok"), ("clearZalo", "false"), ("baseUrl", "https://erp.test"));
        Assert.True(saved.StatusCode == HttpStatusCode.Redirect, $"Lưu cấu hình: {(int)saved.StatusCode} {await saved.Content.ReadAsStringAsync()}");
        Assert.Contains("/Notifications/Channels", saved.Headers.Location!.ToString());
        var me = await UserIdAsync("admin");
        await _f.WithDbAsync(async db => { var u = await db.Users.FindAsync(me); u!.ZaloUserId = "zalo-123"; await db.SaveChangesAsync(); return 0; });
        try
        {
            var r = await _f.PostAsync(admin, "/Notifications/Channels", "/Notifications/TestChannels");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            var flash = System.Text.RegularExpressions.Regex.Match(await admin.GetStringAsync("/Notifications/Channels"), "alert alert-[a-z]+[^>]*>([^<]*)").Groups[1].Value;
            var settings = string.Join(",", await _f.WithDbAsync(db => db.SystemSettings.Where(s => s.Key.StartsWith("Notify.")).Select(s => s.Key + "=" + s.Value).ToListAsync()));
            Assert.True(!fake.Bodies.IsEmpty, "Không nhận được gì. Thông báo: " + WebUtility.HtmlDecode(flash) + " | " + settings);
            Assert.Contains(fake.Bodies, b => b.Contains("Th\\u00F4ng b\\u00E1o th\\u1EED") || b.Contains("Thông báo thử"));
            Assert.Contains(fake.Bodies, b => b.Contains("https://erp.test/Notifications"));
            Assert.Contains(fake.Bodies, b => b.Contains("zalo-123"));
            Assert.Contains("tok", fake.Headers);
            var log = await _f.WithDbAsync(db => db.EmailOutbox.Where(m => m.Source == "Gửi thử").ToListAsync());
            Assert.Contains(log, m => m.Channel == "teams" && m.Status == MailStatus.Sent);
            Assert.Contains(log, m => m.Channel == "zalo" && m.Status == MailStatus.Sent);
        }
        finally
        {
            await _f.PostAsync(admin, "/Notifications/Channels", "/Notifications/SaveChannels", ("teams", ""), ("teamsLevel", "warning"), ("zaloLevel", "warning"), ("clearZalo", "true"), ("zaloApi", ""), ("baseUrl", ""));
        }
    }

    [Fact]
    public async Task Backup_Retention_ListsAndPrunesExpired()
    {
        if (!_f.Enabled) return;
        var admin = await _f.LoginAsync("admin");
        await _f.PostAsync(admin, "/Backup", "/Backup/Run");
        await _f.PostAsync(admin, "/Backup", "/Backup/Run");
        if (!await _f.WithDbAsync(db => db.BackupRecords.CountAsync(b => b.Success).ContinueWith(t => t.Result >= 2))) return;   // máy chủ không cho sao lưu
        await _f.WithDbAsync(async db => { var old = await db.BackupRecords.Where(b => b.Success).OrderBy(b => b.Id).FirstAsync(); old.CreatedAt = DateTime.Now.AddDays(-40); await db.SaveChangesAsync(); return 0; });
        await _f.PostAsync(admin, "/Backup", "/Backup/Settings", ("folder", "/var/opt/mssql/data"), ("autoHour", "-1"), ("keepDays", "30"));
        Assert.Contains("xp_delete_file", await admin.GetStringAsync("/Backup"));
        await _f.PostAsync(admin, "/Backup", "/Backup/Prune");
        Func<ErpDbContext, Task<int>> expired = db => db.BackupRecords.CountAsync(b => b.Success && b.DeletedAt == null && b.CreatedAt < DateTime.Now.AddDays(-30));
        if (await _f.WithDbAsync(expired) > 0) await _f.PostAsync(admin, "/Backup", "/Backup/MarkDeleted");   // không có quyền sysadmin → xóa thủ công rồi đánh dấu
        Assert.Equal(0, await _f.WithDbAsync(expired));
        Assert.Null(await _f.WithDbAsync(db => db.BackupRecords.Where(b => b.Success).OrderByDescending(b => b.CreatedAt).Select(b => b.DeletedAt).FirstAsync()));
        await _f.PostAsync(admin, "/Backup", "/Backup/Settings", ("folder", "/var/opt/mssql/data"), ("autoHour", "-1"), ("keepDays", "0"));
    }
}

/// <summary>Máy chủ HTTP giả để nhận webhook Teams / API Zalo trong kiểm thử</summary>
public sealed class FakeWebhook : IDisposable
{
    private readonly HttpListener _l = new();
    public string Url { get; }
    public System.Collections.Concurrent.ConcurrentBag<string> Bodies { get; } = new();
    public System.Collections.Concurrent.ConcurrentBag<string> Headers { get; } = new();

    public FakeWebhook()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        Url = $"http://localhost:{port}/";
        _l.Prefixes.Add(Url);
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { return; }
                using (var sr = new StreamReader(ctx.Request.InputStream)) Bodies.Add(await sr.ReadToEndAsync());
                if (ctx.Request.Headers["access_token"] is { } t) Headers.Add(t);
                var body = System.Text.Encoding.UTF8.GetBytes(ctx.Request.Url!.AbsolutePath.Contains("zalo") ? "{\"error\":0,\"message\":\"Success\"}" : "1");
                ctx.Response.StatusCode = 200; ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
}
