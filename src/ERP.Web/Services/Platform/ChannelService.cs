using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Models.Entities
{
    public partial class AppUser
    {
        /// <summary>ID người dùng của Zalo OA (người dùng đã quan tâm Official Account) để nhận thông báo qua Zalo</summary>
        [System.ComponentModel.DataAnnotations.StringLength(60)] public string? ZaloUserId { get; set; }
    }
}

namespace ERP.Web.Services
{
    /// <summary>
    /// Kênh gửi thông báo ngoài ứng dụng:
    ///  • Microsoft Teams: Incoming Webhook (cài đặt Notify.TeamsWebhook) – gửi vào một kênh chung;
    ///  • Zalo: API tin nhắn tư vấn của Zalo Official Account (Notify.ZaloToken = access token OA) – gửi tới từng người có ZaloUserId.
    /// Mỗi kênh có mức tối thiểu (info / warning / danger). Mọi lần gửi được ghi vào hộp thư đi (Channel = teams / zalo) để xem lại và gửi lại.
    /// </summary>
    public class ChannelService
    {
        public const string DefaultZaloApi = "https://openapi.zalo.me/v3.0/oa/message/cs";
        private readonly ErpDbContext _db;
        private readonly SettingsService _set;
        private readonly IHttpClientFactory _http;
        public ChannelService(ErpDbContext db, SettingsService set, IHttpClientFactory http) { _db = db; _set = set; _http = http; }

        public static int Rank(string level) => level switch { "danger" => 3, "warning" => 2, "success" => 1, _ => 1 };

        private async Task<bool> PassesAsync(string key, string level) => Rank(level) >= Rank(await _set.GetAsync(key, "warning"));

        /// <summary>Gửi thông báo ra các kênh đã cấu hình (lỗi không làm hỏng nghiệp vụ)</summary>
        public async Task DispatchAsync(IReadOnlyCollection<AppUser> users, string title, string? body, string? url, string level, string? source)
        {
            var link = await AbsoluteAsync(url);
            var webhook = (await _set.GetAsync("Notify.TeamsWebhook")).Trim();
            if (webhook.Length > 0 && await PassesAsync("Notify.TeamsMinLevel", level))
                await LogAndSendAsync(new EmailOutbox { Channel = "teams", ToAddresses = "Teams", Subject = Cut(title, 300), Body = TeamsText(title, body, link), Source = source });
            var token = (await _set.GetAsync("Notify.ZaloToken")).Trim();
            if (token.Length > 0 && await PassesAsync("Notify.ZaloMinLevel", level))
                foreach (var u in users.Where(u => !string.IsNullOrWhiteSpace(u.ZaloUserId)))
                    await LogAndSendAsync(new EmailOutbox { Channel = "zalo", ToAddresses = u.ZaloUserId!, Subject = Cut(title, 300), Body = ZaloText(title, body, link), Source = source });
        }

        public async Task LogAndSendAsync(EmailOutbox m)
        {
            _db.EmailOutbox.Add(m);
            await _db.SaveChangesAsync();
            await SendAsync(m);
        }

        /// <summary>Gửi (hoặc gửi lại) một bản ghi kênh Teams / Zalo</summary>
        public async Task SendAsync(EmailOutbox m)
        {
            try
            {
                var client = _http.CreateClient("channels");
                client.Timeout = TimeSpan.FromSeconds(10);
                if (m.Channel == "teams")
                {
                    var url = (await _set.GetAsync("Notify.TeamsWebhook")).Trim();
                    if (url.Length == 0) { m.Status = MailStatus.NotConfigured; m.Error = "Chưa cấu hình Teams webhook."; }
                    else
                    {
                        var r = await client.PostAsJsonAsync(url, new { text = m.Body });
                        if (r.IsSuccessStatusCode) { m.Status = MailStatus.Sent; m.SentAt = DateTime.Now; m.Error = null; }
                        else { m.Status = MailStatus.Failed; m.Error = $"Teams trả về {(int)r.StatusCode}: {Cut(await r.Content.ReadAsStringAsync(), 300)}"; }
                    }
                }
                else if (m.Channel == "zalo")
                {
                    var token = (await _set.GetAsync("Notify.ZaloToken")).Trim();
                    if (token.Length == 0) { m.Status = MailStatus.NotConfigured; m.Error = "Chưa cấu hình access token Zalo OA."; }
                    else
                    {
                        var api = (await _set.GetAsync("Notify.ZaloApiUrl", DefaultZaloApi)).Trim();
                        using var req = new HttpRequestMessage(HttpMethod.Post, api) { Content = JsonContent.Create(new { recipient = new { user_id = m.ToAddresses }, message = new { text = m.Body } }) };
                        req.Headers.Add("access_token", token);
                        var r = await client.SendAsync(req);
                        var text = await r.Content.ReadAsStringAsync();
                        var code = -1; string? msg = null;
                        try { using var doc = JsonDocument.Parse(text); code = doc.RootElement.TryGetProperty("error", out var e) ? e.GetInt32() : -1; msg = doc.RootElement.TryGetProperty("message", out var mm) ? mm.GetString() : null; } catch { }
                        if (r.IsSuccessStatusCode && code == 0) { m.Status = MailStatus.Sent; m.SentAt = DateTime.Now; m.Error = null; }
                        else { m.Status = MailStatus.Failed; m.Error = $"Zalo trả về {(int)r.StatusCode} / mã {code}: {Cut(msg ?? text, 300)}"; }
                    }
                }
            }
            catch (Exception ex) { m.Status = MailStatus.Failed; m.Error = Cut(ex.Message, 480); }
            await _db.SaveChangesAsync();
        }

        private async Task<string?> AbsoluteAsync(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var baseUrl = (await _set.GetAsync("App.BaseUrl")).Trim().TrimEnd('/');
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || baseUrl.Length == 0 ? (baseUrl.Length == 0 ? null : url) : baseUrl + url;
        }

        public static string TeamsText(string title, string? body, string? link) =>
            $"**{title}**" + (string.IsNullOrEmpty(body) ? "" : $"\n\n{body}") + (link == null ? "" : $"\n\n[Mở trong ERP]({link})");

        public static string ZaloText(string title, string? body, string? link) =>
            Cut("[ERP] " + title + (string.IsNullOrEmpty(body) ? "" : "\n" + body) + (link == null ? "" : "\n" + link), 2000);

        private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
    }
}
