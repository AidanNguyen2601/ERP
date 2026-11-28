using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Services;

/// <summary>Một cảnh báo đang hiệu lực</summary>
public record AlertItem(string RuleKey, string Level, string Title, string Detail, string Controller, string Action, object? Route = null);

/// <summary>Cảnh báo tự động: công nợ đến hạn, tồn kho tối thiểu, chứng từ chờ duyệt, hợp đồng hết hạn, bảo dưỡng, hạn mức công nợ, công việc CRM quá hạn</summary>
public class AlertService
{
    private readonly ErpDbContext _db;
    private readonly StockReportService _stock;
    public AlertService(ErpDbContext db, StockReportService stock) { _db = db; _stock = stock; }

    public static readonly (string Key, string Name, string Desc, decimal Threshold, string Label)[] Defaults =
    {
        ("ar-due", "Công nợ phải thu đến hạn / quá hạn", "Hóa đơn bán ra chưa thu, đến hạn trong N ngày tới hoặc đã quá hạn", 7, "ngày"),
        ("ap-due", "Công nợ phải trả đến hạn / quá hạn", "Hóa đơn mua vào chưa trả, đến hạn trong N ngày tới hoặc đã quá hạn", 7, "ngày"),
        ("low-stock", "Tồn kho dưới mức tối thiểu / lô hết hạn", "Mặt hàng dưới tồn tối thiểu; lô đã hoặc sắp hết hạn", 0, ""),
        ("approvals", "Chứng từ chờ duyệt", "Yêu cầu phê duyệt chưa xử lý quá N ngày (0 = tất cả)", 1, "ngày"),
        ("contracts", "Hợp đồng lao động sắp hết hạn", "Hợp đồng lao động hết hạn trong N ngày tới", 30, "ngày"),
        ("maintenance", "Bảo dưỡng đến hạn & thiết bị hỏng", "Kế hoạch bảo dưỡng quá hạn hoặc đến hạn trong N ngày; thiết bị đang dừng", 7, "ngày"),
        ("credit", "Vượt hạn mức công nợ khách hàng", "Công nợ phải thu của khách vượt hạn mức đã đặt", 0, ""),
        ("crm-tasks", "Công việc CRM quá hạn", "Công việc chăm sóc khách hàng quá hạn chưa hoàn thành", 0, ""),
        ("quotes", "Báo giá sắp hết hiệu lực", "Báo giá hết hiệu lực trong N ngày tới", 3, "ngày"),
    };

    public async Task EnsureRulesAsync()
    {
        var have = (await _db.AlertRules.Select(r => r.Key).ToListAsync()).ToHashSet();
        var order = 0;
        foreach (var d in Defaults)
        {
            order++;
            if (have.Contains(d.Key)) continue;
            _db.AlertRules.Add(new AlertRule { Key = d.Key, Name = d.Name, Description = d.Desc, Threshold = d.Threshold, ThresholdLabel = d.Label, SortOrder = order });
        }
        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync();
    }

