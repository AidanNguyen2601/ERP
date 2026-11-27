using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Tham số hệ thống: quy tắc đặt mã, hóa đơn điện tử, tham số lương, ngân sách, thuế TNDN, chế độ kế toán; tiền tệ & tỷ giá; khóa API kết nối ngoài</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class SettingsController : ErpControllerBase
{
    public SettingsController(ErpDbContext db) : base(db) { }

    public static readonly (string Group, string Key, string Label, string Kind, string? Hint)[] Fields =
    {
        ("Chế độ kế toán", "Accounting.Regime", "Chế độ kế toán áp dụng", "regime", "TT200 (doanh nghiệp lớn) hoặc TT133 (vừa và nhỏ). Có thể xem bảng cân đối theo chuẩn khác tại Báo cáo theo chuẩn mực."),
        ("Hóa đơn điện tử", "EInvoice.Enabled", "Phát hành hóa đơn điện tử khi phát hành hóa đơn bán", "bool", null),
        ("Hóa đơn điện tử", "EInvoice.Provider", "Nhà cung cấp", "provider", "demo = giả lập cấp mã; http = gọi API nhà cung cấp (VNPT, Viettel, MISA, BKAV... qua cổng tích hợp)"),
        ("Hóa đơn điện tử", "EInvoice.Url", "Địa chỉ API nhà cung cấp", "text", "VD: https://api.nha-cung-cap/einvoice"),
        ("Hóa đơn điện tử", "EInvoice.Username", "Tài khoản tích hợp", "text", null),
        ("Hóa đơn điện tử", "EInvoice.ApiKey", "Khóa API / mật khẩu tích hợp", "secret", "Để trống nếu không đổi"),
        ("Hóa đơn điện tử", "EInvoice.TemplateCode", "Ký hiệu mẫu số", "text", "1 = HĐ GTGT, 2 = HĐ bán hàng"),
        ("Bán hàng", "Quote.MinMarginPct", "Lợi nhuận tối thiểu của báo giá (%) – thấp hơn phải Ban Giám đốc duyệt", "number", null),
        ("Ngân sách", "Budget.Enabled", "Kiểm soát ngân sách khi ghi sổ chi phí", "bool", "Cảnh báo hoặc chặn theo cấu hình từng ngân sách"),
        ("Thuế TNDN", "Cit.Rate", "Thuế suất TNDN (%)", "number", null),
        ("Tiền lương", "Payroll.StandardDays", "Ngày công chuẩn / tháng", "number", null),
        ("Tiền lương", "Payroll.EmpBhxh", "BHXH người lao động (%)", "number", null),
        ("Tiền lương", "Payroll.EmpBhyt", "BHYT người lao động (%)", "number", null),
        ("Tiền lương", "Payroll.EmpBhtn", "BHTN người lao động (%)", "number", null),
        ("Tiền lương", "Payroll.ErBhxh", "BHXH doanh nghiệp (%)", "number", null),
        ("Tiền lương", "Payroll.ErBhyt", "BHYT doanh nghiệp (%)", "number", null),
        ("Tiền lương", "Payroll.ErBhtn", "BHTN doanh nghiệp (%)", "number", null),
        ("Tiền lương", "Payroll.ErKpcd", "KPCĐ doanh nghiệp (%)", "number", null),
        ("Tiền lương", "Payroll.InsuranceCap", "Mức lương tối đa đóng BHXH, BHYT", "number", "20 lần lương cơ sở"),
        ("Tiền lương", "Payroll.PersonalDeduction", "Giảm trừ bản thân / tháng", "number", null),
        ("Tiền lương", "Payroll.DependentDeduction", "Giảm trừ mỗi người phụ thuộc / tháng", "number", null),
        ("Tiền lương", "Payroll.PitBrackets", "Biểu thuế TNCN lũy tiến", "text", "Dạng mức_trên:thuế_suất; ... ; 0:thuế_suất_bậc_cuối"),
    };

    public static readonly (string Target, string Name)[] CodeTargets =
    {
        ("customer", "Khách hàng"), ("vendor", "Nhà cung cấp"), ("item.Material", "Nguyên vật liệu"), ("item.FinishedGood", "Thành phẩm"), ("item.Merchandise", "Hàng hóa"),
        ("item.Service", "Dịch vụ"), ("employee", "Nhân viên"), ("contract.Sale", "Hợp đồng bán"), ("contract.Purchase", "Hợp đồng mua"), ("tool", "Công cụ dụng cụ"),
    };

    public async Task<IActionResult> Index()
    {
        ViewBag.Values = await Db.SystemSettings.ToDictionaryAsync(s => s.Key, s => s.Value);
        ViewBag.Rules = await Db.CodeRules.ToDictionaryAsync(r => r.Target);
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant)]
    public async Task<IActionResult> Index(IFormCollection form)
    {
        var all = await Db.SystemSettings.ToDictionaryAsync(s => s.Key);
        foreach (var f in Fields)
        {
            string v = f.Kind == "bool" ? (form[f.Key].Contains("true") ? "true" : "false") : form[f.Key].ToString().Trim();
            if (f.Kind == "secret" && v == "") continue;
            if (f.Kind == "number" && !decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) { Error($"{f.Label}: giá trị không hợp lệ."); return RedirectToAction(nameof(Index)); }
            if (f.Key == "Payroll.PitBrackets" && !v.Split(';').All(p => p.Split(':') is { Length: 2 } x && decimal.TryParse(x[0], out _) && decimal.TryParse(x[1], out _)))
            { Error("Biểu thuế TNCN sai định dạng."); return RedirectToAction(nameof(Index)); }
            if (all.TryGetValue(f.Key, out var s)) { if (s.Value != v) { s.Value = v; s.UpdatedAt = DateTime.Now; } }
            else Db.SystemSettings.Add(new SystemSetting { Key = f.Key, Value = v });
        }
        var rules = await Db.CodeRules.ToDictionaryAsync(r => r.Target);
        foreach (var (t, _) in CodeTargets)
        {
            var prefix = form["prefix." + t].ToString().Trim().ToUpperInvariant();
            if (!int.TryParse(form["digits." + t], out var digits)) digits = 3;
            if (!rules.TryGetValue(t, out var r)) { if (prefix == "") continue; r = new CodeRule { Target = t }; Db.CodeRules.Add(r); }
            r.Prefix = prefix; r.Digits = Math.Clamp(digits, 2, 8); r.Enforce = form["enforce." + t].Contains("true");
        }
        await Db.SaveChangesAsync();
        Success("Đã lưu tham số hệ thống.");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ tiền tệ & tỷ giá
    public async Task<IActionResult> Currencies(DateTime? revalDate, [FromServices] FxService fx)
    {
        ViewBag.Rates = await Db.ExchangeRates.OrderByDescending(r => r.Date).ThenBy(r => r.CurrencyCode).Take(200).ToListAsync();
        var d = (revalDate ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1)).Date;
        ViewBag.RevalDate = d;
        ViewBag.Reval = revalDate == null ? null : await fx.PreviewAsync(d);
        ViewBag.Latest = new Dictionary<string, decimal>();
        foreach (var c in await Db.Currencies.Where(c => c.Code != "VND").Select(c => c.Code).ToListAsync()) ((Dictionary<string, decimal>)ViewBag.Latest)[c] = await fx.RateAsync(c, DateTime.Today);
        return View(await Db.Currencies.OrderBy(c => c.Code == "VND" ? 0 : 1).ThenBy(c => c.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCurrency(string code, string name, int decimals, bool isActive = false)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length != 3 || string.IsNullOrWhiteSpace(name)) { Error("Mã tiền tệ gồm 3 ký tự (ISO 4217) và phải có tên."); return RedirectToAction(nameof(Currencies)); }
        var c = await Db.Currencies.FirstOrDefaultAsync(x => x.Code == code);
        if (c == null) Db.Currencies.Add(c = new Currency { Code = code });
        c.Name = name.Trim(); c.Decimals = Math.Clamp(decimals, 0, 4); c.IsActive = isActive || code == "VND";
        await Db.SaveChangesAsync();
        Success($"Đã lưu loại tiền {code}.");
        return RedirectToAction(nameof(Currencies));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRate(string currencyCode, DateTime date, decimal rate)
    {
        if (rate <= 0 || currencyCode == "VND" || !await Db.Currencies.AnyAsync(c => c.Code == currencyCode)) { Error("Tỷ giá không hợp lệ."); return RedirectToAction(nameof(Currencies)); }
        var x = await Db.ExchangeRates.FirstOrDefaultAsync(r => r.CurrencyCode == currencyCode && r.Date == date.Date);
        if (x == null) Db.ExchangeRates.Add(new ExchangeRate { CurrencyCode = currencyCode, Date = date.Date, Rate = rate }); else x.Rate = rate;
        await Db.SaveChangesAsync();
        Success($"Đã lưu tỷ giá {currencyCode} ngày {date:dd/MM/yyyy}: {rate:N2}.");
        return RedirectToAction(nameof(Currencies));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRate(int id)
    {
        var x = await Db.ExchangeRates.FindAsync(id);
        if (x != null) { Db.ExchangeRates.Remove(x); await Db.SaveChangesAsync(); Success("Đã xóa tỷ giá."); }
        return RedirectToAction(nameof(Currencies));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant)]
    public async Task<IActionResult> Revalue(DateTime date, [FromServices] FxService fx)
    {
        var (je, n) = await fx.RevalueAsync(date.Date);
        if (je == null) Success("Không có khoản mục nào cần đánh giá lại.");
        else Success($"Đã đánh giá lại {n} khoản mục ngoại tệ tại {date:dd/MM/yyyy} (bút toán {je.Number}) và lập bút toán đảo ngày đầu kỳ sau.");
        return RedirectToAction(nameof(Currencies), new { revalDate = date.ToString("yyyy-MM-dd") });
    }

    // ------------------------------------------------------------------ API
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    public static readonly (string Scope, string Name)[] Scopes =
    {
        ("catalog.read", "Đọc danh mục khách hàng, NCC, hàng hóa, tồn kho"), ("invoice.read", "Đọc hóa đơn (tra cứu HĐĐT)"), ("order.write", "Tạo đơn bán nháp (web, sàn TMĐT)"),
        ("bank.write", "Đẩy sao kê ngân hàng"), ("attendance.write", "Đẩy dữ liệu máy chấm công"), ("scale.write", "Đẩy phiếu cân (thiết bị cân)"),
    };

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Api() => View(await Db.ApiClients.OrderBy(c => c.Name).ToListAsync());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> CreateApiClient(string name, string[] scopes)
    {
        if (string.IsNullOrWhiteSpace(name) || scopes.Length == 0) { Error("Nhập tên ứng dụng và chọn ít nhất một quyền."); return RedirectToAction(nameof(Api)); }
        var key = "erp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Db.ApiClients.Add(new ApiClient { Name = name.Trim(), KeyPrefix = key[..10], KeyHash = Hash(key), Scopes = string.Join(",", scopes.Where(s => Scopes.Any(x => x.Scope == s))) });
        await Db.SaveChangesAsync();
        TempData["NewApiKey"] = key;
        Success($"Đã tạo khóa API cho {name}. Sao chép khóa ngay – hệ thống không hiển thị lại.");
        return RedirectToAction(nameof(Api));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> ToggleApiClient(int id)
    {
        var c = await Db.ApiClients.FindAsync(id); if (c == null) return NotFound();
        c.IsActive = !c.IsActive; await Db.SaveChangesAsync();
        Success(c.IsActive ? "Đã kích hoạt khóa API." : "Đã thu hồi khóa API.");
        return RedirectToAction(nameof(Api));
    }
}
