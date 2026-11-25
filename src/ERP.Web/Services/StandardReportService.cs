using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

public record GjLine(int EntryId, DateTime Date, string EntryNo, string Source, string Description, string AccountCode, string AccountName, string? Partner, decimal Debit, decimal Credit);

public class CashFlowLine
{
    public string Key { get; init; } = "";
    public string Section { get; init; } = "";
    public string Label { get; init; } = "";
    public string Accounts { get; set; } = "";
    public decimal Amount { get; set; }
}

public class CashFlowReport
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal Opening { get; set; }
    public decimal OpeningInPeriod { get; set; }
    public decimal GlClosing { get; set; }
    public List<CashFlowLine> Lines { get; set; } = new();
    public decimal Section(string s) => Lines.Where(l => l.Section == s).Sum(l => l.Amount);
    public decimal Net => Lines.Sum(l => l.Amount);
    public decimal Closing => Opening + OpeningInPeriod + Net;
    public decimal Diff => GlClosing - Closing;
}

public class StockSumRow
{
    public int ItemId { get; set; }
    public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Unit { get; set; } = ""; public ItemType Type { get; set; }
    public decimal OpenQty, OpenVal, InQty, InVal, OutQty, OutVal;
    public decimal CloseQty => OpenQty + InQty - OutQty;
    public decimal CloseVal => OpenVal + InVal - OutVal;
}

/// <summary>Báo cáo chuẩn: sổ nhật ký chung, lưu chuyển tiền tệ (phương pháp trực tiếp), tổng hợp nhập – xuất – tồn</summary>
public class StandardReportService
{
    private readonly ErpDbContext _db;
    public StandardReportService(ErpDbContext db) { _db = db; }

