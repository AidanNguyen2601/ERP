using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

public record ImportPreviewRow(int Line, Dictionary<string, string> Values, List<string> Errors, List<string> Warnings, bool IsUpdate);

/// <summary>Nhập danh mục từ Excel / CSV: khách hàng, nhà cung cấp, hàng hóa, nhân viên, tài khoản, chấm công, tỷ giá. Xem trước, cảnh báo trùng, rồi mới ghi.</summary>
public class ImportController : ErpControllerBase
{
    private readonly CodeService _codes;
    public ImportController(ErpDbContext db, CodeService codes) : base(db) => _codes = codes;

    public record Kind(string Key, string Title, string Roles, string[] Columns, string Note);
    public static readonly Kind[] Kinds =
    {
        new("customer", "Khách hàng", AppRoles.Accounting + "," + AppRoles.Sales, new[] { "Mã", "Tên", "Mã số thuế", "Địa chỉ", "Điện thoại", "Email", "Người liên hệ", "Hạn mức công nợ", "Số ngày được nợ", "Số tài khoản", "Ngân hàng" }, "Để trống cột Mã thì hệ thống tự sinh mã theo quy tắc đặt mã. Mã đã có thì cập nhật thông tin."),
        new("vendor", "Nhà cung cấp", AppRoles.Accounting + "," + AppRoles.Purchasing, new[] { "Mã", "Tên", "Mã số thuế", "Địa chỉ", "Điện thoại", "Email", "Người liên hệ", "Số ngày được nợ", "Số tài khoản", "Ngân hàng" }, "Để trống cột Mã thì tự sinh mã. Mã đã có thì cập nhật."),
        new("item", "Hàng hóa, vật tư", AppRoles.Accounting + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing, new[] { "Mã", "Tên", "Đơn vị tính", "Loại", "Giá bán", "Thuế suất", "Tồn tối thiểu" }, "Loại: NVL / Thành phẩm / Hàng hóa / Dịch vụ. Để trống Mã thì tự sinh theo loại."),
        new("employee", "Nhân viên", AppRoles.Accounting, new[] { "Mã", "Họ tên", "Phòng ban", "Chức danh", "Lương cơ bản", "Phụ cấp", "Phụ cấp không chịu thuế", "Lương đóng bảo hiểm", "Số người phụ thuộc", "Mã số thuế", "Số CCCD", "Số tài khoản", "Ngân hàng", "TK chi phí" }, "TK chi phí: 622 (công nhân trực tiếp), 6271 (nhân viên phân xưởng), 6411 (nhân viên bán hàng), 6421 (nhân viên quản lý) – theo TT99."),
        new("attendance", "Chấm công tháng", AppRoles.Accounting, new[] { "Mã nhân viên", "Năm", "Tháng", "Ngày công", "Giờ làm thêm", "Ngày nghỉ" }, "Có thể xuất từ máy chấm công. Dòng đã có (cùng nhân viên, năm, tháng) thì cập nhật."),
        new("account", "Hệ thống tài khoản", AppRoles.Accounting, new[] { "Số hiệu", "Tên tài khoản", "Theo dõi công nợ" }, "Chỉ thêm tài khoản mới (tài khoản cha phải có sẵn); không sửa tài khoản đã có phát sinh."),
        new("rate", "Tỷ giá", AppRoles.Accounting, new[] { "Loại tiền", "Ngày", "Tỷ giá" }, "Tỷ giá quy đổi 1 đơn vị ngoại tệ sang VND."),
    };

    private Kind? Find(string kind) => Kinds.FirstOrDefault(k => k.Key == kind && User.InAny(k.Roles));

    public IActionResult Index() => View(Kinds.Where(k => User.InAny(k.Roles)).ToList());

