using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

public record EInvoiceResult(bool Ok, string? LookupCode, string? TaxCode, string Message);

/// <summary>Bộ chuyển đổi nhà cung cấp dịch vụ hóa đơn điện tử (VNPT, Viettel, MISA, BKAV...). Mỗi nhà cung cấp cài một lớp theo giao diện này.</summary>
public interface IEInvoiceProvider
{
    string Name { get; }
    Task<EInvoiceResult> IssueAsync(Invoice inv, string xml);
    /// <summary>Thông báo hóa đơn có sai sót / hủy (mẫu 04/SS-HĐĐT)</summary>
    Task<EInvoiceResult> CancelAsync(Invoice inv, string reason);
    /// <summary>Hóa đơn điều chỉnh giảm cho hóa đơn gốc</summary>
    Task<EInvoiceResult> AdjustAsync(Invoice original, SalesCreditNote note, string xml);
}

/// <summary>Chế độ thử nghiệm: kiểm tra dữ liệu bắt buộc như nhà cung cấp thật, cấp mã tra cứu và mã CQT giả lập (không gửi đi đâu)</summary>
public class DemoEInvoiceProvider : IEInvoiceProvider
{
    public string Name => "Demo (giả lập)";
    private static string Code(string seed, int len) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + DateTime.Now.Ticks)))[..len];

    public Task<EInvoiceResult> IssueAsync(Invoice inv, string xml)
    {
        if (inv.Partner == null) return Task.FromResult(new EInvoiceResult(false, null, null, "Thiếu thông tin người mua."));
        if (inv.Lines.Count == 0 || inv.Total <= 0) return Task.FromResult(new EInvoiceResult(false, null, null, "Hóa đơn không có dòng hàng hóa / tổng tiền."));
        if (!string.IsNullOrEmpty(inv.Partner.TaxCode) && !System.Text.RegularExpressions.Regex.IsMatch(inv.Partner.TaxCode, @"^\d{10}(-\d{3})?$|^\d{12}$"))
            return Task.FromResult(new EInvoiceResult(false, null, null, $"Mã số thuế người mua \"{inv.Partner.TaxCode}\" không đúng định dạng."));
        return Task.FromResult(new EInvoiceResult(true, Code(inv.Number, 12), "M1-" + inv.Date.ToString("yy") + "-" + Code(inv.InvoiceNo ?? inv.Number, 5) + "-" + Code("x", 11), "Đã cấp mã (giả lập)."));
    }
    public Task<EInvoiceResult> CancelAsync(Invoice inv, string reason) => Task.FromResult(new EInvoiceResult(true, inv.EInvoiceLookupCode, inv.EInvoiceTaxCode, "Đã gửi thông báo sai sót 04/SS-HĐĐT (giả lập)."));
    public Task<EInvoiceResult> AdjustAsync(Invoice original, SalesCreditNote note, string xml) =>
        Task.FromResult(new EInvoiceResult(true, Code(note.Number, 12), "M1-" + note.Date.ToString("yy") + "-" + Code(note.Number, 5) + "-" + Code("y", 11), "Đã cấp mã hóa đơn điều chỉnh (giả lập)."));
}

/// <summary>
/// Kết nối qua HTTP tới API của nhà cung cấp: gửi XML (application/xml) kèm khóa API, đọc JSON {ok, lookupCode, taxCode, message}.
/// Dùng làm mẫu: mỗi nhà cung cấp có đặc tả riêng nên cần điều chỉnh đường dẫn / định dạng theo tài liệu tích hợp của họ.
/// </summary>
public class HttpEInvoiceProvider : IEInvoiceProvider
{
    private readonly string _url, _user, _key;
    public HttpEInvoiceProvider(string url, string user, string key) { _url = url.TrimEnd('/'); _user = user; _key = key; }
    public string Name => "Kết nối HTTP";

