using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Tỷ giá và đánh giá lại các khoản mục tiền tệ có gốc ngoại tệ cuối kỳ</summary>
public class FxService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    public FxService(ErpDbContext db, AccountingService acc) { _db = db; _acc = acc; }

    /// <summary>Tỷ giá gần nhất đến ngày (VND / 1 ngoại tệ); VND = 1; không có = 0</summary>
    public async Task<decimal> RateAsync(string currency, DateTime date)
    {
        if (string.IsNullOrEmpty(currency) || currency == "VND") return 1;
        var d = date.Date;
        return await _db.ExchangeRates.Where(r => r.CurrencyCode == currency && r.Date <= d).OrderByDescending(r => r.Date).Select(r => (decimal?)r.Rate).FirstOrDefaultAsync()
            ?? await _db.ExchangeRates.Where(r => r.CurrencyCode == currency).OrderBy(r => r.Date).Select(r => (decimal?)r.Rate).FirstOrDefaultAsync() ?? 0;
    }

    public record RevalLine(string Kind, string Ref, string Currency, decimal FcOpen, decimal Book, decimal Rate, decimal NewBook, int AccountId, int? PartnerId)
    {
        public decimal Diff => NewBook - Book;
    }

    /// <summary>Danh sách khoản mục cần đánh giá lại tại ngày: công nợ hóa đơn ngoại tệ còn dư, số dư tài khoản tiền ngoại tệ</summary>
    public async Task<List<RevalLine>> PreviewAsync(DateTime date)
    {
        var list = new List<RevalLine>();
        var invs = await _db.Invoices.Include(i => i.Partner).Where(i => i.Status == InvoiceStatus.Issued && i.CurrencyCode != "VND" && i.Date <= date && i.FcTotal > i.FcPaid).ToListAsync();
        var ar = await _acc.RuleAsync(PostingCatalog.SiReceivable); var ap = await _acc.RuleAsync(PostingCatalog.PiPayable);
        foreach (var i in invs)
        {
            var rate = await RateAsync(i.CurrencyCode, date);
            if (rate <= 0) continue;
            var fcOpen = i.FcTotal - i.FcPaid; var book = i.Total - i.PaidAmount;
            list.Add(new RevalLine(i.Direction == InvoiceDirection.Sales ? "Phải thu" : "Phải trả", $"HĐ {i.Series} {i.InvoiceNo} – {i.Partner?.Name}", i.CurrencyCode, fcOpen, book, rate,
                Math.Round(fcOpen * rate, 0), i.Direction == InvoiceDirection.Sales ? ar : ap, i.PartnerId));
        }
        foreach (var ca in await _db.CashAccounts.Include(c => c.Account).Where(c => c.CurrencyCode != "VND" && c.IsActive).ToListAsync())
        {
            var vs = await _db.CashVouchers.Where(v => v.Status == DocStatus.Posted && v.Date <= date && (v.CashAccountId == ca.Id || v.ToCashAccountId == ca.Id)).ToListAsync();
            var fc = vs.Sum(v => v.ToCashAccountId == ca.Id ? v.FcAmount : v.Type == CashVoucherType.Receipt ? v.FcAmount : -v.FcAmount);
            var book = await _acc.BalanceAsync(ca.Account!.Code, date);
            var rate = await RateAsync(ca.CurrencyCode, date);
            if (rate <= 0 || (fc == 0 && book == 0)) continue;
            list.Add(new RevalLine("Tiền", ca.Name, ca.CurrencyCode, fc, book, rate, Math.Round(fc * rate, 0), ca.AccountId, null));
        }
        return list.Where(l => l.Diff != 0).ToList();
    }

    /// <summary>
    /// Ghi nhận chênh lệch đánh giá lại vào TK 4131 tại ngày đánh giá và tự lập bút toán đảo ngày hôm sau
    /// (chênh lệch chưa thực hiện chỉ phản ánh trên báo cáo tại thời điểm đánh giá; khi thu / trả thực tế sẽ ghi nhận lãi / lỗ đã thực hiện).
    /// </summary>
    public async Task<(JournalEntry? Entry, int Count)> RevalueAsync(DateTime date)
    {
        var items = await PreviewAsync(date);
        if (items.Count == 0) return (null, 0);
        var a4131 = await _acc.IdAsync("4131");
        var lines = new List<JLine>();
        foreach (var x in items)
        {
            // Tài sản (phải thu, tiền): chênh lệch dương ghi Nợ; nợ phải trả: giá trị ghi sổ là số dư Có nên chênh lệch dương ghi Có
            var asset = x.Kind != "Phải trả";
            var d = x.Diff;
            if (asset) { if (d > 0) { lines.Add(new JLine(x.AccountId, d, 0, x.PartnerId, x.Ref)); lines.Add(new JLine(a4131, 0, d)); } else { lines.Add(new JLine(a4131, -d, 0)); lines.Add(new JLine(x.AccountId, 0, -d, x.PartnerId, x.Ref)); } }
            else { if (d > 0) { lines.Add(new JLine(a4131, d, 0)); lines.Add(new JLine(x.AccountId, 0, d, x.PartnerId, x.Ref)); } else { lines.Add(new JLine(x.AccountId, -d, 0, x.PartnerId, x.Ref)); lines.Add(new JLine(a4131, 0, -d)); } }
        }
        var je = await _acc.PostAsync(date, $"Đánh giá lại khoản mục tiền tệ có gốc ngoại tệ ngày {date:dd/MM/yyyy}", JournalSource.Fx, $"DGL{date:yyMMdd}", null, lines);
        try { await _acc.ReverseAsync(je.Id, date.AddDays(1), "Đảo đánh giá lại tỷ giá đầu kỳ sau"); }
        catch (BusinessException) { /* kỳ sau đã khóa: giữ nguyên, người dùng tự đảo */ }
        return (je, items.Count);
    }
}
