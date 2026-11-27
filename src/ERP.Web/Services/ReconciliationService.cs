using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Một dòng đối chiếu: số của phân hệ (Left) so với số của sổ cái / chứng từ liên quan (Right)</summary>
public record ReconRow(string Key, string Name, decimal Left, decimal Right, string? Note = null, string? Url = null)
{
    public decimal Diff => Left - Right;
}

public enum ReconKind { Money, Qty, Count }

/// <summary>Kết quả một phép đối chiếu</summary>
public class ReconCheck
{
    public string Code { get; init; } = "";
    public string Group { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string LeftLabel { get; init; } = "Phân hệ";
    public string RightLabel { get; init; } = "Sổ cái";
    public ReconKind Kind { get; init; } = ReconKind.Money;
    /// <summary>Tổng hai phía (hiển thị ở đầu phần chi tiết)</summary>
    public decimal LeftTotal { get; set; }
    public decimal RightTotal { get; set; }
    /// <summary>Chỉ các dòng lệch</summary>
    public List<ReconRow> Rows { get; set; } = new();
    public int Checked { get; set; }
    public string? Hint { get; set; }
    public bool Ok => Rows.Count == 0;
}

/// <summary>
/// Đối chiếu các phân hệ (kho, mua hàng, bán hàng, công nợ, quỹ, TSCĐ, sản xuất, thuế) với sổ cái để bảo đảm
/// mọi nghiệp vụ đều đã hạch toán đủ, đúng số tiền và không còn chênh lệch giữa sổ chi tiết và sổ tổng hợp.
/// </summary>
public class ReconciliationService
{
    public const decimal Tolerance = 0.5m;
    private readonly ErpDbContext _db;
    public ReconciliationService(ErpDbContext db) { _db = db; }

    public async Task<List<ReconCheck>> RunAsync()
    {
        var list = new List<ReconCheck>
        {
            await LedgerBalancedAsync(), await LedgerPostableAsync(), await LedgerPartnerAsync(),
            await DocsHaveEntriesAsync(), await CancelledHaveReversalsAsync(), await InvoiceAmountsAsync(),
            await StockValueAsync(), await StockQtyAsync(), await StockResidualAsync(),
            await ReceivableAsync(), await PayableAsync(),
            await CashAsync(), await AssetsCostAsync(), await AssetsDepAsync(), await WipAsync(),
            await VatAsync(), await RevenueAsync(), await CogsAsync(),
            await OrderReceivedAsync(), await OrderDeliveredAsync(), await OrderInvoicedAsync(), await ProductionIssuedAsync(), await VendorReturnsAsync(), await SalesCreditsAsync(),
        };
        return list;
    }

    private static ReconCheck Count(string code, string group, string title, string desc, List<ReconRow> rows, int checkedN, string? hint = null) => new()
    {
        Code = code, Group = group, Title = title, Description = desc, Kind = ReconKind.Count, LeftLabel = "Số lượng lỗi", RightLabel = "Kỳ vọng",
        Rows = rows, Checked = checkedN, Hint = hint, LeftTotal = rows.Count, RightTotal = 0
    };

    // ------------------------------------------------------------------ A. Sổ cái tự thân
    private async Task<ReconCheck> LedgerBalancedAsync()
    {
        var entries = await _db.JournalEntries.AsNoTracking().Where(j => j.Status == DocStatus.Posted)
            .Select(j => new { j.Id, j.Number, D = j.Lines.Sum(l => l.Debit), C = j.Lines.Sum(l => l.Credit) }).ToListAsync();
        var bad = entries.Where(e => Math.Abs(e.D - e.C) >= Tolerance).Select(e => new ReconRow(e.Number, "Bút toán không cân Nợ / Có", e.D, e.C, null, $"/Journals/Details/{e.Id}")).ToList();
        var c = Count("A1", "Sổ cái", "Mọi bút toán đã ghi sổ cân Nợ = Có", "Tổng Nợ phải bằng tổng Có trên từng bút toán.", bad, entries.Count);
        c.LeftTotal = entries.Sum(e => e.D); c.RightTotal = entries.Sum(e => e.C);
        return c;
    }

    private async Task<ReconCheck> LedgerPostableAsync()
    {
        var rows = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && (!l.Account!.IsPostable || !l.Account.IsActive))
            .Select(l => new { l.JournalEntry!.Id, l.JournalEntry.Number, l.Account!.Code, l.Account.IsPostable }).ToListAsync();
        return Count("A2", "Sổ cái", "Chỉ hạch toán vào tài khoản chi tiết đang sử dụng", "Không được ghi sổ vào tài khoản tổng hợp hoặc tài khoản đã ngưng.",
            rows.Select(r => new ReconRow(r.Number, $"TK {r.Code} {(r.IsPostable ? "đã ngưng sử dụng" : "là tài khoản tổng hợp")}", 1, 0, null, $"/Journals/Details/{r.Id}")).ToList(),
            await _db.JournalLines.CountAsync());
    }