    private async Task<EInvoiceResult> PostAsync(string path, string body, string contentType)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_key}")));
            var res = await http.PostAsync(_url + path, new StringContent(body, Encoding.UTF8, contentType));
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return new EInvoiceResult(false, null, null, $"Nhà cung cấp trả lỗi {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) ? v.ToString() : null;
            return new EInvoiceResult(S("ok") is "true" or "True", S("lookupCode"), S("taxCode"), S("message") ?? "");
        }
        catch (Exception ex) { return new EInvoiceResult(false, null, null, "Không kết nối được nhà cung cấp hóa đơn điện tử: " + ex.Message); }
    }
    public Task<EInvoiceResult> IssueAsync(Invoice inv, string xml) => PostAsync("/invoices", xml, "application/xml");
    public Task<EInvoiceResult> CancelAsync(Invoice inv, string reason) =>
        PostAsync("/invoices/cancel", System.Text.Json.JsonSerializer.Serialize(new { inv.Series, inv.InvoiceNo, inv.EInvoiceLookupCode, reason }), "application/json");
    public Task<EInvoiceResult> AdjustAsync(Invoice original, SalesCreditNote note, string xml) => PostAsync("/invoices/adjust", xml, "application/xml");
}

/// <summary>Phát hành / hủy / điều chỉnh hóa đơn điện tử theo cài đặt (EInvoice.*)</summary>
public class EInvoiceService
{
    private readonly ErpDbContext _db;
    private readonly SettingsService _set;
    private readonly ErpSettings _cfg;
    public EInvoiceService(ErpDbContext db, SettingsService set, IOptions<ErpSettings> cfg) { _db = db; _set = set; _cfg = cfg.Value; }

    public Task<bool> EnabledAsync() => _set.BoolAsync("EInvoice.Enabled");

    private async Task<IEInvoiceProvider> ProviderAsync() =>
        await _set.GetAsync("EInvoice.Provider", "demo") == "http"
            ? new HttpEInvoiceProvider(await _set.GetAsync("EInvoice.Url"), await _set.GetAsync("EInvoice.Username"), await _set.GetAsync("EInvoice.ApiKey"))
            : new DemoEInvoiceProvider();

    private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>XML hóa đơn theo cấu trúc dữ liệu của Tổng cục Thuế (TT78/2021, QĐ 1450/QĐ-TCT): HDon/DLHDon/TTChung/NDHDon...</summary>
    public string BuildXml(Invoice inv, Invoice? original = null, SalesCreditNote? note = null)
    {
        var lines = note != null
            ? note.Lines.Select((l, i) => new XElement("HHDVu", new XElement("TChat", 1), new XElement("STT", i + 1), new XElement("THHDVu", l.Description), new XElement("SLuong", N(l.Quantity)),
                new XElement("DGia", N(l.UnitPrice)), new XElement("ThTien", N(-l.Amount)), new XElement("TSuat", l.VatRate + "%")))
            : inv.Lines.Select((l, i) => new XElement("HHDVu", new XElement("TChat", 1), new XElement("STT", i + 1), new XElement("MHHDVu", l.Item?.Code), new XElement("THHDVu", l.Description),
                new XElement("DVTinh", l.Unit), new XElement("SLuong", N(l.Quantity)), new XElement("DGia", N(inv.IsForeign ? l.FcUnitPrice : l.UnitPrice)),
                new XElement("ThTien", N(inv.IsForeign ? l.FcAmount : l.Amount)), new XElement("TSuat", l.VatRate + "%")));
        var ttChung = new XElement("TTChung", new XElement("PBan", "2.0.1"), new XElement("THDon", "Hóa đơn giá trị gia tăng"), new XElement("KHMSHDon", 1),
            new XElement("KHHDon", inv.Series), new XElement("SHDon", inv.InvoiceNo), new XElement("NLap", (note?.Date ?? inv.Date).ToString("yyyy-MM-dd")),
            new XElement("DVTTe", inv.CurrencyCode), new XElement("TGia", N(inv.ExchangeRate)), new XElement("HTTToan", "TM/CK"), new XElement("MSTTCGP", _cfg.CompanyTaxCode));
        if (note != null && original != null)
            ttChung.Add(new XElement("TTHDLQuan", new XElement("TCHDon", 2), new XElement("LHDCLQuan", 1), new XElement("KHMSHDCLQuan", 1), new XElement("KHHDCLQuan", original.Series),
                new XElement("SHDCLQuan", original.InvoiceNo), new XElement("NLHDCLQuan", original.Date.ToString("yyyy-MM-dd")), new XElement("GChu", note.Reason)));
        var sub = note != null ? -note.Subtotal : inv.IsForeign ? inv.FcSubtotal : inv.Subtotal;
        var vat = note != null ? -note.VatAmount : inv.IsForeign ? inv.FcVatAmount : inv.VatAmount;
        var doc = new XElement("HDon", new XElement("DLHDon", new XAttribute("Id", "data"), ttChung,
            new XElement("NDHDon",
                new XElement("NBan", new XElement("Ten", _cfg.CompanyName), new XElement("MST", _cfg.CompanyTaxCode), new XElement("DChi", _cfg.CompanyAddress)),
                new XElement("NMua", new XElement("Ten", inv.Partner?.Name), new XElement("MST", inv.Partner?.TaxCode), new XElement("DChi", inv.Partner?.Address), new XElement("MKHang", inv.Partner?.Code)),
                new XElement("DSHHDVu", lines),
                new XElement("TToan", new XElement("TgTCThue", N(sub)), new XElement("TgTThue", N(vat)), new XElement("TgTTTBSo", N(sub + vat)),
                    new XElement("TgTTTBChu", VietnameseNumber.ToWords(Math.Abs(note?.Total ?? inv.Total)))))),
            new XElement("DSCKS", new XElement("NBan", "(chữ ký số người bán do nhà cung cấp dịch vụ ký)")));
        return new XDeclaration("1.0", "UTF-8", null) + Environment.NewLine + doc;
    }