    // ------------------------------------------------------------------ Sổ nhật ký chung (mẫu S03a-DN)
    public async Task<List<GjLine>> GeneralJournalAsync(DateTime from, DateTime to, bool includeClosing = true)
    {
        var f = from.Date; var t = to.Date;
        var q = _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Date >= f && l.JournalEntry.Date <= t);
        if (!includeClosing) q = q.Where(l => l.JournalEntry!.Source != JournalSource.Closing);
        var rows = await q.OrderBy(l => l.JournalEntry!.Date).ThenBy(l => l.JournalEntry!.Id).ThenBy(l => l.Id)
            .Select(l => new { l.JournalEntryId, l.JournalEntry!.Date, l.JournalEntry.Number, l.JournalEntry.Source, Desc = l.JournalEntry.Description, l.Account!.Code, l.Account.Name, P = l.Partner != null ? l.Partner.Name : null, l.Debit, l.Credit }).ToListAsync();
        return rows.Select(r => new GjLine(r.JournalEntryId, r.Date, r.Number, r.Source.GetDisplayName(), r.Desc, r.Code, r.Name, r.P, r.Debit, r.Credit)).ToList();
    }

    // ------------------------------------------------------------------ Báo cáo lưu chuyển tiền tệ (trực tiếp)
    private static readonly (string Key, string Section, string Label)[] CfDefs =
    {
        ("op_sales", "I", "1. Tiền thu từ bán hàng, cung cấp dịch vụ và doanh thu khác"),
        ("op_supply", "I", "2. Tiền chi trả cho người cung cấp hàng hóa và dịch vụ"),
        ("op_staff", "I", "3. Tiền chi trả cho người lao động"),
        ("op_interest", "I", "4. Tiền lãi vay đã trả"),
        ("op_tax", "I", "5. Tiền thu / chi về thuế và các khoản nộp ngân sách"),
        ("op_other", "I", "6. Tiền thu / chi khác từ hoạt động kinh doanh"),
        ("inv_buy", "II", "1. Tiền chi để mua sắm, xây dựng TSCĐ và tài sản dài hạn khác"),
        ("inv_sell", "II", "2. Tiền thu từ thanh lý, nhượng bán TSCĐ và tài sản dài hạn khác"),
        ("inv_int", "II", "3. Tiền thu lãi cho vay, cổ tức và lợi nhuận được chia"),
        ("fin_in", "III", "1. Tiền thu từ đi vay và nhận vốn góp của chủ sở hữu"),
        ("fin_out", "III", "2. Tiền trả nợ gốc vay và chi trả vốn / chia lợi nhuận"),
    };

    private static string Classify(string code, decimal amount, JournalSource source)
    {
        bool P(params string[] prefixes) => prefixes.Any(code.StartsWith);
        if (source == JournalSource.FixedAsset && P("711", "811")) return "inv_sell";
        if (P("211", "213", "217", "241")) return amount < 0 ? "inv_buy" : "inv_sell";
        if (P("341", "343", "411")) return amount > 0 ? "fin_in" : "fin_out";
        if (P("421")) return amount < 0 ? "fin_out" : "op_other";
        if (P("515")) return "inv_int";
        if (P("635")) return "op_interest";
        if (P("131", "511", "512", "521")) return "op_sales";
        if (P("334")) return "op_staff";
        if (P("133", "333")) return "op_tax";
        if (P("331", "15", "62", "63", "64", "242", "244", "6")) return "op_supply";
        return "op_other";
    }

    public async Task<CashFlowReport> CashFlowAsync(DateTime from, DateTime to)
    {
        var f = from.Date; var t = to.Date;
        var rep = new CashFlowReport { From = f, To = t };
        var cashLines = _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && (l.Account!.Code.StartsWith("111") || l.Account.Code.StartsWith("112")));
        rep.Opening = await cashLines.Where(l => l.JournalEntry!.Date < f).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        rep.GlClosing = await cashLines.Where(l => l.JournalEntry!.Date <= t).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        // Số dư đầu kỳ chuyển sang ghi sổ trong kỳ cũng là tiền đầu kỳ (không phải lưu chuyển tiền)
        rep.Opening += await cashLines.Where(l => l.JournalEntry!.Date >= f && l.JournalEntry.Date <= t && l.JournalEntry.Source == JournalSource.Opening).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;

        var entryIds = await cashLines.Where(l => l.JournalEntry!.Date >= f && l.JournalEntry.Date <= t && l.JournalEntry.Source != JournalSource.Opening && l.JournalEntry.Source != JournalSource.Closing)
            .Select(l => l.JournalEntryId).Distinct().ToListAsync();
        var lines = await _db.JournalLines.AsNoTracking().Where(l => entryIds.Contains(l.JournalEntryId))
            .Select(l => new { l.JournalEntryId, l.JournalEntry!.Source, Code = l.Account!.Code, l.Debit, l.Credit }).ToListAsync();

        var totals = CfDefs.ToDictionary(d => d.Key, _ => 0m); var used = CfDefs.ToDictionary(d => d.Key, _ => new SortedSet<string>());
        static bool IsCash(string code) => code.StartsWith("111") || code.StartsWith("112");
        foreach (var e in lines.GroupBy(l => l.JournalEntryId))
        {
            var source = e.First().Source;
            // Thanh lý / ghi tăng TSCĐ có nhiều dòng không dùng tiền (hao mòn, giá trị còn lại...): chỉ phần tiền thực thu / chi được xếp vào hoạt động đầu tư
            if (source == JournalSource.FixedAsset)
            {
                var cash = e.Where(l => IsCash(l.Code)).Sum(l => l.Debit - l.Credit);
                var key = cash >= 0 ? "inv_sell" : "inv_buy"; totals[key] += cash;
                foreach (var c in e.Where(l => !IsCash(l.Code))) used[key].Add(c.Code.Length > 3 ? c.Code[..3] : c.Code);
                continue;
            }
            foreach (var l in e.Where(l => !IsCash(l.Code)))
            {
                var amount = l.Credit - l.Debit;      // dương: tiền vào do bút toán đối ứng này
                var key = Classify(l.Code, amount, l.Source);
                totals[key] += amount; used[key].Add(l.Code.Length > 3 ? l.Code[..3] : l.Code);
            }
        }
        foreach (var d in CfDefs) rep.Lines.Add(new CashFlowLine { Key = d.Key, Section = d.Section, Label = d.Label, Amount = totals[d.Key], Accounts = string.Join(", ", used[d.Key]) });
        return rep;
    }

    // ------------------------------------------------------------------ Tổng hợp nhập – xuất – tồn
    public async Task<List<StockSumRow>> StockSummaryAsync(DateTime from, DateTime to, int? warehouseId, ItemType? type, string? q)
    {
        var f = from.Date; var t = to.Date;
        var mq = _db.StockMoves.AsNoTracking().Where(m => m.Date <= t);
        if (warehouseId.HasValue) mq = mq.Where(m => m.WarehouseId == warehouseId);
        var agg = await mq.GroupBy(m => m.ItemId).Select(g => new
        {
            ItemId = g.Key,
            OpenQty = g.Where(m => m.Date < f).Sum(m => m.Quantity), OpenVal = g.Where(m => m.Date < f).Sum(m => m.Amount),
            InQty = g.Where(m => m.Date >= f && m.Quantity > 0).Sum(m => m.Quantity), InVal = g.Where(m => m.Date >= f && m.Quantity > 0).Sum(m => m.Amount),
            OutQty = g.Where(m => m.Date >= f && m.Quantity < 0).Sum(m => -m.Quantity), OutVal = g.Where(m => m.Date >= f && m.Quantity < 0).Sum(m => -m.Amount),
        }).ToListAsync();
        var items = await _db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
        var rows = agg.Select(a => new StockSumRow
        {
            ItemId = a.ItemId, Code = items[a.ItemId].Code, Name = items[a.ItemId].Name, Unit = items[a.ItemId].Unit, Type = items[a.ItemId].Type,
            OpenQty = a.OpenQty, OpenVal = a.OpenVal, InQty = a.InQty, InVal = a.InVal, OutQty = a.OutQty, OutVal = a.OutVal
        }).Where(r => r.OpenQty != 0 || r.OpenVal != 0 || r.InQty != 0 || r.OutQty != 0 || r.CloseQty != 0 || r.CloseVal != 0);
        if (type.HasValue) rows = rows.Where(r => r.Type == type);
        if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(r => r.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        return rows.OrderBy(r => r.Type).ThenBy(r => r.Code).ToList();
    }

    /// <summary>Số dư các TK hàng tồn kho (152, 155, 156) đến hết ngày – để đối chiếu tổng giá trị tồn cuối kỳ</summary>
    public async Task<decimal> StockGlBalanceAsync(DateTime to)
    {
        var t = to.Date;
        return await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Date <= t
            && (l.Account!.Code.StartsWith("152") || l.Account.Code.StartsWith("155") || l.Account.Code.StartsWith("156"))).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
    }
}