    public async Task<List<AlertItem>> ComputeAsync(DateTime? now = null, bool ignoreDisabled = false)
    {
        var today = (now ?? DateTime.Now).Date;
        var rules = await _db.AlertRules.AsNoTracking().ToListAsync();
        var on = rules.Where(r => ignoreDisabled || r.IsEnabled).ToDictionary(r => r.Key, r => r.Threshold);
        var list = new List<AlertItem>();

        foreach (var (key, dir, label) in new[] { ("ar-due", InvoiceDirection.Sales, "phải thu"), ("ap-due", InvoiceDirection.Purchase, "phải trả") })
        {
            if (!on.TryGetValue(key, out var days)) continue;
            var lim = today.AddDays((double)days);
            var invs = await _db.Invoices.Include(i => i.Partner).Where(i => i.Direction == dir && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total && i.DueDate <= lim).OrderBy(i => i.DueDate).Take(30).ToListAsync();
            foreach (var i in invs)
            {
                var late = (today - i.DueDate.Date).Days;
                list.Add(new AlertItem(key, late > 0 ? "danger" : "warning", late > 0 ? $"Quá hạn {late} ngày – {label}" : $"Đến hạn {i.DueDate:dd/MM} – {label}",
                    $"{i.Number} · {i.Partner?.Name} · còn {i.Outstanding.ToMoney()} đ", "Invoices", "Details", new { id = i.Id }));
            }
        }
        if (on.ContainsKey("low-stock"))
        {
            foreach (var a in (await _stock.AlertsAsync(today)).Where(a => a.Kind is "min" or "expired" or "expiring").Take(40))
                list.Add(new AlertItem("low-stock", a.Kind == "expiring" ? "warning" : a.Kind == "min" ? "warning" : "danger", a.Kind == "min" ? "Tồn kho dưới mức tối thiểu" : a.Kind == "expired" ? "Lô đã hết hạn" : "Lô sắp hết hạn",
                    $"{a.Item.Code} {a.Item.Name} · {a.Message}", "Stock", "Alerts"));
        }
        if (on.TryGetValue("approvals", out var ad))
        {
            var cut = today.AddDays(-(double)ad);
            foreach (var r in await _db.ApprovalRequests.Where(r => r.Status == ApprovalStatus.Pending && r.RequestedAt < cut.AddDays(1)).OrderBy(r => r.RequestedAt).Take(30).ToListAsync())
                list.Add(new AlertItem("approvals", (today - r.RequestedAt.Date).Days > 3 ? "danger" : "warning", $"Chờ duyệt {(today - r.RequestedAt.Date).Days} ngày", $"{r.DocType.GetDisplayName()} {r.DocNumber} · {r.Summary} · {r.Amount.ToMoney()} đ", "Approvals", "Index"));
        }
        if (on.TryGetValue("contracts", out var cd))
        {
            var lim = today.AddDays((double)cd);
            foreach (var c in await _db.LaborContracts.Include(c => c.Employee).Where(c => c.Status == LaborContractStatus.Active && c.EndDate != null && c.EndDate <= lim && c.Employee!.IsActive).OrderBy(c => c.EndDate).Take(30).ToListAsync())
                list.Add(new AlertItem("contracts", c.DaysLeft < 0 ? "danger" : "warning", c.DaysLeft < 0 ? $"Hợp đồng lao động đã hết hạn {-c.DaysLeft} ngày" : $"Hợp đồng lao động còn {c.DaysLeft} ngày",
                    $"{c.Employee?.Code} {c.Employee?.FullName} · {c.Number} · hết hạn {c.EndDate:dd/MM/yyyy}", "Hr", "Contracts"));
        }
        if (on.TryGetValue("maintenance", out var md))
        {
            var lim = today.AddDays((double)md);
            foreach (var p in await _db.MaintenancePlans.Include(p => p.Equipment).Where(p => p.Status == MaintenancePlanStatus.Active && p.NextDueDate <= lim).OrderBy(p => p.NextDueDate).Take(30).ToListAsync())
            {
                var late = (today - p.NextDueDate.Date).Days;
                list.Add(new AlertItem("maintenance", late > 0 ? "danger" : "warning", late > 0 ? $"Bảo dưỡng quá hạn {late} ngày" : $"Bảo dưỡng đến hạn {p.NextDueDate:dd/MM}", $"{p.Equipment?.Code} {p.Equipment?.Name} · {p.Title}", "MaintenancePlans", "Index"));
            }
            foreach (var e in await _db.Equipments.Where(e => e.Status == EquipmentStatus.Down).Take(20).ToListAsync())
                list.Add(new AlertItem("maintenance", "danger", "Thiết bị đang dừng (hỏng)", $"{e.Code} {e.Name}", "Equipment", "Index"));
        }
        if (on.ContainsKey("credit"))
        {
            var debt = (await _db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).Select(i => new { i.PartnerId, i.Total, i.PaidAmount }).ToListAsync())
                .GroupBy(i => i.PartnerId).ToDictionary(g => g.Key, g => g.Sum(i => i.Total - i.PaidAmount));
            if (debt.Count > 0)
                foreach (var p in await _db.Partners.Where(p => p.IsCustomer && p.CreditLimit > 0).ToListAsync())
                    if (debt.TryGetValue(p.Id, out var d) && d > p.CreditLimit)
                        list.Add(new AlertItem("credit", "danger", "Vượt hạn mức công nợ", $"{p.Code} {p.Name} · nợ {d.ToMoney()} / hạn mức {p.CreditLimit.ToMoney()} đ", "Partners", "Details", new { id = p.Id }));
        }
        if (on.ContainsKey("crm-tasks"))
        {
            var now2 = now ?? DateTime.Now;
            foreach (var t in await _db.CrmActivities.Include(t => t.Partner).Include(t => t.Lead).Where(t => t.Kind == ActivityKind.Task && !t.Done && t.Date < now2).OrderBy(t => t.Date).Take(30).ToListAsync())
                list.Add(new AlertItem("crm-tasks", "warning", $"Công việc CRM quá hạn {(today - t.Date.Date).Days} ngày", $"{t.Subject} · {t.Partner?.Name ?? t.Lead?.Name}", "Crm", "Activities", new { openTasks = true }));
        }
        if (on.TryGetValue("quotes", out var qd))
        {
            var lim = today.AddDays((double)qd);
            foreach (var q in await _db.SalesQuotes.Include(q => q.Customer).Where(q => (q.Status == QuoteStatus.Sent || q.Status == QuoteStatus.Approved) && q.ValidUntil >= today && q.ValidUntil <= lim).Take(20).ToListAsync())
                list.Add(new AlertItem("quotes", "info", $"Báo giá hết hiệu lực {q.ValidUntil:dd/MM}", $"{q.Number} · {q.Customer?.Name} · {q.Total.ToMoney()} đ", "Quotes", "Details", new { id = q.Id }));
        }
        return list.OrderBy(a => a.Level == "danger" ? 0 : a.Level == "warning" ? 1 : 2).ToList();
    }

    public static string DigestHtml(IEnumerable<AlertItem> items, string baseUrl)
    {
        var sb = new StringBuilder("<h3>Tổng hợp cảnh báo ERP</h3><table border='1' cellpadding='4' cellspacing='0' style='border-collapse:collapse;font-family:Segoe UI,Arial;font-size:13px'><tr><th>Mức</th><th>Cảnh báo</th><th>Chi tiết</th></tr>");
        foreach (var a in items) sb.Append($"<tr><td>{(a.Level == "danger" ? "Khẩn" : a.Level == "warning" ? "Lưu ý" : "Thông tin")}</td><td>{WebUtility.HtmlEncode(a.Title)}</td><td>{WebUtility.HtmlEncode(a.Detail)}</td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }
}

/// <summary>Gửi email qua SMTP (cấu hình ở Cài đặt hệ thống: Smtp.Host, Smtp.Port, Smtp.User, Smtp.Password, Smtp.From, Smtp.Ssl). Chưa cấu hình thì giữ trong hộp thư đi</summary>
public class MailService
{
    private readonly ErpDbContext _db;
    private readonly SettingsService _set;
    public MailService(ErpDbContext db, SettingsService set) { _db = db; _set = set; }

    public async Task<EmailOutbox> QueueAsync(string to, string subject, string body, string? attName = null, byte[]? attData = null, string? source = null, bool sendNow = true)
    {
        var m = new EmailOutbox { ToAddresses = to.Length > 500 ? to[..500] : to, Subject = subject.Length > 300 ? subject[..300] : subject, Body = body, AttachmentName = attName, AttachmentData = attData, Source = source };
        _db.EmailOutbox.Add(m);
        await _db.SaveChangesAsync();
        if (sendNow) await SendAsync(m);
        return m;
    }

    public async Task<bool> ConfiguredAsync() => !string.IsNullOrWhiteSpace(await _set.GetAsync("Smtp.Host"));

    public async Task SendAsync(EmailOutbox m)
    {
        var host = await _set.GetAsync("Smtp.Host");
        if (string.IsNullOrWhiteSpace(host)) { m.Status = MailStatus.NotConfigured; m.Error = "Chưa cấu hình máy chủ SMTP (Cài đặt hệ thống → Smtp.Host)."; await _db.SaveChangesAsync(); return; }
        try
        {
            var port = int.TryParse(await _set.GetAsync("Smtp.Port", "587"), out var p) ? p : 587;
            var user = await _set.GetAsync("Smtp.User"); var pass = await _set.GetAsync("Smtp.Password");
            var from = await _set.GetAsync("Smtp.From", user);
            using var client = new SmtpClient(host, port) { EnableSsl = await _set.BoolAsync("Smtp.Ssl", true), Timeout = 20000 };
            if (!string.IsNullOrEmpty(user)) client.Credentials = new NetworkCredential(user, pass);
            using var msg = new MailMessage { From = new MailAddress(string.IsNullOrWhiteSpace(from) ? "erp@localhost" : from), Subject = m.Subject, Body = m.Body, IsBodyHtml = true, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
            foreach (var a in m.ToAddresses.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) msg.To.Add(a);
            if (m.AttachmentData != null) msg.Attachments.Add(new Attachment(new MemoryStream(m.AttachmentData), m.AttachmentName ?? "report.xlsx"));
            await client.SendMailAsync(msg);
            m.Status = MailStatus.Sent; m.SentAt = DateTime.Now; m.Error = null;
        }
        catch (Exception ex) { m.Status = MailStatus.Failed; m.Error = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message; }
        await _db.SaveChangesAsync();
    }
}

/// <summary>Chạy báo cáo đã lưu theo lịch, gửi kết quả qua email</summary>
public class ReportScheduleService
{
    private readonly ErpDbContext _db;
    private readonly MailService _mail;
    public ReportScheduleService(ErpDbContext db, MailService mail) { _db = db; _mail = mail; }

    public static async Task<(byte[] Data, string FileName, string ContentType, ReportResult Result)> RenderAsync(ErpDbContext db, SavedReport r, string format)
    {
        var ds = DatasetRegistry.Find(r.DatasetKey) ?? throw new InvalidOperationException("Nguồn dữ liệu không còn tồn tại.");
        var def = ReportDef.FromJson(r.DefinitionJson) ?? throw new InvalidOperationException("Định nghĩa báo cáo không hợp lệ.");
        def = ReportEngine.Sanitize(def, ds);
        var result = await ReportEngine.RunAsync(ds, def, db);
        var (cols, rows, totals) = ReportExport.From(result);
        var file = new string(r.Name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim().Replace(' ', '_') + "_" + DateTime.Today.ToString("yyyyMMdd");
        var sub = new[] { $"Nguồn dữ liệu: {ds.Title}", $"Xuất ngày {DateTime.Now:dd/MM/yyyy HH:mm}" };
        if (format == "csv") return (ReportExport.Csv(cols, rows, totals), file + ".csv", "text/csv", result);
        return (ReportExport.Xlsx(r.Name, r.Name, sub, cols, rows, totals), file + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result);
    }

    public async Task RunAsync(ReportSchedule s, bool manual = false)
    {
        var rep = s.SavedReport ?? await _db.SavedReports.FindAsync(s.SavedReportId);
        try
        {
            if (rep == null) throw new InvalidOperationException("Báo cáo đã bị xóa.");
            var (data, name, _, res) = await RenderAsync(_db, rep, s.Format);
            var body = $"<p>Báo cáo <b>{WebUtility.HtmlEncode(rep.Name)}</b> – {res.Rows.Count} dòng, xuất lúc {DateTime.Now:dd/MM/yyyy HH:mm}.</p><p>Tệp đính kèm: {name}</p>";
            var m = await _mail.QueueAsync(s.Recipients, $"[ERP] Báo cáo: {rep.Name} ({DateTime.Today:dd/MM/yyyy})", body, name, data, "Lịch báo cáo");
            s.LastResult = m.Status == MailStatus.Sent ? "Đã gửi" : m.Status == MailStatus.NotConfigured ? "Đã tạo email – chưa cấu hình SMTP" : "Lỗi gửi: " + m.Error;
        }
        catch (Exception ex) { s.LastResult = "Lỗi: " + (ex.Message.Length > 250 ? ex.Message[..250] : ex.Message); }
        s.LastRunAt = DateTime.Now;
        if (!manual || s.NextRunAt <= DateTime.Now) s.NextRunAt = s.NextAfter(DateTime.Now);
        await _db.SaveChangesAsync();
    }
}

/// <summary>Bộ chạy nền: gửi báo cáo theo lịch (mỗi phút kiểm tra) và email tổng hợp cảnh báo mỗi ngày</summary>
public class BiBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _sf;
    private readonly ILogger<BiBackgroundService> _log;
    public BiBackgroundService(IServiceScopeFactory sf, ILogger<BiBackgroundService> log) { _sf = sf; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "BI background tick failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
        var now = DateTime.Now;
        var due = await db.ReportSchedules.Include(s => s.SavedReport).Where(s => s.IsActive && s.NextRunAt <= now).ToListAsync(ct);
        var svc = scope.ServiceProvider.GetRequiredService<ReportScheduleService>();
        foreach (var s in due) await svc.RunAsync(s);

        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        if (now.Hour >= 7)
        {
            var rules = await db.AlertRules.Where(r => r.IsEnabled && r.EmailTo != null && r.EmailTo != "" && (r.LastEmailedOn == null || r.LastEmailedOn < now.Date)).ToListAsync(ct);
            if (rules.Count > 0)
            {
                var items = await alerts.ComputeAsync(now);
                var mail = scope.ServiceProvider.GetRequiredService<MailService>();
                foreach (var g in rules.GroupBy(r => r.EmailTo!.Trim().ToLowerInvariant()))
                {
                    var keys = g.Select(r => r.Key).ToHashSet();
                    var mine = items.Where(i => keys.Contains(i.RuleKey)).ToList();
                    foreach (var r in g) r.LastEmailedOn = now.Date;
                    if (mine.Count > 0) await mail.QueueAsync(g.First().EmailTo!, $"[ERP] {mine.Count} cảnh báo cần xử lý ({now:dd/MM/yyyy})", AlertService.DigestHtml(mine, ""), source: "Cảnh báo");
                }
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