    /// <summary>Gửi phát hành; lỗi thì ném BusinessException để hủy cả nghiệp vụ phát hành (không có hóa đơn đã ghi sổ mà chưa có mã)</summary>
    public async Task IssueAsync(Invoice inv)
    {
        if (inv.Direction != InvoiceDirection.Sales || !await EnabledAsync()) return;
        if (inv.Partner == null) await _db.Entry(inv).Reference(i => i.Partner).LoadAsync();
        foreach (var l in inv.Lines.Where(l => l.ItemId != null && l.Item == null)) await _db.Entry(l).Reference(x => x.Item).LoadAsync();
        var xml = BuildXml(inv);
        var p = await ProviderAsync();
        var r = await p.IssueAsync(inv, xml);
        if (!r.Ok) throw new BusinessException("Phát hành hóa đơn điện tử thất bại – " + r.Message);
        inv.EInvoiceXml = xml; inv.EInvoiceLookupCode = r.LookupCode; inv.EInvoiceTaxCode = r.TaxCode; inv.EInvoiceStatus = EInvoiceStatus.Issued;
        inv.EInvoiceMessage = $"{p.Name}: {r.Message}"; inv.EInvoiceSentAt = DateTime.Now;
    }

    public async Task CancelAsync(Invoice inv, string reason)
    {
        if (inv.EInvoiceStatus != EInvoiceStatus.Issued) return;
        var p = await ProviderAsync();
        var r = await p.CancelAsync(inv, reason);
        if (!r.Ok) throw new BusinessException("Không gửi được thông báo sai sót hóa đơn điện tử – " + r.Message);
        inv.EInvoiceStatus = EInvoiceStatus.Cancelled; inv.EInvoiceMessage = $"{p.Name}: {r.Message}";
    }

    public async Task<string?> AdjustAsync(Invoice original, SalesCreditNote note)
    {
        if (original.EInvoiceStatus is not (EInvoiceStatus.Issued or EInvoiceStatus.Adjusted)) return null;
        if (original.Partner == null) await _db.Entry(original).Reference(i => i.Partner).LoadAsync();
        var xml = BuildXml(original, original, note);
        var p = await ProviderAsync();
        var r = await p.AdjustAsync(original, note, xml);
        if (!r.Ok) throw new BusinessException("Gửi hóa đơn điều chỉnh điện tử thất bại – " + r.Message);
        original.EInvoiceStatus = EInvoiceStatus.Adjusted;
        original.EInvoiceMessage = $"{p.Name}: điều chỉnh bởi {note.Number} – mã tra cứu {r.LookupCode}, mã CQT {r.TaxCode}";
        return r.LookupCode;
    }
}
