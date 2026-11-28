using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Cảnh báo tự động: xem cảnh báo đang hiệu lực, cấu hình quy tắc, đếm cho chuông thông báo</summary>
[Authorize]
public class AlertsController : ErpControllerBase
{
    private readonly AlertService _alerts;
    public AlertsController(ErpDbContext db, AlertService alerts) : base(db) => _alerts = alerts;

    /// <summary>Mỗi nhóm cảnh báo chỉ hiện cho người có quyền liên quan</summary>
    private bool Visible(AlertItem a) => a.RuleKey switch
    {
        "ar-due" or "ap-due" or "credit" => User.SeesFinance() || (a.RuleKey != "ap-due" && User.InAny(AppRoles.SalesTeam)),
        "low-stock" => User.InAny(AppRoles.FinanceView + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing + "," + AppRoles.Production),
        "approvals" => User.InAny(AppRoles.FinanceView + "," + AppRoles.Purchasing),
        "contracts" => User.SeesFinance(),
        "maintenance" => User.InAny(AppRoles.FinanceView + "," + AppRoles.Production),
        "crm-tasks" or "quotes" => User.InAny(AppRoles.SalesTeam + "," + AppRoles.Director + "," + AppRoles.Accountant),
        _ => User.IsAdmin()
    };

    private async Task<List<AlertItem>> MineAsync() { await _alerts.EnsureRulesAsync(); return (await _alerts.ComputeAsync()).Where(Visible).ToList(); }

    public async Task<IActionResult> Index()
    {
        ViewBag.Rules = await Db.AlertRules.OrderBy(r => r.SortOrder).ToListAsync();
        ViewBag.IsAdmin = User.IsAdmin();
        return View(await MineAsync());
    }

