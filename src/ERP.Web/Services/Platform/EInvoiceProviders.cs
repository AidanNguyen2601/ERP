using System.Globalization;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Thông số kết nối nhà cung cấp hóa đơn điện tử (đọc từ cài đặt EInvoice.*)</summary>
public record EInvoiceConfig(string Url, string User, string Password, string ServiceAccount, string ServicePassword, string Template, string SupplierTaxCode, HttpClient Http);

/// <summary>
/// Viettel S-Invoice – API REST JSON (InvoiceAPI/InvoiceWS). Xác thực Basic (tài khoản tích hợp).
/// Cấu trúc dữ liệu theo tài liệu tích hợp công khai của Viettel; cần chạy thử với tài khoản môi trường thử nghiệm (sandbox) trước khi dùng thật.
/// </summary>
public class ViettelSInvoiceProvider : IEInvoiceProvider
{
    private readonly EInvoiceConfig _c;
    public ViettelSInvoiceProvider(EInvoiceConfig c) => _c = c;
    public string Name => "Viettel S-Invoice";
    private string Api(string path) => _c.Url.TrimEnd('/') + "/services/einvoiceapplication/api/InvoiceAPI/InvoiceWS/" + path;

    private static decimal R(decimal v) => Math.Round(v, 0);

    public JsonObject Payload(Invoice inv, string adjustmentType, Invoice? original = null, SalesCreditNote? note = null)
    {
        var general = new JsonObject
        {
            ["invoiceType"] = "1", ["templateCode"] = _c.Template, ["invoiceSeries"] = inv.Series, ["currencyCode"] = inv.CurrencyCode, ["exchangeRate"] = inv.ExchangeRate,
            ["adjustmentType"] = adjustmentType, ["paymentStatus"] = true, ["cusGetInvoiceRight"] = true, ["transactionUuid"] = $"{inv.Number}-{(note?.Number ?? "")}-{DateTime.Now:yyyyMMddHHmmss}",
            ["invoiceIssuedDate"] = new DateTimeOffset(note?.Date ?? inv.Date).ToUnixTimeMilliseconds()
        };
        if (original != null)
        {
            general["originalInvoiceId"] = original.Series + original.InvoiceNo;
            general["originalInvoiceIssueDate"] = new DateTimeOffset(original.Date).ToUnixTimeMilliseconds();
            general["originalTemplateCode"] = _c.Template;
            general["additionalReferenceDesc"] = note?.Reason ?? $"Thay thế hóa đơn {original.Series} {original.InvoiceNo}";
            general["additionalReferenceDate"] = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
            if (note != null) { general["adjustmentInvoiceType"] = "1"; }
        }
        var items = new JsonArray();
        var taxes = new Dictionary<decimal, (decimal Base, decimal Tax)>();
        var n = 0;
        if (note != null)
            foreach (var l in note.Lines)
            {
                var tax = R(l.Amount * l.VatRate / 100);
                items.Add(new JsonObject { ["lineNumber"] = ++n, ["itemName"] = l.Description, ["unitPrice"] = l.UnitPrice, ["quantity"] = l.Quantity, ["itemTotalAmountWithoutTax"] = l.Amount,
                    ["taxPercentage"] = l.VatRate, ["taxAmount"] = tax, ["isIncreaseItem"] = false });
                var t = taxes.GetValueOrDefault(l.VatRate); taxes[l.VatRate] = (t.Base + l.Amount, t.Tax + tax);
            }
        else
            foreach (var l in inv.Lines)
            {
                var amount = inv.IsForeign ? l.FcAmount : l.Amount;
                var tax = R(amount * l.VatRate / 100);
                items.Add(new JsonObject { ["lineNumber"] = ++n, ["itemCode"] = l.Item?.Code, ["itemName"] = l.Description, ["unitName"] = l.Unit, ["unitPrice"] = inv.IsForeign ? l.FcUnitPrice : l.UnitPrice,
                    ["quantity"] = l.Quantity, ["itemTotalAmountWithoutTax"] = amount, ["taxPercentage"] = l.VatRate, ["taxAmount"] = tax, ["itemTotalAmountWithTax"] = amount + tax });
                var t = taxes.GetValueOrDefault(l.VatRate); taxes[l.VatRate] = (t.Base + amount, t.Tax + tax);
            }
        var sub = note?.Subtotal ?? (inv.IsForeign ? inv.FcSubtotal : inv.Subtotal);
        var vat = note?.VatAmount ?? (inv.IsForeign ? inv.FcVatAmount : inv.VatAmount);
        return new JsonObject
        {
            ["generalInvoiceInfo"] = general,
            ["buyerInfo"] = new JsonObject { ["buyerName"] = inv.Partner?.ContactPerson ?? inv.Partner?.Name, ["buyerLegalName"] = inv.Partner?.Name, ["buyerTaxCode"] = inv.Partner?.TaxCode,
                ["buyerAddressLine"] = inv.Partner?.Address, ["buyerCode"] = inv.Partner?.Code, ["buyerEmail"] = inv.Partner?.Email },
            ["payments"] = new JsonArray(new JsonObject { ["paymentMethodName"] = "TM/CK" }),
            ["itemInfo"] = items,
            ["taxBreakdowns"] = new JsonArray(taxes.Select(t => (JsonNode)new JsonObject { ["taxPercentage"] = t.Key, ["taxableAmount"] = t.Value.Base, ["taxAmount"] = t.Value.Tax }).ToArray()),
            ["summarizeInfo"] = new JsonObject { ["totalAmountWithoutTax"] = sub, ["totalTaxAmount"] = vat, ["totalAmountWithTax"] = sub + vat, ["totalAmountWithTaxInWords"] = VietnameseNumber.ToWords(sub + vat) }
        };
    }