    public IActionResult Template(string kind)
    {
        var k = Find(kind); if (k == null) return Denied();
        var cols = k.Columns.Select(c => new ReportExport.Col(c, FieldKind.Text)).ToList();
        return File(ReportExport.Xlsx(k.Title, "MẪU NHẬP " + k.Title.ToUpperInvariant(), new[] { k.Note, "Giữ nguyên dòng tiêu đề cột, nhập dữ liệu từ dòng ngay dưới." }, cols, Array.Empty<object?[]>(), null),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Mau_nhap_{k.Key}.xlsx");
    }

    private static string TempPath(string token) => Path.Combine(Path.GetTempPath(), "erp-import-" + token + ".bin");

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Preview(string kind, IFormFile? file)
    {
        var k = Find(kind); if (k == null) return Denied();
        if (file == null || file.Length == 0) { Error("Chọn file Excel (.xlsx) hoặc CSV."); return RedirectToAction(nameof(Index)); }
        List<Dictionary<string, string>> rows;
        try
        {
            await using var s = file.OpenReadStream();
            rows = TabularReader.Read(s, file.FileName);
            // Mẫu xuất ra có dòng tiêu đề báo cáo phía trên: tìm dòng tiêu đề cột thật
            if (rows.Count > 0 && !rows[0].Keys.Any(x => k.Columns.Select(TabularReader.Key).Contains(x)))
            {
                await using var s2 = file.OpenReadStream();
                rows = ReadSkippingTitle(s2, file.FileName, k);
            }
        }
        catch (Exception ex) { Error("Không đọc được file: " + ex.Message); return RedirectToAction(nameof(Index)); }
        if (rows.Count == 0) { Error("File không có dòng dữ liệu."); return RedirectToAction(nameof(Index)); }
        if (rows.Count > 5000) { Error("Tối đa 5.000 dòng mỗi lần nhập."); return RedirectToAction(nameof(Index)); }
        var token = Guid.NewGuid().ToString("N");
        await System.IO.File.WriteAllTextAsync(TempPath(token), System.Text.Json.JsonSerializer.Serialize(rows));
        ViewBag.Kind = k; ViewBag.Token = token; ViewBag.FileName = file.FileName;
        return View(await ValidateAsync(k, rows));
    }

    private static List<Dictionary<string, string>> ReadSkippingTitle(Stream s, string name, Kind k)
    {
        // Đọc lại dạng thô và bỏ các dòng trước dòng tiêu đề cột
        using var ms = new MemoryStream(); s.CopyTo(ms);
        var keys = k.Columns.Select(TabularReader.Key).ToHashSet();
        for (var skip = 1; skip <= 6; skip++)
        {
            ms.Position = 0;
            var all = TabularReader.Read(ms, name);
            // TabularReader dùng dòng đầu làm tiêu đề: tìm dòng có giá trị trùng tên cột
            var idx = all.FindIndex(r => r.Values.Count(v => keys.Contains(TabularReader.Key(v))) >= 2);
            if (idx < 0) return all;
            var header = all[idx];
            var map = header.ToDictionary(x => x.Key, x => TabularReader.Key(x.Value));
            return all.Skip(idx + 1).Select(r => r.Where(x => map.ContainsKey(x.Key) && map[x.Key].Length > 0).GroupBy(x => map[x.Key]).ToDictionary(g => g.Key, g => g.First().Value)).ToList();
        }
        return new();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Commit(string kind, string token, bool allowDuplicates = false)
    {
        var k = Find(kind); if (k == null) return Denied();
        var path = TempPath(new string(token.Where(char.IsLetterOrDigit).ToArray()));
        if (!System.IO.File.Exists(path)) { Error("Phiên nhập đã hết hạn – tải file lên lại."); return RedirectToAction(nameof(Index)); }
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(await System.IO.File.ReadAllTextAsync(path))!;
        var checks = await ValidateAsync(k, rows);
        if (checks.Any(c => c.Errors.Any())) { Error("File còn dòng lỗi – sửa file rồi nhập lại."); return RedirectToAction(nameof(Index)); }
        if (checks.Any(c => c.Warnings.Any()) && !(allowDuplicates && DuplicateService.CanOverride(User)))
        { Error("Có dòng trùng dữ liệu (mã số thuế / tên). Chỉ Kế toán, Ban Giám đốc, Quản trị được xác nhận nhập dù trùng."); return RedirectToAction(nameof(Index)); }
        var (added, updated) = await ApplyAsync(k, rows);
        System.IO.File.Delete(path);
        await Db.LogAsync("Nhập Excel", "Import", "Nhập dữ liệu", k.Key, k.Title, $"{{\"Thêm mới\":[null,{added}],\"Cập nhật\":[null,{updated}]}}");
        Success($"Đã nhập {k.Title.ToLowerInvariant()}: thêm mới {added}, cập nhật {updated}.");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ kiểm tra từng dòng
    private async Task<List<ImportPreviewRow>> ValidateAsync(Kind k, List<Dictionary<string, string>> rows)
    {
        var res = new List<ImportPreviewRow>();
        var partners = await Db.Partners.ToListAsync(); var items = await Db.Items.ToListAsync(); var emps = await Db.Employees.IgnoreQueryFilters().ToListAsync();
        var accounts = await Db.Accounts.ToListAsync(); var currencies = await Db.Currencies.Select(c => c.Code).ToListAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var err = new List<string>(); var warn = new List<string>(); var upd = false;
            string? G(params string[] n) => TabularReader.Get(r, n);
            switch (k.Key)
            {
                case "customer" or "vendor":
                {
                    var code = G("Mã")?.ToUpperInvariant(); var name = G("Tên");
                    if (string.IsNullOrWhiteSpace(name)) err.Add("Thiếu tên");
                    if (code != null && !seen.Add(code)) err.Add("Trùng mã trong file");
                    var ex = code == null ? null : partners.FirstOrDefault(p => p.Code == code);
                    upd = ex != null;
                    var tax = G("Mã số thuế");
                    if (!string.IsNullOrEmpty(tax))
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(tax, @"^\d{10}(-\d{3})?$|^\d{12}$")) err.Add("Mã số thuế không đúng định dạng (10, 13 hoặc 12 số)");
                        var dup = partners.Where(p => p.TaxCode == tax && p.Id != ex?.Id).Select(p => p.Code).ToList();
                        if (dup.Any()) warn.Add($"Trùng MST với {string.Join(", ", dup)}");
                    }
                    if (name != null && partners.Any(p => p.Name == name && p.Id != ex?.Id)) warn.Add("Trùng tên với đối tượng đã có");
                    break;
                }
                case "item":
                {
                    var code = G("Mã")?.ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(G("Tên"))) err.Add("Thiếu tên");
                    if (ParseType(G("Loại")) == null) err.Add("Loại không hợp lệ (NVL / Thành phẩm / Hàng hóa / Dịch vụ)");
                    if (code != null && !seen.Add(code)) err.Add("Trùng mã trong file");
                    upd = code != null && items.Any(x => x.Code == code);
                    var vat = (int)TabularReader.Dec(G("Thuế suất"), 10);
                    if (vat is not (0 or 5 or 8 or 10)) err.Add("Thuế suất phải là 0, 5, 8 hoặc 10");
                    break;
                }
                case "employee":
                {
                    var code = G("Mã")?.ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(G("Họ tên", "Tên"))) err.Add("Thiếu họ tên");
                    if (code != null && !seen.Add(code)) err.Add("Trùng mã trong file");
                    var ex = code == null ? null : emps.FirstOrDefault(e => e.Code == code); upd = ex != null;
                    var acc = G("TK chi phí") ?? "6421";
                    if (!accounts.Any(a => a.Code == acc && a.IsPostable)) err.Add($"TK chi phí {acc} không tồn tại hoặc không phải TK chi tiết");
                    var tax = G("Mã số thuế");
                    if (!string.IsNullOrEmpty(tax) && emps.Any(e => e.PersonalTaxCode == tax && e.Id != ex?.Id)) warn.Add("Trùng mã số thuế cá nhân");
                    break;
                }
                case "attendance":
                {
                    var code = G("Mã nhân viên", "Mã")?.ToUpperInvariant();
                    if (code == null || !emps.Any(e => e.Code == code)) err.Add($"Không có nhân viên mã {code}");
                    var y = (int)TabularReader.Dec(G("Năm")); var m = (int)TabularReader.Dec(G("Tháng"));
                    if (y < 2000 || m < 1 || m > 12) err.Add("Năm / tháng không hợp lệ");
                    if (TabularReader.Dec(G("Ngày công")) is < 0 or > 31) err.Add("Ngày công 0–31");
                    break;
                }
                case "account":
                {
                    var code = G("Số hiệu", "Số hiệu TK");
                    if (code == null || !System.Text.RegularExpressions.Regex.IsMatch(code, @"^\d{3,8}$")) err.Add("Số hiệu TK 3–8 chữ số");
                    else
                    {
                        upd = accounts.Any(a => a.Code == code);
                        if (code.Length > 3 && !accounts.Any(a => code.StartsWith(a.Code) && a.Code.Length < code.Length) && !rows.Take(i).Any(x => (TabularReader.Get(x, "Số hiệu") ?? "") is var c2 && c2.Length < code.Length && code.StartsWith(c2)))
                            err.Add("Chưa có tài khoản cha");
                    }
                    if (string.IsNullOrWhiteSpace(G("Tên tài khoản", "Tên"))) err.Add("Thiếu tên tài khoản");
                    break;
                }
                case "rate":
                {
                    var cur = G("Loại tiền")?.ToUpperInvariant();
                    if (cur == null || !currencies.Contains(cur) || cur == "VND") err.Add("Loại tiền không có trong danh mục tiền tệ");
                    if (TabularReader.Date(G("Ngày")) == null) err.Add("Ngày không hợp lệ (dd/MM/yyyy)");
                    if (TabularReader.Dec(G("Tỷ giá")) <= 0) err.Add("Tỷ giá phải > 0");
                    break;
                }
            }
            res.Add(new ImportPreviewRow(i + 2, r, err, warn, upd));
        }
        return res;
    }

    private static ItemType? ParseType(string? s) => TabularReader.Key(s ?? "") switch
    {
        "nvl" or "nguyenvatlieu" or "nguyenlieu" or "material" or "vattu" => ItemType.Material,
        "tp" or "thanhpham" or "finishedgood" => ItemType.FinishedGood,
        "hh" or "hanghoa" or "merchandise" or "" => ItemType.Merchandise,
        "dv" or "dichvu" or "service" => ItemType.Service,
        _ => null
    };

    private async Task<(int Added, int Updated)> ApplyAsync(Kind k, List<Dictionary<string, string>> rows)
    {
        int add = 0, upd = 0;
        foreach (var r in rows)
        {
            string? G(params string[] n) => TabularReader.Get(r, n);
            switch (k.Key)
            {
                case "customer" or "vendor":
                {
                    var code = G("Mã")?.ToUpperInvariant() ?? await _codes.NextAsync(k.Key) ?? throw new BusinessException("Chưa có quy tắc đặt mã " + k.Title);
                    var p = await Db.Partners.FirstOrDefaultAsync(x => x.Code == code);
                    if (p == null) { p = new Partner { Code = code, IsCustomer = k.Key == "customer", IsVendor = k.Key == "vendor" }; Db.Partners.Add(p); add++; } else upd++;
                    if (k.Key == "customer") p.IsCustomer = true; else p.IsVendor = true;
                    p.Name = G("Tên")!; p.TaxCode = G("Mã số thuế") ?? p.TaxCode; p.Address = G("Địa chỉ") ?? p.Address; p.Phone = G("Điện thoại") ?? p.Phone; p.Email = G("Email") ?? p.Email;
                    p.ContactPerson = G("Người liên hệ") ?? p.ContactPerson; p.BankAccountNo = G("Số tài khoản") ?? p.BankAccountNo; p.BankName = G("Ngân hàng") ?? p.BankName;
                    if (G("Hạn mức công nợ") is { } lim) p.CreditLimit = TabularReader.Dec(lim);
                    if (G("Số ngày được nợ") is { } days) p.PaymentTermDays = (int)TabularReader.Dec(days, 30);
                    await Db.SaveChangesAsync();
                    break;
                }
                case "item":
                {
                    var type = ParseType(G("Loại"))!.Value;
                    var code = G("Mã")?.ToUpperInvariant() ?? await _codes.NextAsync(CodeService.Target(type)) ?? throw new BusinessException("Chưa có quy tắc đặt mã hàng hóa");
                    var it = await Db.Items.FirstOrDefaultAsync(x => x.Code == code);
                    if (it == null) { it = new Item { Code = code, Type = type }; Db.Items.Add(it); add++; } else upd++;
                    it.Name = G("Tên")!; it.Unit = G("Đơn vị tính", "ĐVT") ?? it.Unit;
                    if (!await Db.StockMoves.AnyAsync(m => m.ItemId == it.Id)) it.Type = type;
                    it.SalePrice = TabularReader.Dec(G("Giá bán"), it.SalePrice); it.VatRate = (int)TabularReader.Dec(G("Thuế suất"), it.VatRate); it.MinStock = TabularReader.Dec(G("Tồn tối thiểu"), it.MinStock);
                    await Db.SaveChangesAsync();
                    break;
                }
                case "employee":
                {
                    var code = G("Mã")?.ToUpperInvariant() ?? await _codes.NextAsync("employee") ?? throw new BusinessException("Chưa có quy tắc đặt mã nhân viên");
                    var e = await Db.Employees.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Code == code);
                    if (e == null) { e = new Employee { Code = code }; Db.Employees.Add(e); add++; } else upd++;
                    e.FullName = G("Họ tên", "Tên")!; e.Department = G("Phòng ban") ?? e.Department; e.Position = G("Chức danh") ?? e.Position;
                    e.BaseSalary = TabularReader.Dec(G("Lương cơ bản"), e.BaseSalary); e.Allowance = TabularReader.Dec(G("Phụ cấp"), e.Allowance);
                    e.NonTaxAllowance = TabularReader.Dec(G("Phụ cấp không chịu thuế"), e.NonTaxAllowance); e.InsuranceSalary = TabularReader.Dec(G("Lương đóng bảo hiểm"), e.InsuranceSalary == 0 ? e.BaseSalary : e.InsuranceSalary);
                    e.Dependents = (int)TabularReader.Dec(G("Số người phụ thuộc"), e.Dependents); e.PersonalTaxCode = G("Mã số thuế") ?? e.PersonalTaxCode; e.IdNumber = G("Số CCCD") ?? e.IdNumber;
                    e.BankAccountNo = G("Số tài khoản") ?? e.BankAccountNo; e.BankName = G("Ngân hàng") ?? e.BankName; e.ExpenseAccount = G("TK chi phí") ?? e.ExpenseAccount;
                    await Db.SaveChangesAsync();
                    break;
                }
                case "attendance":
                {
                    var code = G("Mã nhân viên", "Mã")!.ToUpperInvariant();
                    var e = await Db.Employees.IgnoreQueryFilters().FirstAsync(x => x.Code == code);
                    int y = (int)TabularReader.Dec(G("Năm")), m = (int)TabularReader.Dec(G("Tháng"));
                    var a = await Db.Attendances.FirstOrDefaultAsync(x => x.EmployeeId == e.Id && x.Year == y && x.Month == m);
                    if (a == null) { a = new Attendance { EmployeeId = e.Id, Year = y, Month = m }; Db.Attendances.Add(a); add++; } else upd++;
                    a.WorkDays = TabularReader.Dec(G("Ngày công")); a.OvertimeHours = TabularReader.Dec(G("Giờ làm thêm")); a.LeaveDays = TabularReader.Dec(G("Ngày nghỉ")); a.Source = "Nhập Excel";
                    await Db.SaveChangesAsync();
                    break;
                }
                case "account":
                {
                    var code = G("Số hiệu", "Số hiệu TK")!;
                    var a = await Db.Accounts.FirstOrDefaultAsync(x => x.Code == code);
                    if (a != null) { if (!await Db.JournalLines.IgnoreQueryFilters().AnyAsync(l => l.AccountId == a.Id)) { a.Name = G("Tên tài khoản", "Tên")!; upd++; } continue; }
                    var parent = (await Db.Accounts.ToListAsync()).Where(x => code.StartsWith(x.Code) && x.Code.Length < code.Length).OrderByDescending(x => x.Code.Length).First();
                    Db.Accounts.Add(new Account { Code = code, Name = G("Tên tài khoản", "Tên")!, Type = parent.Type, TrackPartner = TabularReader.Key(G("Theo dõi công nợ") ?? "") is "co" or "x" or "1" or "true" || parent.TrackPartner });
                    if (parent.IsPostable && !await Db.JournalLines.IgnoreQueryFilters().AnyAsync(l => l.AccountId == parent.Id)) parent.IsPostable = false;
                    add++; await Db.SaveChangesAsync();
                    break;
                }
                case "rate":
                {
                    var cur = G("Loại tiền")!.ToUpperInvariant(); var d = TabularReader.Date(G("Ngày"))!.Value;
                    var x = await Db.ExchangeRates.FirstOrDefaultAsync(e => e.CurrencyCode == cur && e.Date == d);
                    if (x == null) { Db.ExchangeRates.Add(new ExchangeRate { CurrencyCode = cur, Date = d, Rate = TabularReader.Dec(G("Tỷ giá")) }); add++; } else { x.Rate = TabularReader.Dec(G("Tỷ giá")); upd++; }
                    await Db.SaveChangesAsync();
                    break;
                }
            }
        }
        return (add, upd);
    }
}