    [HttpGet]
    public async Task<IActionResult> Count()
    {
        var items = await MineAsync();
        return Json(new { total = items.Count, danger = items.Count(i => i.Level == "danger") });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveRule(int id, bool isEnabled, decimal threshold, string? emailTo)
    {
        var r = await Db.AlertRules.FindAsync(id);
        if (r == null) return NotFound();
        r.IsEnabled = isEnabled; r.Threshold = Math.Max(0, threshold); r.EmailTo = string.IsNullOrWhiteSpace(emailTo) ? null : emailTo.Trim();
        await Db.SaveChangesAsync();
        Success($"Đã lưu quy tắc \"{r.Name}\".");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Lịch gửi báo cáo qua email và hộp thư đi</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class ReportSchedulesController : ErpControllerBase
{
    private readonly ReportScheduleService _svc; private readonly MailService _mail; private readonly SettingsService _set;
    public ReportSchedulesController(ErpDbContext db, ReportScheduleService svc, MailService mail, SettingsService set) : base(db) { _svc = svc; _mail = mail; _set = set; }

    public async Task<IActionResult> Index()
    {
        var me = Me; var admin = User.IsAdmin();
        ViewBag.Schedules = await Db.ReportSchedules.Include(s => s.SavedReport).Include(s => s.Owner).Where(s => admin || s.OwnerId == me).OrderBy(s => s.SavedReport!.Name).ToListAsync();
        ViewBag.Reports = await Db.SavedReports.Where(r => r.OwnerId == me || r.IsShared).OrderBy(r => r.Name).ToListAsync();
        ViewBag.Outbox = await Db.EmailOutbox.OrderByDescending(m => m.CreatedAt).Take(30).ToListAsync();
        ViewBag.Smtp = await _mail.ConfiguredAsync();
        ViewBag.SmtpHost = await _set.GetAsync("Smtp.Host"); ViewBag.SmtpPort = await _set.GetAsync("Smtp.Port", "587"); ViewBag.SmtpUser = await _set.GetAsync("Smtp.User");
        ViewBag.SmtpFrom = await _set.GetAsync("Smtp.From"); ViewBag.SmtpSsl = await _set.BoolAsync("Smtp.Ssl", true); ViewBag.NotifyEmail = await _set.BoolAsync("Notify.Email", false);
        ViewBag.IsAdmin = admin;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int savedReportId, ScheduleFrequency frequency, int hour, string recipients, string format)
    {
        var rep = await Db.SavedReports.FirstOrDefaultAsync(r => r.Id == savedReportId && (r.OwnerId == Me || r.IsShared));
        if (rep == null) throw new BusinessException("Không tìm thấy báo cáo.");
        var ds = DatasetRegistry.Find(rep.DatasetKey);
        if (ds == null || !DatasetRegistry.Allowed(ds, User)) return Denied();
        var list = (recipients ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (list.Count == 0 || list.Any(a => !System.Net.Mail.MailAddress.TryCreate(a, out _))) throw new BusinessException("Email người nhận không hợp lệ.");
        var s = new ReportSchedule { SavedReportId = rep.Id, Frequency = frequency, Hour = Math.Clamp(hour, 0, 23), Recipients = string.Join(", ", list), Format = format == "csv" ? "csv" : "xlsx", OwnerId = Me };
        s.NextRunAt = s.NextAfter(DateTime.Now);
        Db.ReportSchedules.Add(s);
        await Db.SaveChangesAsync();
        Success($"Đã đặt lịch gửi \"{rep.Name}\" – lần đầu {s.NextRunAt:dd/MM/yyyy HH:mm}.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<ReportSchedule?> Mine(int id)
    {
        var s = await Db.ReportSchedules.Include(x => x.SavedReport).FirstOrDefaultAsync(x => x.Id == id);
        return s != null && (s.OwnerId == Me || User.IsAdmin()) ? s : null;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var s = await Mine(id); if (s == null) return NotFound();
        s.IsActive = !s.IsActive; if (s.IsActive) s.NextRunAt = s.NextAfter(DateTime.Now);
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(int id)
    {
        var s = await Mine(id); if (s == null) return NotFound();
        await _svc.RunAsync(s, manual: true);
        Success("Kết quả: " + s.LastResult);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await Mine(id); if (s == null) return NotFound();
        Db.ReportSchedules.Remove(s); await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(int id, [FromServices] ChannelService channels)
    {
        var m = await Db.EmailOutbox.FindAsync(id);
        if (m == null) return NotFound();
        if (m.Channel is "teams" or "zalo") await channels.SendAsync(m); else await _mail.SendAsync(m);
        Success(m.Status == MailStatus.Sent ? "Đã gửi." : "Chưa gửi được: " + m.Error);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Attachment(int id)
    {
        var m = await Db.EmailOutbox.FindAsync(id);
        if (m?.AttachmentData == null) return NotFound();
        return File(m.AttachmentData, "application/octet-stream", m.AttachmentName ?? "report");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveSmtp(string? host, int port, string? user, string? password, string? from, bool ssl, bool notifyEmail)
    {
        await _set.SetAsync("Smtp.Host", host?.Trim() ?? "");
        await _set.SetAsync("Smtp.Port", (port <= 0 ? 587 : port).ToString());
        await _set.SetAsync("Smtp.User", user?.Trim() ?? "");
        if (!string.IsNullOrEmpty(password)) await _set.SetAsync("Smtp.Password", password);
        await _set.SetAsync("Smtp.From", from?.Trim() ?? "");
        await _set.SetAsync("Smtp.Ssl", ssl.ToString());
        await _set.SetAsync("Notify.Email", notifyEmail.ToString());
        Success("Đã lưu cấu hình SMTP.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Dashboard điều hành: doanh thu, công nợ, tồn kho, sản xuất theo thời gian thực (tự làm mới)</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class ExecutiveController : ErpControllerBase
{
    private readonly AccountingService _acc; private readonly AlertService _alerts;
    public ExecutiveController(ErpDbContext db, AccountingService acc, AlertService alerts) : base(db) { _acc = acc; _alerts = alerts; }

    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Data()
    {
        var today = DateTime.Today; var m0 = new DateTime(today.Year, today.Month, 1); var y0 = new DateTime(today.Year, 1, 1);
        var revMonth = -await _acc.MovementAsync("511", m0, today);
        var cogsMonth = await _acc.MovementAsync("632", m0, today);
        var revPrev = -await _acc.MovementAsync("511", m0.AddMonths(-1), m0.AddDays(-1));
        var revYear = -await _acc.MovementAsync("511", y0, today);
        var months = new List<object>();
        for (var i = 11; i >= 0; i--)
        {
            var f = m0.AddMonths(-i); var t = f.AddMonths(1).AddDays(-1);
            var rev = -await _acc.MovementAsync("511", f, t);
            var cost = await _acc.MovementAsync("632", f, t) + await _acc.MovementAsync("641", f, t) + await _acc.MovementAsync("642", f, t);
            months.Add(new { label = f.ToString("MM/yy"), revenue = rev, cost });
        }
        var cash = await _acc.BalanceAsync("111") + await _acc.BalanceAsync("112");
        var ar = await _acc.BalanceAsync("131"); var ap = -await _acc.BalanceAsync("331");
        var invs = await Db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).Select(i => new { i.Total, i.PaidAmount, i.DueDate }).ToListAsync();
        var overdue = invs.Where(i => i.DueDate < today).Sum(i => i.Total - i.PaidAmount);
        var aging = new[] { ("Trong hạn", invs.Where(i => i.DueDate >= today).Sum(i => i.Total - i.PaidAmount)), ("1–30 ngày", invs.Where(i => (today - i.DueDate).Days is >= 1 and <= 30).Sum(i => i.Total - i.PaidAmount)),
            ("31–60 ngày", invs.Where(i => (today - i.DueDate).Days is > 30 and <= 60).Sum(i => i.Total - i.PaidAmount)), ("> 60 ngày", invs.Where(i => (today - i.DueDate).Days > 60).Sum(i => i.Total - i.PaidAmount)) };
        var invValue = await Db.StockLevels.Where(s => s.Item!.Type != ItemType.Service).SumAsync(s => (decimal?)(s.Quantity * s.Item!.AvgCost)) ?? 0;
        var lowStock = await Db.Items.CountAsync(i => i.Type != ItemType.Service && i.IsActive && i.MinStock > 0 && (Db.StockLevels.Where(s => s.ItemId == i.Id).Sum(s => (decimal?)s.Quantity) ?? 0) < i.MinStock);
        var open = new[] { ProductionStatus.Draft, ProductionStatus.Released, ProductionStatus.InProgress };
        var po = await Db.ProductionOrders.Where(p => open.Contains(p.Status)).Select(p => new { p.Quantity, p.CompletedQty }).ToListAsync();
        var doneMonth = await Db.ProductionOrders.Where(p => p.Status == ProductionStatus.Completed).CountAsync();
        var topCust = (await Db.Invoices.Include(i => i.Partner).Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.Date >= y0).ToListAsync())
            .GroupBy(i => i.Partner?.Name ?? "?").Select(g => new { name = g.Key, value = g.Sum(i => i.Subtotal) }).OrderByDescending(x => x.value).Take(5).ToList();
        var alerts = await _alerts.ComputeAsync();
        var opps = await Db.Opportunities.Where(o => o.Stage != OppStage.Won && o.Stage != OppStage.Lost).Select(o => new { o.Amount, o.Probability }).ToListAsync();
        return Json(new
        {
            at = DateTime.Now.ToString("HH:mm:ss"),
            revenueMonth = revMonth, revenuePrev = revPrev, revenueYear = revYear, grossProfitMonth = revMonth - cogsMonth,
            cash, receivable = ar, payable = ap, overdue, aging = aging.Select(a => new { label = a.Item1, value = a.Item2 }),
            inventoryValue = invValue, lowStock,
            production = new { open = po.Count, plan = po.Sum(p => p.Quantity), done = po.Sum(p => p.CompletedQty), completed = doneMonth },
            pendingApprovals = await Db.ApprovalRequests.CountAsync(a => a.Status == ApprovalStatus.Pending),
            soToDeliver = await Db.SalesOrders.CountAsync(s => s.Status == SoStatus.Confirmed || s.Status == SoStatus.PartiallyDelivered),
            headcount = await Db.Employees.CountAsync(e => e.IsActive),
            equipmentDown = await Db.Equipments.CountAsync(e => e.Status == EquipmentStatus.Down),
            pipeline = new { count = opps.Count, value = opps.Sum(o => o.Amount), weighted = opps.Sum(o => o.Amount * o.Probability / 100m) },
            months, topCustomers = topCust,
            alertsDanger = alerts.Count(a => a.Level == "danger"), alertsTotal = alerts.Count,
            topAlerts = alerts.Take(6).Select(a => new { a.Level, Title = Lang.T(HttpContext, a.Title), Detail = Lang.T(HttpContext, a.Detail) })
        });
    }
}

/// <summary>Hộp thông báo của người dùng</summary>
[Authorize]
public class NotificationsController : ErpControllerBase
{
    public NotificationsController(ErpDbContext db) : base(db) { }

    public async Task<IActionResult> Index(bool unread = false, int page = 1)
    {
        var me = Me;
        var q = Db.Notifications.Where(n => n.UserId == me);
        if (unread) q = q.Where(n => !n.IsRead);
        const int size = 50;
        ViewBag.Unread = unread; ViewBag.Page = page; ViewBag.Total = await q.CountAsync();
        return View(await q.OrderByDescending(n => n.CreatedAt).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> Count() => Json(new { unread = await Db.Notifications.CountAsync(n => n.UserId == Me && !n.IsRead) });

    /// <summary>Đánh dấu đã đọc rồi chuyển tới nội dung liên quan</summary>
    public async Task<IActionResult> Open(long id)
    {
        var n = await Db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Me);
        if (n == null) return NotFound();
        if (!n.IsRead) { n.IsRead = true; await Db.SaveChangesAsync(); }
        return !string.IsNullOrEmpty(n.Url) && Url.IsLocalUrl(n.Url) ? Redirect(n.Url) : RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ kênh gửi (quản trị)
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Channels([FromServices] SettingsService set)
    {
        ViewBag.Teams = await set.GetAsync("Notify.TeamsWebhook"); ViewBag.TeamsLevel = await set.GetAsync("Notify.TeamsMinLevel", "warning");
        ViewBag.ZaloSet = (await set.GetAsync("Notify.ZaloToken")).Length > 0; ViewBag.ZaloLevel = await set.GetAsync("Notify.ZaloMinLevel", "warning");
        ViewBag.ZaloApi = await set.GetAsync("Notify.ZaloApiUrl", ChannelService.DefaultZaloApi); ViewBag.BaseUrl = await set.GetAsync("App.BaseUrl");
        ViewBag.Log = await Db.EmailOutbox.Where(m => m.Channel != null).OrderByDescending(m => m.CreatedAt).Take(20).ToListAsync();
        return View(await Db.Users.Where(u => u.IsActive).OrderBy(u => u.Username).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveChannels([FromServices] SettingsService set, string? teams, string teamsLevel, string? zaloToken, bool clearZalo, string zaloLevel, string? zaloApi, string? baseUrl, [FromForm(Name = "zaloIds")] Dictionary<string, string?>? zaloIds)
    {
        static string Lv(string v) => v is "info" or "warning" or "danger" ? v : "warning";
        teams = teams?.Trim() ?? "";
        if (teams.Length > 0 && !Uri.TryCreate(teams, UriKind.Absolute, out var tu) | (tu != null && tu.Scheme != Uri.UriSchemeHttps && !tu.IsLoopback)) throw new BusinessException("Teams webhook phải là địa chỉ https hợp lệ.");
        await set.SetAsync("Notify.TeamsWebhook", teams);
        await set.SetAsync("Notify.TeamsMinLevel", Lv(teamsLevel));
        if (clearZalo) await set.SetAsync("Notify.ZaloToken", ""); else if (!string.IsNullOrWhiteSpace(zaloToken)) await set.SetAsync("Notify.ZaloToken", zaloToken.Trim());
        await set.SetAsync("Notify.ZaloMinLevel", Lv(zaloLevel));
        await set.SetAsync("Notify.ZaloApiUrl", string.IsNullOrWhiteSpace(zaloApi) ? ChannelService.DefaultZaloApi : zaloApi.Trim());
        await set.SetAsync("App.BaseUrl", baseUrl?.Trim().TrimEnd('/') ?? "");
        var zalo = (zaloIds ?? new()).Where(x => int.TryParse(x.Key, out _)).ToDictionary(x => int.Parse(x.Key), x => x.Value);
        if (zalo.Count > 0)
            foreach (var u in await Db.Users.Where(u => zalo.Keys.Contains(u.Id)).ToListAsync())
                u.ZaloUserId = string.IsNullOrWhiteSpace(zalo[u.Id]) ? null : zalo[u.Id]!.Trim();
        await Db.SaveChangesAsync();
        Success("Đã lưu cấu hình kênh thông báo.");
        return RedirectToAction(nameof(Channels));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> TestChannels([FromServices] ChannelService channels)
    {
        var me = await Db.Users.FirstAsync(u => u.Id == Me);
        await channels.DispatchAsync(new[] { me }, "Thông báo thử từ ERP", $"Gửi bởi {me.FullName} lúc {DateTime.Now:dd/MM/yyyy HH:mm}", "/Notifications", "danger", "Gửi thử");
        var log = await Db.EmailOutbox.Where(m => m.Channel != null && m.Source == "Gửi thử").OrderByDescending(m => m.Id).Take(2).ToListAsync();
        if (log.Count == 0) Error("Chưa cấu hình kênh nào (hoặc bạn chưa có Zalo ID).");
        else Success("Kết quả: " + string.Join("; ", log.Select(l => $"{l.Channel}: {l.Status.GetDisplayName()}{(l.Error != null ? " – " + l.Error : "")}")));
        return RedirectToAction(nameof(Channels));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAll()
    {
        var me = Me;
        await Db.Notifications.Where(n => n.UserId == me && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return RedirectToAction(nameof(Index));
    }
}