    private async Task<ReconCheck> LedgerPartnerAsync()
    {
        var rows = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.TrackPartner && l.PartnerId == null)
            .Select(l => new { l.JournalEntry!.Id, l.JournalEntry.Number, l.Account!.Code }).ToListAsync();
        return Count("A3", "Sổ cái", "Tài khoản công nợ luôn có đối tượng", "Dòng hạch toán vào TK công nợ (131, 331…) phải gắn khách hàng / nhà cung cấp.",
            rows.Select(r => new ReconRow(r.Number, $"TK {r.Code} thiếu đối tượng", 1, 0, null, $"/Journals/Details/{r.Id}")).ToList(),
            await _db.JournalLines.CountAsync(l => l.Account!.TrackPartner));
    }

    // ------------------------------------------------------------------ B. Chứng từ ↔ bút toán
    private async Task<ReconCheck> DocsHaveEntriesAsync()
    {
        var posted = (await _db.JournalEntries.AsNoTracking().Where(j => j.Status == DocStatus.Posted).Select(j => j.Id).ToListAsync()).ToHashSet();
        var rows = new List<ReconRow>();
        var n = 0;

        var inv = await _db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.Status == InvoiceStatus.Issued && i.Series != "DK").ToListAsync();
        foreach (var i in inv)
        {
            n++;
            // Hóa đơn mua chỉ gồm hàng đã nhập kho và không có thuế thì không cần bút toán
            var needs = i.Direction == InvoiceDirection.Sales ? i.Total > 0 : i.Lines.Any(l => !l.GoodsPosted) || i.VatAmount > 0;
            if (needs && (i.JournalEntryId == null || !posted.Contains(i.JournalEntryId.Value)))
                rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, $"Hóa đơn {(i.Direction == InvoiceDirection.Sales ? "bán" : "mua")} đã phát hành nhưng chưa có bút toán", i.Total, 0, null, $"/Invoices/Details/{i.Id}"));
        }
        var cash = await _db.CashVouchers.AsNoTracking().Where(v => v.Status == DocStatus.Posted).ToListAsync();
        foreach (var v in cash)
        {
            n++;
            if (v.JournalEntryId == null || !posted.Contains(v.JournalEntryId.Value))
                rows.Add(new ReconRow(v.Number, "Phiếu thu / chi đã ghi sổ nhưng chưa có bút toán", v.Amount, 0, null, $"/Cash/Details/{v.Id}"));
        }
        var docs = await _db.InventoryDocs.AsNoTracking().Where(d => d.Status == DocStatus.Posted && d.Type != InvDocType.Transfer)
            .Select(d => new { d.Id, d.Number, d.JournalEntryId, Amt = d.Lines.Sum(l => l.Amount) }).ToListAsync();
        foreach (var d in docs)
        {
            n++;
            if (d.Amt != 0 && (d.JournalEntryId == null || !posted.Contains(d.JournalEntryId.Value)))
                rows.Add(new ReconRow(d.Number, "Phiếu kho có giá trị nhưng chưa có bút toán", d.Amt, 0, null, $"/Inventory/Details/{d.Id}"));
        }
        var runs = await _db.DepreciationRuns.AsNoTracking().ToListAsync();
        foreach (var r in runs)
        {
            n++;
            if (r.JournalEntryId == null || !posted.Contains(r.JournalEntryId.Value))
                rows.Add(new ReconRow($"KH{r.Month:00}/{r.Year}", "Kỳ khấu hao chưa có bút toán", r.Total, 0));
        }
        return Count("B1", "Chứng từ ↔ bút toán", "Mọi chứng từ đã ghi sổ đều có bút toán", "Hóa đơn, phiếu thu / chi, phiếu kho, kỳ khấu hao đã ghi sổ phải trỏ tới một bút toán đã ghi sổ.", rows, n);
    }

    private async Task<ReconCheck> CancelledHaveReversalsAsync()
    {
        var reversed = (await _db.JournalEntries.AsNoTracking().Where(j => j.ReversalOfId != null).Select(j => j.ReversalOfId!.Value).ToListAsync()).ToHashSet();
        var rows = new List<ReconRow>(); var n = 0;
        foreach (var i in await _db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Cancelled && i.JournalEntryId != null).ToListAsync())
        { n++; if (!reversed.Contains(i.JournalEntryId!.Value)) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Hóa đơn đã hủy nhưng bút toán chưa được đảo", i.Total, 0, null, $"/Invoices/Details/{i.Id}")); }
        foreach (var v in await _db.CashVouchers.AsNoTracking().Where(v => v.Status == DocStatus.Cancelled && v.JournalEntryId != null).ToListAsync())
        { n++; if (!reversed.Contains(v.JournalEntryId!.Value)) rows.Add(new ReconRow(v.Number, "Phiếu đã hủy nhưng bút toán chưa được đảo", v.Amount, 0, null, $"/Cash/Details/{v.Id}")); }
        var cancelledDocIds = (await _db.InventoryDocs.AsNoTracking().Where(d => d.Status == DocStatus.Cancelled && d.JournalEntryId != null).Select(d => new { d.Number, d.JournalEntryId, d.Id }).ToListAsync());
        foreach (var d in cancelledDocIds)
        { n++; if (!reversed.Contains(d.JournalEntryId!.Value)) rows.Add(new ReconRow(d.Number, "Phiếu kho đã hủy nhưng bút toán chưa được đảo", 1, 0, null, $"/Inventory/Details/{d.Id}")); }
        // Bút toán đảo phải triệt tiêu hoàn toàn bút toán gốc
        var revs = await _db.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.ReversalOfId != null && j.Status == DocStatus.Posted).ToListAsync();
        var srcIds = revs.Select(r => r.ReversalOfId!.Value).ToList();
        var srcs = (await _db.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => srcIds.Contains(j.Id)).ToListAsync()).ToDictionary(j => j.Id);
        foreach (var r in revs)
        {
            n++;
            if (!srcs.TryGetValue(r.ReversalOfId!.Value, out var s)) continue;
            var a = s.Lines.GroupBy(l => (l.AccountId, l.PartnerId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
            var b = r.Lines.GroupBy(l => (l.AccountId, l.PartnerId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
            if (a.Keys.Union(b.Keys).Any(k => Math.Abs(a.GetValueOrDefault(k) + b.GetValueOrDefault(k)) >= Tolerance))
                rows.Add(new ReconRow(r.Number, $"Bút toán đảo không triệt tiêu hết bút toán gốc {s.Number}", 1, 0, null, $"/Journals/Details/{r.Id}"));
        }
        return Count("B2", "Chứng từ ↔ bút toán", "Chứng từ hủy đều có bút toán đảo đúng", "Hủy hóa đơn / phiếu thu chi phải đảo bút toán gốc và bút toán đảo phải triệt tiêu hoàn toàn.", rows, n);
    }

    private async Task<ReconCheck> InvoiceAmountsAsync()
    {
        var invs = await _db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.Status == InvoiceStatus.Issued && i.JournalEntryId != null && i.Series != "DK").ToListAsync();
        var ids = invs.Select(i => i.JournalEntryId!.Value).ToList();
        var lines = (await _db.JournalLines.AsNoTracking().Include(l => l.Account).Where(l => ids.Contains(l.JournalEntryId)).ToListAsync()).GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<ReconRow>();
        foreach (var i in invs)
        {
            if (!lines.TryGetValue(i.JournalEntryId!.Value, out var jl)) continue;
            if (i.Direction == InvoiceDirection.Sales)
            {
                var ar = jl.Where(l => l.Account!.Code.StartsWith("131")).Sum(l => l.Debit - l.Credit);
                var rev = jl.Where(l => l.Account!.Code.StartsWith("5")).Sum(l => l.Credit - l.Debit);
                var vat = jl.Where(l => l.Account!.Code.StartsWith("3331")).Sum(l => l.Credit - l.Debit);
                if (Math.Abs(ar - i.Total) >= Tolerance) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Phải thu trên bút toán ≠ tổng tiền hóa đơn bán", i.Total, ar, null, $"/Invoices/Details/{i.Id}"));
                else if (Math.Abs(rev - i.Subtotal) >= Tolerance) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Doanh thu trên bút toán ≠ tiền hàng hóa đơn bán", i.Subtotal, rev, null, $"/Invoices/Details/{i.Id}"));
                else if (Math.Abs(vat - i.VatAmount) >= Tolerance) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Thuế đầu ra trên bút toán ≠ thuế hóa đơn bán", i.VatAmount, vat, null, $"/Invoices/Details/{i.Id}"));
            }
            else
            {
                var ap = jl.Where(l => l.Account!.Code.StartsWith("331")).Sum(l => l.Credit - l.Debit);
                var vat = jl.Where(l => l.Account!.Code.StartsWith("133")).Sum(l => l.Debit - l.Credit);
                var expectAp = i.Lines.Where(l => !l.GoodsPosted).Sum(l => l.Amount) + i.VatAmount;
                if (Math.Abs(ap - expectAp) >= Tolerance) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Phải trả trên bút toán ≠ giá trị hóa đơn mua chưa nhập kho + thuế", expectAp, ap, null, $"/Invoices/Details/{i.Id}"));
                else if (Math.Abs(vat - i.VatAmount) >= Tolerance) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Thuế đầu vào trên bút toán ≠ thuế hóa đơn mua", i.VatAmount, vat, null, $"/Invoices/Details/{i.Id}"));
            }
        }
        return Count("B3", "Chứng từ ↔ bút toán", "Số tiền hóa đơn khớp bút toán", "Phải thu / phải trả, doanh thu và thuế GTGT trên bút toán phải bằng số liệu trên hóa đơn.", rows, invs.Count);
    }

    // ------------------------------------------------------------------ C. Số dư phân hệ ↔ sổ cái
    private async Task<ReconCheck> StockValueAsync()
    {
        var moves = await _db.StockMoves.AsNoTracking().Select(m => new { m.Item!.Type, m.Amount }).ToListAsync();
        var gl = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && (l.Account!.Code.StartsWith("152") || l.Account.Code.StartsWith("155") || l.Account.Code.StartsWith("156")))
            .Select(l => new { l.Account!.Code, l.Debit, l.Credit }).ToListAsync();
        var rows = new List<ReconRow>();
        decimal lt = 0, rt = 0;
        foreach (var (code, name, types) in new[] { ("152", "Nguyên vật liệu", new[] { ItemType.Material }), ("155", "Thành phẩm", new[] { ItemType.FinishedGood, ItemType.SemiFinished }), ("156", "Hàng hóa", new[] { ItemType.Merchandise }) })
        {
            var sub = moves.Where(m => types.Contains(m.Type)).Sum(m => m.Amount);
            var led = gl.Where(l => l.Code.StartsWith(code)).Sum(l => l.Debit - l.Credit);
            lt += sub; rt += led;
            if (Math.Abs(sub - led) >= Tolerance) rows.Add(new ReconRow(code, $"Giá trị tồn kho {name}", sub, led, "Thẻ kho (nhập – xuất) so với TK " + code));
        }
        return new ReconCheck
        {
            Code = "C1", Group = "Số dư phân hệ ↔ sổ cái", Title = "Giá trị tồn kho = TK 152 / 155 / 156", Kind = ReconKind.Money, LeftLabel = "Thẻ kho", RightLabel = "Sổ cái",
            Description = "Tổng giá trị nhập – xuất trên thẻ kho theo loại hàng phải bằng số dư các tài khoản hàng tồn kho.", Rows = rows, Checked = 3, LeftTotal = lt, RightTotal = rt,
            Hint = "Lệch thường do bút toán thủ công vào TK kho, đổi cấu hình TK kho sau khi đã có phát sinh, hoặc phiếu kho không sinh bút toán."
        };
    }

    private async Task<ReconCheck> StockQtyAsync()
    {
        var moves = (await _db.StockMoves.AsNoTracking().GroupBy(m => new { m.ItemId, m.WarehouseId }).Select(g => new { g.Key.ItemId, g.Key.WarehouseId, Q = g.Sum(x => x.Quantity) }).ToListAsync())
            .ToDictionary(x => (x.ItemId, x.WarehouseId), x => x.Q);
        var levels = await _db.StockLevels.AsNoTracking().Include(s => s.Item).Include(s => s.Warehouse).ToListAsync();
        var rows = new List<ReconRow>();
        foreach (var l in levels)
        {
            var q = moves.GetValueOrDefault((l.ItemId, l.WarehouseId));
            if (Math.Abs(q - l.Quantity) >= 0.0005m) rows.Add(new ReconRow($"{l.Item?.Code}@{l.Warehouse?.Code}", $"{l.Item?.Name} – {l.Warehouse?.Name}", l.Quantity, q, "Tồn kho hiện tại so với tổng nhập – xuất thẻ kho", $"/Inventory/StockCard?itemId={l.ItemId}"));
        }
        foreach (var kv in moves.Where(k => !levels.Any(l => l.ItemId == k.Key.ItemId && l.WarehouseId == k.Key.WarehouseId) && Math.Abs(k.Value) >= 0.0005m))
            rows.Add(new ReconRow($"#{kv.Key.ItemId}", "Có phát sinh thẻ kho nhưng không có dòng tồn", 0, kv.Value));
        return new ReconCheck
        {
            Code = "C2", Group = "Số dư phân hệ ↔ sổ cái", Title = "Tồn kho số lượng = tổng nhập – xuất thẻ kho", Kind = ReconKind.Qty, LeftLabel = "Tồn kho", RightLabel = "Thẻ kho",
            Description = "Số lượng tồn theo từng mặt hàng × kho phải bằng tổng số lượng nhập – xuất trên thẻ kho.", Rows = rows, Checked = levels.Count,
            LeftTotal = levels.Sum(l => l.Quantity), RightTotal = moves.Values.Sum()
        };
    }

    private async Task<ReconCheck> StockResidualAsync()
    {
        var items = await _db.Items.AsNoTracking().Where(i => i.Type != ItemType.Service).ToListAsync();
        var val = (await _db.StockMoves.AsNoTracking().GroupBy(m => m.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity), V = g.Sum(x => x.Amount) }).ToListAsync()).ToDictionary(x => x.Key);
        var rows = new List<ReconRow>();
        foreach (var i in items)
        {
            if (!val.TryGetValue(i.Id, out var v)) continue;
            // Hết hàng mà sổ cái kho vẫn còn giá trị (hoặc âm) là dư do làm tròn giá bình quân; còn hàng thì giá trị phải xấp xỉ SL × giá bình quân (hàng tính giá theo lô FIFO / đích danh: giá trị theo giá từng lô nên không so với giá bình quân)
            if (v.Q == 0 && Math.Abs(v.V) >= Tolerance)
                rows.Add(new ReconRow(i.Code, $"{i.Name}: tồn bằng 0 nhưng còn giá trị trên sổ kho", v.V, 0, "Dư làm tròn giá vốn bình quân khi xuất hết hàng", $"/Inventory/StockCard?itemId={i.Id}"));
            else if (v.Q > 0 && i.CostMethod == CostMethod.Average && Math.Abs(v.V - v.Q * i.AvgCost) > Math.Max(5m, Math.Abs(v.V) * 0.001m))
                rows.Add(new ReconRow(i.Code, $"{i.Name}: giá trị tồn ≠ SL × giá bình quân", v.V, Math.Round(v.Q * i.AvgCost, 0), $"SL {v.Q:0.###} × giá BQ {i.AvgCost:N2}", $"/Inventory/StockCard?itemId={i.Id}"));
        }
        return new ReconCheck
        {
            Code = "C2b", Group = "Số dư phân hệ ↔ sổ cái", Title = "Giá trị tồn kho không bị dư khi hết hàng", LeftLabel = "Giá trị thẻ kho", RightLabel = "Kỳ vọng",
            Description = "Mặt hàng đã xuất hết (tồn = 0) không được còn giá trị trên TK kho; mặt hàng còn tồn phải có giá trị ≈ số lượng × giá vốn bình quân.",
            Rows = rows, Checked = val.Count, LeftTotal = rows.Sum(r => r.Left), RightTotal = rows.Sum(r => r.Right)
        };
    }

    private async Task<ReconCheck> ReceivableAsync()
    {
        var gl = (await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.Code.StartsWith("131"))
            .GroupBy(l => l.PartnerId).Select(g => new { P = g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToListAsync()).ToDictionary(x => x.P ?? 0, x => x.B);
        var inv = (await _db.Invoices.AsNoTracking().Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued)
            .Select(i => new { i.PartnerId, O = i.Total - i.PaidAmount }).ToListAsync()).GroupBy(x => x.PartnerId ?? 0).ToDictionary(g => g.Key, g => g.Sum(x => x.O));
        var vouchers = await _db.CashVouchers.AsNoTracking().Where(v => v.Status == DocStatus.Posted && v.Purpose == CashPurpose.CustomerReceipt)
            .Select(v => new { v.PartnerId, v.Amount, v.FxDifference, Alloc = v.Allocations.Sum(a => a.Amount) }).ToListAsync();
        // Phần tiền chưa cấn trừ: số tiền phiếu − giá trị ghi sổ đã cấn trừ − chênh lệch tỷ giá đã thực hiện (đã hạch toán 515 / 635, không qua 131 / 331)
        var unalloc = vouchers.GroupBy(v => v.PartnerId ?? 0).ToDictionary(g => g.Key, g => g.Sum(v => v.Amount - v.Alloc - v.FxDifference));
        // Hóa đơn điều chỉnh giảm đã giảm TK 131 nhưng phần hóa đơn gốc đã thanh toán hết chưa cấn trừ được (khách còn được ghi có)
        var creditUnapplied = (await _db.SalesCreditNotes.AsNoTracking().Where(n => n.Status == DocStatus.Posted && n.Unapplied > 0).Select(n => new { n.CustomerId, n.Unapplied }).ToListAsync())
            .GroupBy(x => x.CustomerId).ToDictionary(g => g.Key, g => g.Sum(x => x.Unapplied));
        var names = await _db.Partners.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code + " – " + p.Name);
        var rows = new List<ReconRow>(); decimal lt = 0, rt = 0;
        foreach (var p in gl.Keys.Union(inv.Keys).Union(unalloc.Keys).Union(creditUnapplied.Keys).OrderBy(k => k))
        {
            var sub = inv.GetValueOrDefault(p) - unalloc.GetValueOrDefault(p) - creditUnapplied.GetValueOrDefault(p); // hóa đơn còn nợ − tiền thu chưa cấn trừ − điều chỉnh giảm chưa cấn trừ
            var led = gl.GetValueOrDefault(p);
            lt += sub; rt += led;
            if (Math.Abs(sub - led) >= Tolerance)
                rows.Add(new ReconRow(p == 0 ? "—" : names.GetValueOrDefault(p, "#" + p), "Dư nợ phải thu theo hóa đơn ≠ TK 131", sub, led, $"Hóa đơn còn nợ {inv.GetValueOrDefault(p):N0} − thu chưa cấn trừ {unalloc.GetValueOrDefault(p):N0} − điều chỉnh giảm chưa cấn trừ {creditUnapplied.GetValueOrDefault(p):N0}", p == 0 ? null : $"/Debts/Statement/{p}?kind=ar"));
        }
        return new ReconCheck
        {
            Code = "C3", Group = "Số dư phân hệ ↔ sổ cái", Title = "Công nợ phải thu theo hóa đơn = TK 131", LeftLabel = "Hóa đơn / phiếu thu", RightLabel = "Sổ cái TK 131",
            Description = "Dư nợ từng khách hàng: Σ hóa đơn bán còn nợ − tiền thu chưa cấn trừ phải bằng số dư TK 131 của khách đó.", Rows = rows, Checked = gl.Count, LeftTotal = lt, RightTotal = rt,
            Hint = "Lệch thường do bút toán thủ công vào TK 131, hóa đơn nhập tay không qua phát hành, hoặc điều chỉnh công nợ không qua hóa đơn."
        };
    }

    private async Task<ReconCheck> PayableAsync()
    {
        var gl = (await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.Code.StartsWith("331"))
            .GroupBy(l => l.PartnerId).Select(g => new { P = g.Key, B = g.Sum(x => x.Credit - x.Debit) }).ToListAsync()).ToDictionary(x => x.P ?? 0, x => x.B);
        var inv = (await _db.Invoices.AsNoTracking().Where(i => i.Direction == InvoiceDirection.Purchase && i.Status == InvoiceStatus.Issued)
            .Select(i => new { i.PartnerId, O = i.Total - i.PaidAmount }).ToListAsync()).GroupBy(x => x.PartnerId ?? 0).ToDictionary(g => g.Key, g => g.Sum(x => x.O));
        // Hàng đã nhập kho chưa có hóa đơn: đã ghi Có 331 lúc nhập kho
        var pols = await _db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrder!.Status != PoStatus.Cancelled && l.ReceivedQty - l.ReturnedQty > l.InvoicedQty)
            .Select(l => new { l.PurchaseOrder!.VendorId, V = (l.ReceivedQty - l.ReturnedQty - l.InvoicedQty) * l.UnitPrice }).ToListAsync();
        var grni = pols.GroupBy(x => x.VendorId ?? 0).ToDictionary(g => g.Key, g => g.Sum(x => Math.Round(x.V, 0)));
        var vouchers = await _db.CashVouchers.AsNoTracking().Where(v => v.Status == DocStatus.Posted && v.Purpose == CashPurpose.VendorPayment)
            .Select(v => new { v.PartnerId, v.Amount, v.FxDifference, Alloc = v.Allocations.Sum(a => a.Amount) }).ToListAsync();
        // Phần tiền chưa cấn trừ: số tiền phiếu − giá trị ghi sổ đã cấn trừ − chênh lệch tỷ giá đã thực hiện (đã hạch toán 515 / 635, không qua 131 / 331)
        var unalloc = vouchers.GroupBy(v => v.PartnerId ?? 0).ToDictionary(g => g.Key, g => g.Sum(v => v.Amount - v.Alloc - v.FxDifference));
        // Giá trị trả hàng đã lập hóa đơn nhưng chưa cấn trừ được vào hóa đơn nào (nhà cung cấp còn nợ lại)
        var retUnapplied = (await _db.PurchaseReturns.AsNoTracking().Where(r => r.Unapplied > 0).Select(r => new { r.PurchaseOrderLine!.PurchaseOrder!.VendorId, r.Unapplied }).ToListAsync())
            .GroupBy(x => x.VendorId ?? 0).ToDictionary(g => g.Key, g => g.Sum(x => x.Unapplied));
        var names = await _db.Partners.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code + " – " + p.Name);
        var rows = new List<ReconRow>(); decimal lt = 0, rt = 0;
        foreach (var p in gl.Keys.Union(inv.Keys).Union(grni.Keys).Union(unalloc.Keys).Union(retUnapplied.Keys).OrderBy(k => k))
        {
            var sub = inv.GetValueOrDefault(p) + grni.GetValueOrDefault(p) - unalloc.GetValueOrDefault(p) - retUnapplied.GetValueOrDefault(p);
            var led = gl.GetValueOrDefault(p);
            lt += sub; rt += led;
            if (Math.Abs(sub - led) >= Tolerance)
                rows.Add(new ReconRow(p == 0 ? "—" : names.GetValueOrDefault(p, "#" + p), "Dư nợ phải trả theo hóa đơn ≠ TK 331", sub, led,
                    $"Hóa đơn còn nợ {inv.GetValueOrDefault(p):N0} + hàng nhập chưa có hóa đơn {grni.GetValueOrDefault(p):N0} − chi chưa cấn trừ {unalloc.GetValueOrDefault(p):N0} − trả hàng chưa cấn trừ {retUnapplied.GetValueOrDefault(p):N0}", p == 0 ? null : $"/Debts/Statement/{p}?kind=ap"));
        }
        return new ReconCheck
        {
            Code = "C4", Group = "Số dư phân hệ ↔ sổ cái", Title = "Công nợ phải trả theo hóa đơn = TK 331", LeftLabel = "Hóa đơn / phiếu chi / hàng nhập", RightLabel = "Sổ cái TK 331",
            Description = "Dư nợ từng nhà cung cấp: Σ hóa đơn mua còn nợ + hàng đã nhập kho chưa có hóa đơn − tiền chi chưa cấn trừ phải bằng số dư TK 331.", Rows = rows, Checked = gl.Count, LeftTotal = lt, RightTotal = rt,
            Hint = "Công nợ phát sinh ngoài hóa đơn (bút toán thủ công vào TK 331, điều chỉnh giảm không qua trả hàng / cấn trừ) làm sổ cái lệch với sổ chi tiết."
        };
    }

    private async Task<ReconCheck> CashAsync()
    {
        var accounts = await _db.CashAccounts.AsNoTracking().Include(c => c.Account).ToListAsync();
        var vs = await _db.CashVouchers.AsNoTracking().Where(v => v.Status == DocStatus.Posted).ToListAsync();
        var gl = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Source == JournalSource.Cash).Select(l => new { l.AccountId, l.Debit, l.Credit }).ToListAsync();
        var rows = new List<ReconRow>(); decimal lt = 0, rt = 0;
        foreach (var a in accounts)
        {
            var sub = vs.Where(v => v.CashAccountId == a.Id).Sum(v => v.Type == CashVoucherType.Receipt ? v.Amount : -v.Amount)
                    + vs.Where(v => v.ToCashAccountId == a.Id && v.Type == CashVoucherType.Transfer).Sum(v => v.Amount);
            var led = gl.Where(l => l.AccountId == a.AccountId).Sum(l => l.Debit - l.Credit);
            lt += sub; rt += led;
            if (Math.Abs(sub - led) >= Tolerance) rows.Add(new ReconRow(a.Code, $"Phát sinh phiếu thu / chi của {a.Name}", sub, led, $"So với phát sinh TK {a.Account?.Code} từ nghiệp vụ tiền", $"/Cash/Book/{a.Id}"));
        }
        return new ReconCheck
        {
            Code = "C5", Group = "Số dư phân hệ ↔ sổ cái", Title = "Phát sinh phiếu thu / chi = phát sinh TK tiền", LeftLabel = "Phiếu thu / chi", RightLabel = "Sổ cái",
            Description = "Với từng quỹ / tài khoản ngân hàng: Σ phiếu thu − Σ phiếu chi ± chuyển nội bộ phải bằng phát sinh Nợ − Có của TK tiền từ nghiệp vụ tiền.",
            Rows = rows, Checked = accounts.Count, LeftTotal = lt, RightTotal = rt
        };
    }

    private async Task<ReconCheck> AssetsCostAsync()
    {
        var assets = await _db.FixedAssets.AsNoTracking().Include(a => a.Category).Where(a => a.Status != AssetStatus.Disposed).ToListAsync();
        var accs = await _db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var ids = assets.Select(a => a.Category!.AssetAccountId).Distinct().ToList();
        var gl = (await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && ids.Contains(l.AccountId)).GroupBy(l => l.AccountId).Select(g => new { g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToListAsync()).ToDictionary(x => x.Key, x => x.B);
        var rows = new List<ReconRow>(); decimal lt = 0, rt = 0;
        foreach (var g in assets.GroupBy(a => a.Category!.AssetAccountId))
        {
            var sub = g.Sum(a => a.OriginalCost); var led = gl.GetValueOrDefault(g.Key); lt += sub; rt += led;
            if (Math.Abs(sub - led) >= Tolerance) rows.Add(new ReconRow(accs[g.Key].Code, $"Nguyên giá TSCĐ – {accs[g.Key].Name}", sub, led, "Σ nguyên giá tài sản còn sử dụng so với số dư tài khoản", "/Assets"));
        }
        return new ReconCheck
        {
            Code = "C6", Group = "Số dư phân hệ ↔ sổ cái", Title = "Nguyên giá TSCĐ = TK 211", LeftLabel = "Sổ TSCĐ", RightLabel = "Sổ cái",
            Description = "Tổng nguyên giá tài sản còn sử dụng theo từng tài khoản nguyên giá phải bằng số dư tài khoản đó.", Rows = rows, Checked = assets.Count, LeftTotal = lt, RightTotal = rt
        };
    }

    private async Task<ReconCheck> AssetsDepAsync()
    {
        var assets = await _db.FixedAssets.AsNoTracking().Include(a => a.Category).Where(a => a.Status != AssetStatus.Disposed).ToListAsync();
        var accs = await _db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var ids = assets.Select(a => a.Category!.DepreciationAccountId).Distinct().ToList();
        var gl = (await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && ids.Contains(l.AccountId)).GroupBy(l => l.AccountId).Select(g => new { g.Key, B = g.Sum(x => x.Credit - x.Debit) }).ToListAsync()).ToDictionary(x => x.Key, x => x.B);
        var rows = new List<ReconRow>(); decimal lt = 0, rt = 0;
        foreach (var g in assets.GroupBy(a => a.Category!.DepreciationAccountId))
        {
            var sub = g.Sum(a => a.AccumulatedDepreciation); var led = gl.GetValueOrDefault(g.Key); lt += sub; rt += led;
            if (Math.Abs(sub - led) >= Tolerance) rows.Add(new ReconRow(accs[g.Key].Code, $"Hao mòn lũy kế – {accs[g.Key].Name}", sub, led, "Σ hao mòn lũy kế tài sản còn sử dụng so với số dư tài khoản", "/Assets"));
        }
        return new ReconCheck
        {
            Code = "C7", Group = "Số dư phân hệ ↔ sổ cái", Title = "Hao mòn lũy kế TSCĐ = TK 214", LeftLabel = "Sổ TSCĐ", RightLabel = "Sổ cái",
            Description = "Tổng hao mòn lũy kế (đầu kỳ + khấu hao đã trích) phải bằng số dư có của các tài khoản hao mòn.", Rows = rows, Checked = assets.Count, LeftTotal = lt, RightTotal = rt
        };
    }

    private async Task<ReconCheck> WipAsync()
    {
        var orders = await _db.ProductionOrders.AsNoTracking().Where(p => p.Status != ProductionStatus.Cancelled && p.Status != ProductionStatus.Draft && p.Status != ProductionStatus.Released && p.Status != ProductionStatus.PendingApproval && p.Status != ProductionStatus.Approved).ToListAsync();
        var docs = await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.Production && d.Status == DocStatus.Posted)
            .Select(d => new { d.OriginId, d.Type, Amt = d.Lines.Sum(l => l.Amount) }).ToListAsync();
        var rows = new List<ReconRow>(); decimal sub = 0;
        foreach (var o in orders)
        {
            var issued = docs.Where(d => d.OriginId == o.Id && d.Type == InvDocType.Issue).Sum(d => d.Amt);
            var received = docs.Where(d => d.OriginId == o.Id && d.Type == InvDocType.Receipt).Sum(d => d.Amt);
            var labor = o.LaborCost + o.OverheadCost;   // gồm phần chi phí SXC đã phân bổ cho lệnh đang sản xuất
            var rest = issued + labor - received;
            sub += rest;
            // Lệnh đã hoàn thành phải kết chuyển hết chi phí sang thành phẩm
            if (o.Status is ProductionStatus.Completed or ProductionStatus.Closed && Math.Abs(rest) >= Tolerance)
                rows.Add(new ReconRow(o.Number, "Lệnh đã hoàn thành còn dư chi phí dở dang (chi phí đầu vào ≠ giá trị nhập kho thành phẩm)", issued + labor, received, "Chênh lệch do làm tròn giá thành đơn vị", $"/Production/Details/{o.Id}"));
        }
        // Gia công ngoài: NVL đã giao + phí gia công đã ghi sổ hóa đơn − hàng đã nhận về
        var osDocs = await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.Outsource && d.Status == DocStatus.Posted).Select(d => new { d.Type, Amt = d.Lines.Sum(l => l.Amount) }).ToListAsync();
        var osFee = await _db.OutsourceReceipts.AsNoTracking().Where(r => r.Invoice != null && r.Invoice.Status == InvoiceStatus.Issued).SumAsync(r => (decimal?)r.FeeAmount) ?? 0;
        sub += osDocs.Where(d => d.Type == InvDocType.Issue).Sum(d => d.Amt) + osFee - osDocs.Where(d => d.Type == InvDocType.Receipt).Sum(d => d.Amt);
        var gl = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.Code.StartsWith("154")).SumAsync(l => l.Debit - l.Credit);
        if (Math.Abs(sub - gl) >= Tolerance) rows.Add(new ReconRow("154", "Chi phí dở dang theo lệnh sản xuất ≠ số dư TK 154", sub, gl, "Σ (NVL đã xuất + nhân công + SXC − thành phẩm nhập kho) so với TK 154"));
        return new ReconCheck
        {
            Code = "C8", Group = "Số dư phân hệ ↔ sổ cái", Title = "Chi phí sản xuất dở dang = TK 154", LeftLabel = "Lệnh sản xuất", RightLabel = "Sổ cái TK 154",
            Description = "Lệnh hoàn thành phải kết chuyển hết chi phí vào thành phẩm (TK 154 về 0); lệnh đang chạy còn dư bằng chi phí đã tập hợp.", Rows = rows, Checked = orders.Count, LeftTotal = sub, RightTotal = gl
        };
    }

    private async Task<ReconCheck> VatAsync()
    {
        var invs = await _db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Issued && i.Series != "DK").Select(i => new { i.Direction, i.VatAmount }).ToListAsync();
        var glOut = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.Code.StartsWith("3331")).SumAsync(l => l.Credit - l.Debit);
        var glIn = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.Account!.Code.StartsWith("133")).SumAsync(l => l.Debit - l.Credit);
        // Thuế đầu ra của bảng kê đã trừ thuế điều chỉnh giảm (hàng bán bị trả lại / giảm giá)
        var subOut = invs.Where(i => i.Direction == InvoiceDirection.Sales).Sum(i => i.VatAmount) - (await _db.SalesCreditNotes.AsNoTracking().Where(n => n.Status == DocStatus.Posted).SumAsync(n => (decimal?)n.VatAmount) ?? 0);
        // Thuế đầu vào của bảng kê đã trừ thuế hoàn do trả hàng nhà cung cấp (điều chỉnh giảm)
        var subIn = invs.Where(i => i.Direction == InvoiceDirection.Purchase).Sum(i => i.VatAmount) - (await _db.PurchaseReturns.AsNoTracking().SumAsync(r => (decimal?)r.VatAmount) ?? 0);
        var rows = new List<ReconRow>();
        if (Math.Abs(subOut - glOut) >= Tolerance) rows.Add(new ReconRow("3331", "Thuế GTGT đầu ra: bảng kê hóa đơn bán ≠ TK 3331", subOut, glOut, "Σ thuế trên hóa đơn bán đã phát hành"));
        if (Math.Abs(subIn - glIn) >= Tolerance) rows.Add(new ReconRow("133", "Thuế GTGT đầu vào: bảng kê hóa đơn mua ≠ TK 133", subIn, glIn, "Σ thuế trên hóa đơn mua đã ghi sổ"));
        return new ReconCheck
        {
            Code = "C9", Group = "Số dư phân hệ ↔ sổ cái", Title = "Thuế GTGT trên bảng kê = TK 3331 / 133", LeftLabel = "Bảng kê hóa đơn", RightLabel = "Sổ cái",
            Description = "Thuế GTGT đầu ra / đầu vào của hóa đơn đã phát hành phải bằng số dư các tài khoản thuế.", Rows = rows, Checked = invs.Count, LeftTotal = subOut + subIn, RightTotal = glOut + glIn,
            Hint = "Lệch do hóa đơn bị hủy chưa đảo thuế, nộp thuế / khấu trừ bằng bút toán thủ công, hoặc điều chỉnh thuế ngoài hóa đơn (trả hàng, giảm giá)."
        };
    }

    private async Task<ReconCheck> RevenueAsync()
    {
        var sub = await _db.Invoices.AsNoTracking().Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.Series != "DK").SumAsync(i => i.Subtotal);
        var gl = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source != JournalSource.Closing && l.Account!.Code.StartsWith("511")).SumAsync(l => l.Credit - l.Debit);
        var rows = Math.Abs(sub - gl) >= Tolerance ? new List<ReconRow> { new("511", "Doanh thu: Σ hóa đơn bán ≠ phát sinh TK 511", sub, gl, "Không tính bút toán kết chuyển") } : new();
        return new ReconCheck
        {
            Code = "C10", Group = "Số dư phân hệ ↔ sổ cái", Title = "Doanh thu hóa đơn bán = TK 511", LeftLabel = "Hóa đơn bán", RightLabel = "Sổ cái TK 511",
            Description = "Tổng tiền hàng trên các hóa đơn bán đã phát hành phải bằng phát sinh có TK 511 (không kể kết chuyển).", Rows = rows, Checked = 1, LeftTotal = sub, RightTotal = gl
        };
    }

    private async Task<ReconCheck> CogsAsync()
    {
        var docs = await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.Sales && d.Type == InvDocType.Issue && d.Status == DocStatus.Posted).Select(d => d.Lines.Sum(l => l.Amount)).ToListAsync();
        var returned = await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.SalesReturn && d.Type == InvDocType.Receipt && d.Status == DocStatus.Posted).Select(d => d.Lines.Sum(l => l.Amount)).ToListAsync();
        var sub = docs.Sum() - returned.Sum();   // giá vốn xuất bán − giá vốn hàng bán bị trả lại nhập lại kho
        var gl = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source == JournalSource.Inventory && l.Account!.Code.StartsWith("632"))
            .SumAsync(l => l.Debit - l.Credit);
        var rows = Math.Abs(sub - gl) >= Tolerance ? new List<ReconRow> { new("632", "Giá vốn: Σ phiếu xuất bán ≠ phát sinh TK 632 từ nghiệp vụ kho", sub, gl) } : new();
        return new ReconCheck
        {
            Code = "C11", Group = "Số dư phân hệ ↔ sổ cái", Title = "Giá vốn phiếu xuất bán = TK 632", LeftLabel = "Phiếu xuất bán", RightLabel = "Sổ cái TK 632",
            Description = "Tổng giá trị các phiếu xuất kho giao hàng bán (trừ hàng bán bị trả lại nhập lại kho) phải bằng phát sinh Nợ TK 632 từ nghiệp vụ kho.", Rows = rows, Checked = docs.Count, LeftTotal = sub, RightTotal = gl
        };
    }

    // ------------------------------------------------------------------ D. Đơn hàng ↔ chứng từ
    private async Task<ReconCheck> OrderReceivedAsync()
    {
        var docs = (await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.Purchase && d.Type == InvDocType.Receipt && d.Status == DocStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { d.OriginId, l.ItemId, l.Quantity })).ToListAsync()).GroupBy(x => (x.OriginId ?? 0, x.ItemId)).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var lines = await _db.PurchaseOrderLines.AsNoTracking().Include(l => l.PurchaseOrder).Include(l => l.Item).ToListAsync();
        var rows = new List<ReconRow>();
        foreach (var g in lines.GroupBy(l => (l.PurchaseOrderId, l.ItemId)))
        {
            var sub = g.Sum(l => l.ReceivedQty); var q = docs.GetValueOrDefault((g.Key.PurchaseOrderId, g.Key.ItemId));
            if (Math.Abs(sub - q) >= 0.0005m) rows.Add(new ReconRow(g.First().PurchaseOrder!.Number, $"{g.First().Item?.Code}: SL nhận trên đơn ≠ SL phiếu nhập kho", sub, q, null, $"/PurchaseOrders/Details/{g.Key.PurchaseOrderId}"));
        }
        return new ReconCheck { Code = "D1", Group = "Đơn hàng ↔ chứng từ", Title = "Đơn mua: số lượng đã nhận = phiếu nhập kho", Kind = ReconKind.Qty, LeftLabel = "Đơn mua", RightLabel = "Phiếu nhập kho",
            Description = "Số lượng đã nhận trên dòng đơn mua phải bằng tổng số lượng các phiếu nhập kho đã ghi sổ của đơn.", Rows = rows, Checked = lines.Count, LeftTotal = lines.Sum(l => l.ReceivedQty), RightTotal = docs.Values.Sum() };
    }

    private async Task<ReconCheck> OrderDeliveredAsync()
    {
        var docs = (await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.Sales && d.Type == InvDocType.Issue && d.Status == DocStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { d.OriginId, l.ItemId, l.Quantity })).ToListAsync()).GroupBy(x => (x.OriginId ?? 0, x.ItemId)).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var lines = await _db.SalesOrderLines.AsNoTracking().Include(l => l.SalesOrder).Include(l => l.Item).Where(l => l.Item!.Type != ItemType.Service).ToListAsync();
        var rows = new List<ReconRow>();
        foreach (var g in lines.GroupBy(l => (l.SalesOrderId, l.ItemId)))
        {
            var sub = g.Sum(l => l.DeliveredQty); var q = docs.GetValueOrDefault((g.Key.SalesOrderId, g.Key.ItemId));
            if (Math.Abs(sub - q) >= 0.0005m) rows.Add(new ReconRow(g.First().SalesOrder!.Number, $"{g.First().Item?.Code}: SL giao trên đơn ≠ SL phiếu xuất kho", sub, q, null, $"/SalesOrders/Details/{g.Key.SalesOrderId}"));
        }
        return new ReconCheck { Code = "D2", Group = "Đơn hàng ↔ chứng từ", Title = "Đơn bán: số lượng đã giao = phiếu xuất kho", Kind = ReconKind.Qty, LeftLabel = "Đơn bán", RightLabel = "Phiếu xuất kho",
            Description = "Số lượng đã giao trên dòng đơn bán phải bằng tổng số lượng các phiếu xuất kho đã ghi sổ của đơn (không kể dịch vụ).", Rows = rows, Checked = lines.Count, LeftTotal = lines.Sum(l => l.DeliveredQty), RightTotal = docs.Values.Sum() };
    }

    private async Task<ReconCheck> OrderInvoicedAsync()
    {
        var invLines = await _db.InvoiceLines.AsNoTracking().Include(l => l.Invoice).Where(l => l.SourceLineId != null && l.Invoice!.Status != InvoiceStatus.Cancelled).ToListAsync();
        var bySo = invLines.Where(l => l.Invoice!.SalesOrderId != null).GroupBy(l => l.SourceLineId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var byPo = invLines.Where(l => l.Invoice!.PurchaseOrderId != null).GroupBy(l => l.SourceLineId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var rows = new List<ReconRow>(); var n = 0;
        foreach (var l in await _db.SalesOrderLines.AsNoTracking().Include(l => l.SalesOrder).Include(l => l.Item).ToListAsync())
        { n++; var q = bySo.GetValueOrDefault(l.Id); if (Math.Abs(l.InvoicedQty - q) >= 0.0005m) rows.Add(new ReconRow(l.SalesOrder!.Number, $"{l.Item?.Code}: SL đã lập hóa đơn trên đơn bán ≠ hóa đơn", l.InvoicedQty, q, null, $"/SalesOrders/Details/{l.SalesOrderId}")); }
        foreach (var l in await _db.PurchaseOrderLines.AsNoTracking().Include(l => l.PurchaseOrder).Include(l => l.Item).ToListAsync())
        { n++; var q = byPo.GetValueOrDefault(l.Id); if (Math.Abs(l.InvoicedQty - q) >= 0.0005m) rows.Add(new ReconRow(l.PurchaseOrder!.Number, $"{l.Item?.Code}: SL đã lập hóa đơn trên đơn mua ≠ hóa đơn", l.InvoicedQty, q, null, $"/PurchaseOrders/Details/{l.PurchaseOrderId}")); }
        return new ReconCheck { Code = "D3", Group = "Đơn hàng ↔ chứng từ", Title = "Số lượng đã lập hóa đơn khớp dòng hóa đơn", Kind = ReconKind.Qty, LeftLabel = "Đơn hàng", RightLabel = "Hóa đơn",
            Description = "Số lượng đã xuất hóa đơn trên dòng đơn mua / bán phải bằng tổng số lượng các hóa đơn chưa hủy tham chiếu dòng đó.", Rows = rows, Checked = n };
    }

    private async Task<ReconCheck> SalesCreditsAsync()
    {
        var notes = await _db.SalesCreditNotes.AsNoTracking().Include(n => n.Lines).Include(n => n.Offsets).Include(n => n.InventoryDoc).ThenInclude(d => d!.Lines).ToListAsync();
        var posted = notes.Where(n => n.Status == DocStatus.Posted).ToList();
        var jids = posted.Where(n => n.JournalEntryId != null).Select(n => n.JournalEntryId!.Value).ToList();
        var jl = (await _db.JournalLines.AsNoTracking().Include(l => l.Account).Where(l => jids.Contains(l.JournalEntryId)).ToListAsync()).GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.ToList());
        var reversed = (await _db.JournalEntries.AsNoTracking().Where(j => j.ReversalOfId != null).Select(j => j.ReversalOfId!.Value).ToListAsync()).ToHashSet();
        var rows = new List<ReconRow>();
        foreach (var n in notes)
        {
            var url = $"/SalesCreditNotes/Details/{n.Id}";
            if (n.Status == DocStatus.Cancelled)
            {
                if (n.JournalEntryId != null && !reversed.Contains(n.JournalEntryId.Value)) rows.Add(new ReconRow(n.Number, "Chứng từ đã hủy nhưng bút toán chưa được đảo", n.Total, 0, null, url));
                continue;
            }
            if (Math.Abs(n.Total - (n.Subtotal + n.VatAmount)) >= Tolerance || Math.Abs(n.Subtotal - n.Lines.Sum(l => l.Amount)) >= Tolerance || Math.Abs(n.VatAmount - n.Lines.Sum(l => l.VatAmount)) >= Tolerance)
                rows.Add(new ReconRow(n.Number, "Tổng chứng từ ≠ tổng các dòng", n.Total, n.Lines.Sum(l => l.Amount + l.VatAmount), null, url));
            if (n.JournalEntryId == null || !jl.TryGetValue(n.JournalEntryId.Value, out var lines)) { rows.Add(new ReconRow(n.Number, "Chưa có bút toán", n.Total, 0, null, url)); continue; }
            var ar = lines.Where(l => l.Account!.Code.StartsWith("131")).Sum(l => l.Credit - l.Debit);
            var rev = lines.Where(l => l.Account!.Code.StartsWith("521")).Sum(l => l.Debit - l.Credit);
            var vat = lines.Where(l => l.Account!.Code.StartsWith("3331")).Sum(l => l.Debit - l.Credit);
            if (Math.Abs(ar - n.Total) >= Tolerance) rows.Add(new ReconRow(n.Number, "Giảm phải thu trên bút toán ≠ tổng chứng từ", n.Total, ar, null, url));
            else if (Math.Abs(rev - n.Subtotal) >= Tolerance) rows.Add(new ReconRow(n.Number, "Giảm trừ doanh thu trên bút toán ≠ tiền hàng chứng từ", n.Subtotal, rev, null, url));
            else if (Math.Abs(vat - n.VatAmount) >= Tolerance) rows.Add(new ReconRow(n.Number, "Thuế đầu ra trên bút toán ≠ thuế chứng từ", n.VatAmount, vat, null, url));
            var applied = n.Offsets.Sum(o => o.Amount) + n.Unapplied;
            if (Math.Abs(n.Total - applied) >= Tolerance) rows.Add(new ReconRow(n.Number, "Giá trị điều chỉnh ≠ cấn trừ hóa đơn + chưa cấn trừ", n.Total, applied, null, url));
            if (n.Kind == CreditNoteKind.Return)
            {
                var q = n.Lines.Sum(l => l.Quantity); var dq = n.InventoryDoc?.Status == DocStatus.Posted ? n.InventoryDoc.Lines.Sum(l => l.Quantity) : 0;
                if (Math.Abs(q - dq) >= 0.0005m) rows.Add(new ReconRow(n.Number, "Số lượng trả lại ≠ phiếu nhập kho", q, dq, null, url));
            }
        }
        // Tổng giảm trừ doanh thu trên sổ cái (không kể kết chuyển) = Σ chứng từ điều chỉnh
        var subTotal = posted.Sum(n => n.Subtotal);
        var glTotal = await _db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source != JournalSource.Closing && l.Account!.Code.StartsWith("521")).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        if (Math.Abs(subTotal - glTotal) >= Tolerance) rows.Add(new ReconRow("521", "Giảm trừ doanh thu: Σ chứng từ điều chỉnh ≠ phát sinh TK 521", subTotal, glTotal, "Không tính bút toán kết chuyển"));
        // Hóa đơn bị điều chỉnh không vượt giá trị gốc
        var invIds = notes.Select(n => n.InvoiceId).Distinct().ToList();
        var inv = await _db.Invoices.AsNoTracking().Where(i => invIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        foreach (var g in posted.GroupBy(n => n.InvoiceId))
            if (inv.TryGetValue(g.Key, out var i) && g.Sum(n => n.Total) - i.Total >= Tolerance) rows.Add(new ReconRow(i.InvoiceNo ?? i.Number, "Tổng điều chỉnh giảm vượt giá trị hóa đơn gốc", g.Sum(n => n.Total), i.Total, null, $"/Invoices/Details/{i.Id}"));
        return new ReconCheck
        {
            Code = "D6", Group = "Đơn hàng ↔ chứng từ", Title = "Hóa đơn điều chỉnh giảm bán hàng: kho – doanh thu – thuế – công nợ khớp nhau", Kind = ReconKind.Money, LeftLabel = "Chứng từ điều chỉnh", RightLabel = "Sổ cái TK 521",
            Description = "Mỗi hóa đơn điều chỉnh giảm phải có bút toán khớp (giảm phải thu, giảm trừ doanh thu, thuế đầu ra), phiếu nhập kho đúng số lượng khi trả hàng, cấn trừ đủ vào hóa đơn gốc, và tổng giảm trừ doanh thu khớp TK 521.",
            Rows = rows, Checked = notes.Count, LeftTotal = subTotal, RightTotal = glTotal
        };
    }

    private async Task<ReconCheck> VendorReturnsAsync()
    {
        var ncrs = await _db.QcNonconformances.AsNoTracking().Include(n => n.InventoryDoc).ThenInclude(d => d!.Lines)
            .Where(n => n.Disposition == NcrDisposition.ReturnToVendor && n.InventoryDocId != null).ToListAsync();
        var rets = (await _db.PurchaseReturns.AsNoTracking().Include(r => r.Offsets).ToListAsync()).GroupBy(r => r.NcrId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<ReconRow>();
        foreach (var n in ncrs)
        {
            var rs = rets.GetValueOrDefault(n.Id) ?? new();
            if (rs.Count == 0) { rows.Add(new ReconRow(n.Number, "NCR trả hàng nhà cung cấp chưa gắn dòng đơn mua (công nợ, thuế, hóa đơn chưa được điều chỉnh)", n.Quantity, 0, null, $"/QcNcrs/Details/{n.Id}")); continue; }
            var q = rs.Sum(r => r.Quantity);
            if (Math.Abs(q - n.Quantity) >= 0.0005m) rows.Add(new ReconRow(n.Number, "Số lượng trả theo đơn mua ≠ số lượng trên NCR", q, n.Quantity, null, $"/QcNcrs/Details/{n.Id}"));
            foreach (var r in rs)
            {
                var credit = r.BilledAmount + r.VatAmount;
                var applied = r.Offsets.Sum(o => o.Amount) + r.Unapplied;
                if (Math.Abs(credit - applied) >= Tolerance) rows.Add(new ReconRow(n.Number, "Giá trị trả đã lập hóa đơn ≠ cấn trừ hóa đơn + chưa cấn trừ", credit, applied, null, $"/QcNcrs/Details/{n.Id}"));
            }
        }
        // Mọi giảm trừ công nợ hóa đơn từ trả hàng phải có phiếu trả hàng tương ứng
        var offsetSum = await _db.InvoiceOffsets.AsNoTracking().SumAsync(o => (decimal?)o.Amount) ?? 0;
        return new ReconCheck
        {
            Code = "D5", Group = "Đơn hàng ↔ chứng từ", Title = "Trả hàng nhà cung cấp: kho – đơn mua – công nợ khớp nhau", Kind = ReconKind.Money, LeftLabel = "Giá trị trả đã lập HĐ", RightLabel = "Cấn trừ + chưa cấn trừ",
            Description = "Mỗi NCR trả lại nhà cung cấp phải có phiếu trả hàng theo dòng đơn mua; số lượng khớp NCR; giá trị trả đã có hóa đơn (kèm thuế) phải được cấn trừ vào công nợ hóa đơn hoặc ghi nhận chưa cấn trừ.",
            Rows = rows, Checked = ncrs.Count, LeftTotal = rets.Values.SelectMany(x => x).Sum(r => r.BilledAmount + r.VatAmount), RightTotal = offsetSum + rets.Values.SelectMany(x => x).Sum(r => r.Unapplied)
        };
    }

    private async Task<ReconCheck> ProductionIssuedAsync()
    {
        var docs = (await _db.InventoryDocs.AsNoTracking().Where(d => d.Origin == DocOrigin.Production && (d.Type == InvDocType.Issue || d.Type == InvDocType.Receipt) && d.Status == DocStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { d.OriginId, l.ItemId, Quantity = d.Type == InvDocType.Issue ? l.Quantity : -l.Quantity, Amount = d.Type == InvDocType.Issue ? l.Amount : -l.Amount })).ToListAsync()).GroupBy(x => (x.OriginId ?? 0, x.ItemId)).ToDictionary(g => g.Key, g => (Q: g.Sum(x => x.Quantity), A: g.Sum(x => x.Amount)));
        var lines = await _db.ProductionOrderLines.AsNoTracking().Include(l => l.ProductionOrder).Include(l => l.Item).ToListAsync();
        var rows = new List<ReconRow>();
        foreach (var l in lines)
        {
            var d = docs.GetValueOrDefault((l.ProductionOrderId, l.ItemId));
            if (Math.Abs(l.IssuedQty - d.Q) >= 0.0005m || Math.Abs(l.Cost - d.A) >= Tolerance)
                rows.Add(new ReconRow(l.ProductionOrder!.Number, $"{l.Item?.Code}: NVL đã xuất / giá trị trên lệnh ≠ phiếu xuất kho", l.IssuedQty, d.Q, $"Giá trị lệnh {l.Cost:N0} so với phiếu {d.A:N0}", $"/Production/Details/{l.ProductionOrderId}"));
        }
        return new ReconCheck { Code = "D4", Group = "Đơn hàng ↔ chứng từ", Title = "Lệnh sản xuất: NVL đã xuất = phiếu xuất kho", Kind = ReconKind.Qty, LeftLabel = "Lệnh sản xuất", RightLabel = "Phiếu xuất kho",
            Description = "Số lượng và giá trị NVL đã xuất trên lệnh phải bằng các phiếu xuất kho sản xuất đã ghi sổ.", Rows = rows, Checked = lines.Count, LeftTotal = lines.Sum(l => l.IssuedQty), RightTotal = lines.Sum(l => docs.GetValueOrDefault((l.ProductionOrderId, l.ItemId)).Q) };
    }
}
