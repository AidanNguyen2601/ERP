using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Thông báo trong ứng dụng; nếu người nhận có email và hệ thống đã cấu hình SMTP + bật "Notify.Email" thì gửi thêm email.
/// Lỗi gửi thông báo không bao giờ làm hỏng nghiệp vụ chính.
/// </summary>
public class NotificationService
{
    private readonly ErpDbContext _db;
    private readonly MailService _mail;
    private readonly SettingsService _set;
    private readonly ChannelService _channels;
    public NotificationService(ErpDbContext db, MailService mail, SettingsService set, ChannelService channels) { _db = db; _mail = mail; _set = set; _channels = channels; }

    /// <summary>Gửi cho các vai trò (dùng cho bước duyệt theo vai trò) và/hoặc một người dùng cụ thể</summary>
    public async Task NotifyAsync(IEnumerable<string?> roles, int? userId, string title, string? body, string? url, string level = "info", string? source = null, int? exceptUserId = null)
    {
        try
        {
            var rs = roles.Where(r => !string.IsNullOrEmpty(r)).Cast<string>().Distinct().ToList();
            var users = await _db.Users.AsNoTracking().Where(u => u.IsActive && ((userId != null && u.Id == userId) || rs.Contains(u.Role))).ToListAsync();
            if (exceptUserId != null) users = users.Where(u => u.Id != exceptUserId).ToList();
            await SendAsync(users, title, body, url, level, source);
        }
        catch
        {
            foreach (var e in _db.ChangeTracker.Entries<Notification>().ToList()) e.State = EntityState.Detached;   // chỉ bỏ thông báo lỗi, không đụng tới dữ liệu nghiệp vụ đang theo dõi
        }
    }

    public async Task SendAsync(IEnumerable<AppUser> users, string title, string? body, string? url, string level = "info", string? source = null)
    {
        var list = users.ToList();
        if (list.Count == 0) return;
        foreach (var u in list)
            _db.Notifications.Add(new Notification { UserId = u.Id, Title = Cut(title, 200), Body = body == null ? null : Cut(body, 600), Url = url, Level = level, Source = source });
        await _db.SaveChangesAsync();
        if (await _set.BoolAsync("Notify.Email", false) && await _mail.ConfiguredAsync())
        {
            var to = string.Join(", ", list.Where(u => !string.IsNullOrWhiteSpace(u.Email)).Select(u => u.Email));
            if (to.Length > 0) await _mail.QueueAsync(to, "[ERP] " + title, $"<p><b>{System.Net.WebUtility.HtmlEncode(title)}</b></p><p>{System.Net.WebUtility.HtmlEncode(body ?? "")}</p>", source: source ?? "Thông báo");
        }
        try { await _channels.DispatchAsync(list, title, body, url, level, source); }
        catch { foreach (var e in _db.ChangeTracker.Entries<EmailOutbox>().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached; }
    }

    public Task<int> UnreadAsync(int userId) => _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}