    private async Task<EInvoiceResult> SendAsync(HttpRequestMessage req)
    {
        try
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_c.User}:{_c.Password}")));
            var res = await _c.Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return new EInvoiceResult(false, null, null, $"Viettel trả lỗi {(int)res.StatusCode}: {Cut(text)}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var err = root.TryGetProperty("errorCode", out var e) && e.ValueKind != JsonValueKind.Null ? e.ToString() : null;
            var desc = root.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null ? d.ToString() : null;
            if (!string.IsNullOrEmpty(err)) return new EInvoiceResult(false, null, null, $"Viettel: {err} – {desc}");
            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
                return new EInvoiceResult(true, S("reservationCode") ?? S("transactionID"), S("codeOfTax") ?? S("taxAuthorityCode"), $"Số HĐ {S("invoiceNo")}" + (desc != null ? " – " + desc : ""));
            }
            return new EInvoiceResult(true, null, null, desc ?? "Đã gửi.");
        }
        catch (Exception ex) { return new EInvoiceResult(false, null, null, "Không kết nối được Viettel S-Invoice: " + ex.Message); }
    }

    private HttpRequestMessage Json(string path, JsonObject body) => new(HttpMethod.Post, Api(path)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public Task<EInvoiceResult> IssueAsync(Invoice inv, string xml) =>
        SendAsync(Json($"createInvoice/{_c.SupplierTaxCode}", inv.ReplacesInvoice != null ? Payload(inv, "3", inv.ReplacesInvoice) : Payload(inv, "1")));

    public Task<EInvoiceResult> AdjustAsync(Invoice original, SalesCreditNote note, string xml) => SendAsync(Json($"createInvoice/{_c.SupplierTaxCode}", Payload(original, "5", original, note)));

    public Task<EInvoiceResult> CancelAsync(Invoice inv, string reason) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, Api("cancelTransactionInvoice"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["supplierTaxCode"] = _c.SupplierTaxCode, ["invoiceNo"] = inv.Series + inv.InvoiceNo, ["strIssueDate"] = inv.Date.ToString("yyyyMMddHHmmss"),
                ["additionalReferenceDesc"] = reason, ["additionalReferenceDate"] = DateTime.Today.ToString("yyyyMMddHHmmss")
            })
        });

    private static string Cut(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// VNPT Invoice – dịch vụ SOAP (PublishService.asmx / BusinessService.asmx): ImportAndPublishInv, ReplaceInvoiceAction, AdjustInvoiceAction, cancelInv.
/// Account / ACpass là tài khoản dịch vụ; username / password là tài khoản người phát hành. Kết quả dạng "OK:..." hoặc "ERR:mã".
/// Cấu trúc theo tài liệu tích hợp công khai của VNPT; cần chạy thử với đường dẫn demo của doanh nghiệp trước khi dùng thật.
/// </summary>
public class VnptInvoiceProvider : IEInvoiceProvider
{
    private readonly EInvoiceConfig _c;
    public VnptInvoiceProvider(EInvoiceConfig c) => _c = c;
    public string Name => "VNPT Invoice";
    public static string Key(Invoice inv) => "ERP" + inv.Id.ToString("D8");
    private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string InvXml(Invoice inv, SalesCreditNote? note = null, int? adjustType = null)
    {
        IEnumerable<XElement> products = note != null
            ? note.Lines.Select(l => new XElement("Product", new XElement("ProdName", l.Description), new XElement("ProdQuantity", N(l.Quantity)), new XElement("ProdPrice", N(l.UnitPrice)),
                new XElement("Amount", N(l.Amount)), new XElement("VATRate", N(l.VatRate)), new XElement("VATAmount", N(Math.Round(l.Amount * l.VatRate / 100))), new XElement("Total", N(l.Amount + Math.Round(l.Amount * l.VatRate / 100)))))
            : inv.Lines.Select(l => new XElement("Product", new XElement("Code", l.Item?.Code), new XElement("ProdName", l.Description), new XElement("ProdUnit", l.Unit), new XElement("ProdQuantity", N(l.Quantity)),
                new XElement("ProdPrice", N(inv.IsForeign ? l.FcUnitPrice : l.UnitPrice)), new XElement("Amount", N(inv.IsForeign ? l.FcAmount : l.Amount)), new XElement("VATRate", N(l.VatRate)),
                new XElement("VATAmount", N(Math.Round((inv.IsForeign ? l.FcAmount : l.Amount) * l.VatRate / 100))), new XElement("Total", N((inv.IsForeign ? l.FcAmount : l.Amount) * (1 + l.VatRate / 100)))));
        var sub = note?.Subtotal ?? (inv.IsForeign ? inv.FcSubtotal : inv.Subtotal);
        var vat = note?.VatAmount ?? (inv.IsForeign ? inv.FcVatAmount : inv.VatAmount);
        var invoice = new XElement("Invoice",
            new XElement("CusCode", inv.Partner?.Code), new XElement("CusName", inv.Partner?.Name), new XElement("Buyer", inv.Partner?.ContactPerson), new XElement("CusAddress", inv.Partner?.Address),
            new XElement("CusTaxCode", inv.Partner?.TaxCode), new XElement("CusEmail", inv.Partner?.Email), new XElement("PaymentMethod", "TM/CK"), new XElement("CurrencyUnit", inv.CurrencyCode),
            new XElement("ExchangeRate", N(inv.ExchangeRate)), new XElement("ArisingDate", (note?.Date ?? inv.Date).ToString("dd/MM/yyyy")),
            new XElement("Products", products), new XElement("Total", N(sub)), new XElement("VATAmount", N(vat)), new XElement("Amount", N(sub + vat)), new XElement("AmountInWords", VietnameseNumber.ToWords(sub + vat)));
        if (adjustType != null) invoice.Add(new XElement("Type", adjustType));
        var root = adjustType != null || note != null ? new XElement("AdjustInv", new XElement("key", Key(inv) + (note != null ? "-" + note.Id : "")), invoice.Elements())
            : new XElement("Invoices", new XElement("Inv", new XElement("key", Key(inv)), invoice));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private async Task<EInvoiceResult> SoapAsync(string service, string action, params (string Name, string Value)[] args)
    {
        try
        {
            var body = string.Concat(args.Select(a => $"<{a.Name}>{SecurityElement.Escape(a.Value)}</{a.Name}>"));
            var env = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><{action} xmlns=\"http://tempuri.org/\">{body}</{action}></soap:Body></soap:Envelope>";
            using var req = new HttpRequestMessage(HttpMethod.Post, _c.Url.TrimEnd('/') + "/" + service) { Content = new StringContent(env, Encoding.UTF8, "text/xml") };
            req.Headers.Add("SOAPAction", $"\"http://tempuri.org/{action}\"");
            var res = await _c.Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return new EInvoiceResult(false, null, null, $"VNPT trả lỗi {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
            var result = XDocument.Parse(text).Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Result")?.Value ?? "";
            if (result.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                // OK:pattern;serial-key_số  → lấy số hóa đơn và key làm mã tra cứu
                var tail = result.Contains(';') ? result[(result.IndexOf(';') + 1)..] : result[3..];
                return new EInvoiceResult(true, tail.Contains('-') ? tail : tail, null, "VNPT: " + result);
            }
            return new EInvoiceResult(false, null, null, "VNPT: " + (string.IsNullOrEmpty(result) ? "không đọc được kết quả" : result + " " + VnptError(result)));
        }
        catch (Exception ex) { return new EInvoiceResult(false, null, null, "Không kết nối được VNPT Invoice: " + ex.Message); }
    }

    public static string VnptError(string r) => r switch
    {
        "ERR:1" => "(tài khoản đăng nhập sai hoặc không có quyền)", "ERR:3" => "(dữ liệu XML không đúng quy định)", "ERR:5" => "(không phát hành được hóa đơn)",
        "ERR:6" => "(dải hóa đơn hết số)", "ERR:7" => "(tên đăng nhập không đúng)", "ERR:10" => "(lô vượt số lượng cho phép)", "ERR:20" => "(mẫu số, ký hiệu không hợp lệ)",
        "ERR:2" => "(hóa đơn cần thao tác không tồn tại)", "ERR:9" => "(trạng thái hóa đơn không cho phép thao tác)", _ => ""
    };

    private (string, string)[] Auth() => new[] { ("Account", _c.ServiceAccount), ("ACpass", _c.ServicePassword) };
    private (string, string)[] User() => new[] { ("username", _c.User), ("password", _c.Password) };

    public Task<EInvoiceResult> IssueAsync(Invoice inv, string xml)
    {
        if (inv.ReplacesInvoice != null)
            return SoapAsync("BusinessService.asmx", "ReplaceInvoiceAction", Auth().Concat(new[] { ("xmlInvData", InvXml(inv).Replace("<Invoices><Inv>", "<ReplaceInv>").Replace("</Inv></Invoices>", "</ReplaceInv>")) })
                .Concat(User()).Concat(new[] { ("fkey", Key(inv.ReplacesInvoice)), ("AttachFile", ""), ("convert", "0"), ("pattern", _c.Template), ("serial", inv.Series ?? "") }).ToArray());
        return SoapAsync("PublishService.asmx", "ImportAndPublishInv", Auth().Concat(new[] { ("xmlInvData", InvXml(inv)) }).Concat(User())
            .Concat(new[] { ("pattern", _c.Template), ("serial", inv.Series ?? ""), ("convert", "0") }).ToArray());
    }

    public Task<EInvoiceResult> AdjustAsync(Invoice original, SalesCreditNote note, string xml) =>
        SoapAsync("BusinessService.asmx", "AdjustInvoiceAction", Auth().Concat(new[] { ("xmlInvData", InvXml(original, note, 3)) }).Concat(User())
            .Concat(new[] { ("fkey", Key(original)), ("AttachFile", ""), ("convert", "0"), ("pattern", _c.Template), ("serial", original.Series ?? "") }).ToArray());

    public Task<EInvoiceResult> CancelAsync(Invoice inv, string reason) =>
        SoapAsync("BusinessService.asmx", "cancelInv", Auth().Concat(new[] { ("fkey", Key(inv)), ("userName", _c.User), ("userPass", _c.Password) }).ToArray());
}
