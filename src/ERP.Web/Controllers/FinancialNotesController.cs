using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Bản thuyết minh báo cáo tài chính (mẫu B09-DN): chính sách kế toán và chi tiết các khoản mục, lập từ sổ cái</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class FinancialNotesController : ErpControllerBase
{
    private readonly AccountingService _acc; private readonly SettingsService _set; private readonly ErpSettings _cfg;
    public FinancialNotesController(ErpDbContext db, AccountingService acc, SettingsService set, IOptions<ErpSettings> cfg) : base(db) { _acc = acc; _set = set; _cfg = cfg.Value; }

    public record NoteLine(string Label, decimal? Opening, decimal? Closing, decimal? Increase = null, decimal? Decrease = null, bool Bold = false);
    public record NoteSection(string No, string Title, string[] Headers, List<NoteLine> Lines, string? Text = null);

    private async Task<(decimal Debit, decimal Credit)> DcAsync(string prefix, DateTime from, DateTime to)
    {
        var r = await Db.JournalLines.Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.Code.StartsWith(prefix) && l.JournalEntry.Date >= from && l.JournalEntry.Date <= to && l.JournalEntry.Source != JournalSource.Closing)
            .GroupBy(_ => 1).Select(g => new { D = g.Sum(l => l.Debit), C = g.Sum(l => l.Credit) }).FirstOrDefaultAsync();
        return (r?.D ?? 0, r?.C ?? 0);
    }

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, bool excel = false)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? new DateTime(t.Year, 1, 1)).Date; var open = f.AddDays(-1);
        async Task<NoteLine> Bal(string label, string prefix, int sign = 1, bool bold = false) => new(label, sign * await _acc.BalanceAsync(prefix, open), sign * await _acc.BalanceAsync(prefix, t), null, null, bold);
        async Task<NoteLine> Mov(string label, string prefix, int sign = -1, bool bold = false) => new(label, null, sign * await _acc.MovementAsync(prefix, f, t), null, null, bold);
        var regime = await _set.GetAsync("Accounting.Regime", "TT99");
        var s = new List<NoteSection>
        {
            new("I", "Đặc điểm hoạt động của doanh nghiệp", Array.Empty<string>(), new(),
                $"{_cfg.CompanyName} · MST {_cfg.CompanyTaxCode} · {_cfg.CompanyAddress}. Hình thức sở hữu, lĩnh vực và ngành nghề kinh doanh: theo Giấy chứng nhận đăng ký doanh nghiệp."),
            new("II", "Kỳ kế toán, đơn vị tiền tệ sử dụng trong kế toán", Array.Empty<string>(), new(),
                $"Kỳ kế toán năm bắt đầu từ 01/01 và kết thúc 31/12. Kỳ lập thuyết minh: {f:dd/MM/yyyy} – {t:dd/MM/yyyy}. Đơn vị tiền tệ: đồng Việt Nam (VND)."),
            new("III", "Chuẩn mực và chế độ kế toán áp dụng", Array.Empty<string>(), new(),
                regime == "TT133" ? "Chế độ kế toán doanh nghiệp nhỏ và vừa theo Thông tư 133/2016/TT-BTC." : "Chế độ kế toán doanh nghiệp theo Thông tư 99/2025/TT-BTC (thay thế Thông tư 200/2014/TT-BTC), áp dụng chuẩn mực kế toán Việt Nam."),
            new("IV", "Các chính sách kế toán áp dụng", Array.Empty<string>(), new(),
                "Hàng tồn kho ghi nhận theo giá gốc, giá xuất kho theo phương pháp bình quân gia quyền (hoặc FIFO với mặt hàng khai báo), hạch toán theo phương pháp kê khai thường xuyên. " +
                "Tài sản cố định ghi nhận theo nguyên giá, khấu hao theo phương pháp đường thẳng dựa trên thời gian sử dụng ước tính. " +
                "Doanh thu ghi nhận khi chuyển giao phần lớn rủi ro và lợi ích cho người mua (khi xuất hóa đơn). Giao dịch ngoại tệ quy đổi theo tỷ giá thực tế, đánh giá lại số dư cuối kỳ."),
            new("V.1", "Tiền và các khoản tương đương tiền", new[] { "Đầu kỳ", "Cuối kỳ" }, new()
                { await Bal("Tiền mặt", "111"), await Bal("Tiền gửi ngân hàng không kỳ hạn", "112"), await Bal("Tiền đang chuyển", "113"), await Bal("Cộng", "11", 1, true) }),
        };
        // phải thu / phải trả theo đối tượng
        var names = await Db.Partners.ToDictionaryAsync(p => p.Id, p => p.Code + " – " + p.Name);
        async Task<NoteSection> ByPartner(string no, string title, string prefix, int sign)
        {
            var o = await _acc.PartnerBalancesAsync(prefix, open); var c = await _acc.PartnerBalancesAsync(prefix, t);
            var lines = c.Keys.Union(o.Keys).Select(k => new NoteLine(names.GetValueOrDefault(k, "#" + k), sign * o.GetValueOrDefault(k), sign * c.GetValueOrDefault(k)))
                .Where(l => l.Opening != 0 || l.Closing != 0).OrderByDescending(l => Math.Abs(l.Closing ?? 0)).ToList();
            var top = lines.Take(10).ToList();
            if (lines.Count > 10) top.Add(new NoteLine($"Các đối tượng khác ({lines.Count - 10})", lines.Skip(10).Sum(l => l.Opening), lines.Skip(10).Sum(l => l.Closing)));
            top.Add(new NoteLine("Cộng", lines.Sum(l => l.Opening), lines.Sum(l => l.Closing), null, null, true));
            return new NoteSection(no, title, new[] { "Đầu kỳ", "Cuối kỳ" }, top);
        }
        s.Add(await ByPartner("V.2", "Phải thu của khách hàng", "131", 1));
        s.Add(new("V.3", "Hàng tồn kho (giá gốc)", new[] { "Đầu kỳ", "Cuối kỳ" }, new()
            { await Bal("Hàng mua đang đi đường", "151"), await Bal("Nguyên liệu, vật liệu", "152"), await Bal("Công cụ, dụng cụ", "153"), await Bal("Chi phí sản xuất, kinh doanh dở dang", "154"),
              await Bal("Thành phẩm", "155"), await Bal("Hàng hóa", "156"), await Bal("Hàng gửi bán", "157"), await Bal("Cộng", "15", 1, true) }));
        var (incA, decA) = await DcAsync("211", f, t); var (incD, decD) = await DcAsync("214", f, t);
        s.Add(new("V.4", "Tăng, giảm tài sản cố định hữu hình", new[] { "Đầu kỳ", "Tăng trong kỳ", "Giảm trong kỳ", "Cuối kỳ" }, new()
        {
            new("Nguyên giá", await _acc.BalanceAsync("211", open), await _acc.BalanceAsync("211", t), incA, decA),
            new("Hao mòn lũy kế", -await _acc.BalanceAsync("214", open), -await _acc.BalanceAsync("214", t), decD, incD),
            new("Giá trị còn lại", await _acc.BalanceAsync("211", open) + await _acc.BalanceAsync("214", open), await _acc.BalanceAsync("211", t) + await _acc.BalanceAsync("214", t), null, null, true),
        }));
        s.Add(await ByPartner("V.5", "Phải trả người bán", "331", -1));
        s.Add(new("V.6", "Thuế và các khoản phải nộp Nhà nước", new[] { "Đầu kỳ", "Cuối kỳ" }, new()
            { await Bal("Thuế GTGT phải nộp", "3331", -1), await Bal("Thuế thu nhập doanh nghiệp", "3334", -1), await Bal("Thuế thu nhập cá nhân", "3335", -1), await Bal("Các loại thuế khác", "3338", -1), await Bal("Cộng", "333", -1, true) }));
        s.Add(new("V.7", "Phải trả người lao động và các khoản trích theo lương", new[] { "Đầu kỳ", "Cuối kỳ" }, new()
            { await Bal("Phải trả người lao động", "334", -1), await Bal("Kinh phí công đoàn", "3382", -1), await Bal("Bảo hiểm xã hội", "3383", -1), await Bal("Bảo hiểm y tế", "3384", -1), await Bal("Bảo hiểm thất nghiệp", "3386", -1) }));
        s.Add(new("V.8", "Vốn chủ sở hữu", new[] { "Đầu kỳ", "Cuối kỳ" }, new()
            { await Bal("Vốn góp của chủ sở hữu", "411", -1), await Bal("Lợi nhuận sau thuế chưa phân phối", "421", -1), await Bal("Cộng vốn chủ sở hữu", "4", -1, true) }));
        s.Add(new("VI.1", "Doanh thu bán hàng và cung cấp dịch vụ", new[] { "Kỳ này" }, new()
            { await Mov("Doanh thu bán hàng hóa", "5111"), await Mov("Doanh thu bán thành phẩm", "5112"), await Mov("Doanh thu cung cấp dịch vụ", "5113"), await Mov("Doanh thu khác", "5118"),
              await Mov("Các khoản giảm trừ doanh thu (hàng bán trả lại, giảm giá)", "521", 1), await Mov("Doanh thu thuần", "511", -1, true) }));
        s.Add(new("VI.2", "Giá vốn hàng bán", new[] { "Kỳ này" }, new() { await Mov("Giá vốn hàng bán", "632", 1, true) }));
        s.Add(new("VI.3", "Doanh thu và chi phí hoạt động tài chính", new[] { "Kỳ này" }, new() { await Mov("Doanh thu hoạt động tài chính", "515"), await Mov("Chi phí tài chính", "635", 1) }));
        s.Add(new("VI.4", "Chi phí bán hàng và chi phí quản lý doanh nghiệp", new[] { "Kỳ này" }, new()
            { await Mov("Chi phí bán hàng", "641", 1), await Mov("Chi phí quản lý doanh nghiệp", "642", 1), await Mov("Trong đó: chi phí nhân viên quản lý", "6421", 1), await Mov("Chi phí khấu hao TSCĐ (quản lý)", "6424", 1) }));
        s.Add(new("VI.5", "Thu nhập khác và chi phí khác", new[] { "Kỳ này" }, new() { await Mov("Thu nhập khác", "711"), await Mov("Chi phí khác", "811", 1) }));
        s.Add(new("VI.6", "Chi phí thuế thu nhập doanh nghiệp hiện hành", new[] { "Kỳ này" }, new() { await Mov("Chi phí thuế TNDN hiện hành", "8211", 1, true) }));

        if (excel)
        {
            var rows = new List<object?[]>();
            foreach (var sec in s)
            {
                rows.Add(new object?[] { $"{sec.No}. {sec.Title}", null, null, null, null });
                if (sec.Text != null) rows.Add(new object?[] { sec.Text, null, null, null, null });
                foreach (var l in sec.Lines) rows.Add(new object?[] { "   " + l.Label, l.Opening, l.Increase, l.Decrease, l.Closing });
            }
            return File(ReportExport.Xlsx("B09-DN", "BẢN THUYẾT MINH BÁO CÁO TÀI CHÍNH (B09-DN)", new[] { _cfg.CompanyName, $"Từ {f:dd/MM/yyyy} đến {t:dd/MM/yyyy}" },
                new List<ReportExport.Col> { new("Chỉ tiêu", FieldKind.Text), new("Đầu kỳ", FieldKind.Money), new("Tăng", FieldKind.Money), new("Giảm", FieldKind.Money), new("Cuối kỳ / kỳ này", FieldKind.Money) }, rows, null),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"B09DN_{t:yyyyMMdd}.xlsx");
        }
        ViewBag.From = f; ViewBag.To = t;
        return View(s);
    }
}
