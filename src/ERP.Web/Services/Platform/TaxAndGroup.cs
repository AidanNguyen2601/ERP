using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

// =====================================================================
//  THUẾ GTGT (01/GTGT), ĐỐI CHIẾU HÓA ĐƠN, THUẾ TNDN
// =====================================================================
public class TaxService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly SettingsService _set;
    private readonly CurrentUser _user;
    public TaxService(ErpDbContext db, AccountingService acc, SettingsService set, CurrentUser user) { _db = db; _acc = acc; _set = set; _user = user; }

    /// <summary>Kỳ: 1–12 tháng; 13–16 quý 1–4</summary>
    public static (DateTime From, DateTime To) Range(int year, int period) =>
        period <= 12 ? (new DateTime(year, period, 1), new DateTime(year, period, 1).AddMonths(1).AddDays(-1))
                     : (new DateTime(year, (period - 13) * 3 + 1, 1), new DateTime(year, (period - 13) * 3 + 1, 1).AddMonths(3).AddDays(-1));
    public static string PeriodName(int year, int period) => period <= 12 ? $"tháng {period:00}/{year}" : $"quý {period - 12}/{year}";
    private static int Key(int year, int period) => year * 100 + (period <= 12 ? period : (period - 12) * 3);

    /// <summary>Tính các chỉ tiêu tờ khai 01/GTGT từ hóa đơn đã phát hành, hóa đơn điều chỉnh giảm và hàng mua trả lại trong kỳ</summary>
    public async Task<VatDeclaration> ComputeAsync(int year, int period, decimal c37 = 0, decimal c38 = 0)
    {
        var (f, t) = Range(year, period);
        var inv = await _db.Invoices.Include(i => i.Lines).Where(i => i.Status == InvoiceStatus.Issued && i.Date >= f && i.Date <= t && i.Series != "DK" && i.Series != AssetService.AssetInvoiceSeries).ToListAsync();
        var credits = await _db.SalesCreditLines.Where(l => l.SalesCreditNote!.Status == DocStatus.Posted && l.SalesCreditNote.Date >= f && l.SalesCreditNote.Date <= t).ToListAsync();
        var returns = await _db.PurchaseReturns.Where(r => r.Date >= f && r.Date <= t).SumAsync(r => (decimal?)r.VatAmount) ?? 0;
        var sales = inv.Where(i => i.Direction == InvoiceDirection.Sales).SelectMany(i => i.Lines).Select(l => (VatRate: l.VatRate, Amount: l.Amount, VatAmount: l.VatAmount))
            .Concat(credits.Select(c => (VatRate: c.VatRate, Amount: -c.Amount, VatAmount: -c.VatAmount))).ToList();
        var purchase = inv.Where(i => i.Direction == InvoiceDirection.Purchase).ToList();
        // Kỳ trước: số còn được khấu trừ chuyển sang [22] = [43] của tờ khai gần nhất trước kỳ này
        var prev = (await _db.VatDeclarations.ToListAsync()).Where(d => Key(d.Year, d.Period) < Key(year, period)).OrderByDescending(d => Key(d.Year, d.Period)).FirstOrDefault();
        var d = new VatDeclaration
        {
            Year = year, Period = period, C22 = prev?.C43 ?? 0,
            C23 = purchase.Sum(i => i.Subtotal), C24 = purchase.Sum(i => i.VatAmount) - returns,
            C29 = sales.Where(x => x.VatRate == 0).Sum(x => x.Amount),
            C30 = sales.Where(x => x.VatRate == 5).Sum(x => x.Amount), C31 = sales.Where(x => x.VatRate == 5).Sum(x => x.VatAmount),
            C32 = sales.Where(x => x.VatRate >= 8).Sum(x => x.Amount), C33 = sales.Where(x => x.VatRate >= 8).Sum(x => x.VatAmount),
            C37 = c37, C38 = c38, CreatedBy = _user.Name
        };
        d.C25 = d.C24;
        var c36 = d.C31 + d.C33 - d.C25;
        var net = c36 - d.C22 + d.C37 - d.C38;
        d.C40a = Math.Max(0, net); d.C41 = Math.Max(0, -net); d.C43 = d.C41;
        return d;
    }

    /// <summary>Lưu tờ khai; tùy chọn hạch toán bù trừ thuế đầu vào với đầu ra (Nợ 33311 / Có 1331)</summary>
    public async Task<VatDeclaration> SaveAsync(VatDeclaration d, bool postOffset)
    {
        var old = await _db.VatDeclarations.FirstOrDefaultAsync(x => x.Year == d.Year && x.Period == d.Period);
        if (old?.JournalEntryId != null) throw new BusinessException($"Tờ khai {PeriodName(d.Year, d.Period)} đã hạch toán khấu trừ – hủy bút toán trước khi lập lại.");
        if (old != null) _db.VatDeclarations.Remove(old);
        _db.VatDeclarations.Add(d);
        await _db.SaveChangesAsync();
        if (postOffset)
        {
            var output = d.C31 + d.C33; var offset = Math.Min(output, d.C25 + d.C22);
            if (offset > 0)
            {
                var (_, t) = Range(d.Year, d.Period);
                var je = await _acc.PostAsync(t, $"Khấu trừ thuế GTGT đầu vào với đầu ra {PeriodName(d.Year, d.Period)}", JournalSource.Tax, $"KTGTGT{d.Period:00}{d.Year % 100:00}", d.Id, new[]
                {
                    new JLine(await _acc.RuleAsync(PostingCatalog.SiVatOut), offset, 0), new JLine(await _acc.RuleAsync(PostingCatalog.PiVatIn), 0, offset)
                });
                d.JournalEntryId = je.Id; await _db.SaveChangesAsync();
            }
        }
        return d;
    }

    // ---------------- Đối chiếu với danh sách hóa đơn trên cổng thuế
    public record MatchRow(string Status, InvoiceDirection Direction, string? Series, string InvoiceNo, DateTime? Date, string? TaxCode, string? Partner, decimal? SysAmount, decimal? SysVat, decimal? PortalAmount, decimal? PortalVat, int? InvoiceId);

    public async Task<List<MatchRow>> MatchAsync(DateTime from, DateTime to)
    {
        var sys = await _db.Invoices.Include(i => i.Partner).Where(i => i.Status == InvoiceStatus.Issued && i.Date >= from && i.Date <= to && i.Series != "DK" && i.Series != AssetService.AssetInvoiceSeries).ToListAsync();
        var portal = await _db.TaxPortalInvoices.Where(p => p.Date >= from && p.Date <= to).ToListAsync();
        static string K(InvoiceDirection d, string? tax, string? no) => $"{(int)d}|{(tax ?? "").Trim()}|{(no ?? "").TrimStart('0')}";
        var sysByKey = sys.GroupBy(i => K(i.Direction, i.Partner?.TaxCode, i.InvoiceNo)).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<MatchRow>(); var seen = new HashSet<string>();
        foreach (var p in portal)
        {
            var k = K(p.Direction, p.PartnerTaxCode, p.InvoiceNo); seen.Add(k);
            if (sysByKey.TryGetValue(k, out var i))
                rows.Add(new MatchRow(i.Subtotal == p.Subtotal && i.VatAmount == p.VatAmount ? "Khớp" : "Lệch số tiền", p.Direction, p.Series, p.InvoiceNo, p.Date, p.PartnerTaxCode, i.Partner?.Name ?? p.PartnerName, i.Subtotal, i.VatAmount, p.Subtotal, p.VatAmount, i.Id));
            else rows.Add(new MatchRow("Có trên cổng thuế, chưa ghi sổ", p.Direction, p.Series, p.InvoiceNo, p.Date, p.PartnerTaxCode, p.PartnerName, null, null, p.Subtotal, p.VatAmount, null));
        }
        foreach (var (k, i) in sysByKey.Where(x => !seen.Contains(x.Key)))
            rows.Add(new MatchRow(portal.Any() ? "Có trên sổ, không có trên cổng thuế" : "Chưa có dữ liệu cổng thuế", i.Direction, i.Series, i.InvoiceNo ?? i.Number, i.Date, i.Partner?.TaxCode, i.Partner?.Name, i.Subtotal, i.VatAmount, null, null, i.Id));
        return rows.OrderBy(r => r.Status == "Khớp").ThenBy(r => r.Direction).ThenBy(r => r.Date).ToList();
    }

    // ---------------- Thuế TNDN
    public async Task<decimal> AccountingProfitAsync(DateTime from, DateTime to)
    {
        var rows = await _db.JournalLines.Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source != JournalSource.Closing && l.JournalEntry.Date >= from && l.JournalEntry.Date <= to
            && (l.Account!.Code.StartsWith("5") || l.Account.Code.StartsWith("6") || l.Account.Code.StartsWith("7") || l.Account.Code.StartsWith("8")) && !l.Account.Code.StartsWith("821"))
            .SumAsync(l => (decimal?)(l.Credit - l.Debit)) ?? 0;
        return rows;
    }

    public async Task<CitDeclaration> ComputeCitAsync(int year, int quarter, decimal nonDeductible, decimal exempt, decimal loss)
    {
        var (f, t) = quarter == 0 ? (new DateTime(year, 1, 1), new DateTime(year, 12, 31)) : (new DateTime(year, (quarter - 1) * 3 + 1, 1), new DateTime(year, (quarter - 1) * 3 + 1, 1).AddMonths(3).AddDays(-1));
        var rate = await _set.DecAsync("Cit.Rate", 20);
        var d = new CitDeclaration { Year = year, Quarter = quarter, AccountingProfit = await AccountingProfitAsync(f, t), NonDeductible = nonDeductible, ExemptIncome = exempt, LossCarried = loss, Rate = rate, CreatedBy = _user.Name };
        d.TaxableIncome = Math.Max(0, d.AccountingProfit + d.NonDeductible - d.ExemptIncome - d.LossCarried);
        d.Tax = Math.Round(d.TaxableIncome * rate / 100, 0);
        d.PaidEarlier = quarter == 0 ? await _db.CitDeclarations.Where(x => x.Year == year && x.Quarter > 0).SumAsync(x => (decimal?)x.Tax) ?? 0 : 0;
        d.Payable = d.Tax - d.PaidEarlier;
        return d;
    }

    public async Task<CitDeclaration> SaveCitAsync(CitDeclaration d, bool post)
    {
        var old = await _db.CitDeclarations.FirstOrDefaultAsync(x => x.Year == d.Year && x.Quarter == d.Quarter);
        if (old?.JournalEntryId != null) throw new BusinessException("Kỳ này đã hạch toán thuế TNDN – đảo bút toán trước khi lập lại.");
        if (old != null) _db.CitDeclarations.Remove(old);
        _db.CitDeclarations.Add(d); await _db.SaveChangesAsync();
        if (post && d.Payable != 0)
        {
            var date = d.Quarter == 0 ? new DateTime(d.Year, 12, 31) : new DateTime(d.Year, d.Quarter * 3, 1).AddMonths(1).AddDays(-1);
            var e = await _acc.IdAsync("821"); var p = await _acc.IdAsync("3334");
            var amt = Math.Abs(d.Payable);
            var lines = d.Payable > 0 ? new[] { new JLine(e, amt, 0), new JLine(p, 0, amt) } : new[] { new JLine(p, amt, 0), new JLine(e, 0, amt) };
            var je = await _acc.PostAsync(date, $"Thuế TNDN {(d.Quarter == 0 ? "quyết toán năm" : "tạm tính quý " + d.Quarter)} {d.Year}", JournalSource.Tax, $"TNDN{d.Quarter}{d.Year % 100:00}", d.Id, lines);
            d.JournalEntryId = je.Id; await _db.SaveChangesAsync();
        }
        return d;
    }
}

// =====================================================================
//  HỢP NHẤT BÁO CÁO NHIỀU PHÁP NHÂN
// =====================================================================
public class ConsolidationRow
{
    public string Code { get; set; } = ""; public string Name { get; set; } = "";
    public Dictionary<int, decimal> ByCompany { get; } = new();
    public decimal Elimination { get; set; }
    public decimal Total => ByCompany.Values.Sum();
    public decimal Consolidated => Total + Elimination;
}

public class ConsolidationResult
{
    public List<Company> Companies { get; set; } = new();
    public List<ConsolidationRow> Rows { get; set; } = new();
    public List<(string Desc, string Debit, string Credit, decimal Amount)> Eliminations { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public decimal Sum(string prefix, Func<ConsolidationRow, decimal> f) => Rows.Where(r => r.Code.StartsWith(prefix)).Sum(f);
}

public class ConsolidationService
{
    private readonly ErpDbContext _db;
    public ConsolidationService(ErpDbContext db) => _db = db;

    /// <summary>
    /// Hợp nhất: cộng số dư từng TK (tài sản / nguồn vốn đến ngày; doanh thu / chi phí trong kỳ) của các công ty, loại trừ giao dịch nội bộ:
    /// công nợ nội bộ (131 ↔ 331 với đối tác là công ty trong tập đoàn) và doanh thu bán nội bộ (511 ↔ 632).
    /// </summary>
    public async Task<ConsolidationResult> RunAsync(DateTime from, DateTime to, int[] companyIds)
    {
        var companies = await _db.Companies.Include(c => c.Branches).Where(c => companyIds.Contains(c.Id)).OrderByDescending(c => c.IsParent).ThenBy(c => c.Code).ToListAsync();
        var res = new ConsolidationResult { Companies = companies };
        var accounts = await _db.Accounts.ToDictionaryAsync(a => a.Id);
        var rows = new Dictionary<string, ConsolidationRow>();
        var groupPartners = (await _db.Partners.Where(p => p.GroupCompanyId != null && companyIds.Contains(p.GroupCompanyId.Value)).Select(p => new { p.Id, p.GroupCompanyId }).ToListAsync()).ToDictionary(p => p.Id, p => p.GroupCompanyId!.Value);
        decimal icAr = 0, icAp = 0, icRevenue = 0;
        var icExpense = new Dictionary<string, decimal>();
        foreach (var c in companies)
        {
            var branches = c.Branches.Select(b => (int?)b.Id).ToList();
            var lines = await _db.JournalLines.IgnoreQueryFilters().Where(l => l.JournalEntry!.Status == DocStatus.Posted && branches.Contains(l.JournalEntry.BranchId) && l.JournalEntry.Date <= to)
                .Select(l => new { l.AccountId, l.PartnerId, l.Debit, l.Credit, l.JournalEntry!.Date, l.JournalEntry.Source }).ToListAsync();
            foreach (var g in lines.GroupBy(l => l.AccountId))
            {
                var a = accounts[g.Key];
                var pl = a.Code[0] is '5' or '6' or '7' or '8' or '9';
                var set = pl ? g.Where(x => x.Date >= from && x.Source != JournalSource.Closing) : g;
                var bal = set.Sum(x => x.Debit - x.Credit);
                if (bal == 0) continue;
                if (!rows.TryGetValue(a.Code, out var r)) rows[a.Code] = r = new ConsolidationRow { Code = a.Code, Name = a.Name };
                r.ByCompany[c.Id] = r.ByCompany.GetValueOrDefault(c.Id) + bal;
            }
            // Công nợ nội bộ còn dư cuối kỳ
            icAr += lines.Where(l => accounts[l.AccountId].Code.StartsWith("131") && l.PartnerId != null && groupPartners.TryGetValue(l.PartnerId.Value, out var gc) && gc != c.Id).Sum(l => l.Debit - l.Credit);
            icAp += lines.Where(l => accounts[l.AccountId].Code.StartsWith("331") && l.PartnerId != null && groupPartners.TryGetValue(l.PartnerId.Value, out var gc) && gc != c.Id).Sum(l => l.Credit - l.Debit);
            // Doanh thu bán cho công ty trong tập đoàn trong kỳ
            icRevenue += await _db.Invoices.IgnoreQueryFilters().Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && branches.Contains(i.BranchId)
                && i.Date >= from && i.Date <= to && i.PartnerId != null && groupPartners.Keys.Contains(i.PartnerId.Value)).SumAsync(i => (decimal?)i.Subtotal) ?? 0;
            // Bên mua nội bộ: chi phí dịch vụ ghi nhận theo TK trên dòng hóa đơn mua (hàng hóa nhập kho → giá vốn khi bán ra)
            var icBuy = await _db.InvoiceLines.IgnoreQueryFilters().Where(l => l.Invoice!.Direction == InvoiceDirection.Purchase && l.Invoice.Status == InvoiceStatus.Issued && branches.Contains(l.Invoice.BranchId)
                && l.Invoice.Date >= from && l.Invoice.Date <= to && l.Invoice.PartnerId != null && groupPartners.Keys.Contains(l.Invoice.PartnerId.Value) && l.AccountId != null)
                .GroupBy(l => l.AccountId!.Value).Select(g => new { g.Key, A = g.Sum(x => x.Amount) }).ToListAsync();
            foreach (var b in icBuy) icExpense[accounts[b.Key].Code] = icExpense.GetValueOrDefault(accounts[b.Key].Code) + b.A;
        }
        void Elim(string desc, string dr, string cr, decimal amt)
        {
            if (amt == 0) return;
            res.Eliminations.Add((desc, dr, cr, amt));
            Row(dr).Elimination += amt; Row(cr).Elimination -= amt;
        }
        ConsolidationRow Row(string code)
        {
            var key = rows.Keys.Where(k => k.StartsWith(code)).OrderBy(k => k.Length).FirstOrDefault() ?? code;
            if (!rows.TryGetValue(key, out var r)) rows[key] = r = new ConsolidationRow { Code = key, Name = accounts.Values.FirstOrDefault(a => a.Code == key)?.Name ?? key };
            return r;
        }
        var icDebt = Math.Min(icAr, icAp);
        Elim("Loại trừ công nợ phải thu / phải trả giữa các công ty trong tập đoàn", "331", "131", icDebt);
        if (icAr != icAp) res.Notes.Add($"Công nợ nội bộ chưa khớp: phải thu {icAr.ToMoney()} ≠ phải trả {icAp.ToMoney()} (chênh {(icAr - icAp).ToMoney()}) – đối chiếu công nợ giữa các công ty trước khi hợp nhất.");
        var revKey = rows.Keys.Where(k => k.StartsWith("511")).OrderBy(k => k.Length).FirstOrDefault() ?? "511";
        // Doanh thu nội bộ loại trừ với chi phí tương ứng bên mua (dịch vụ: TK chi phí trên hóa đơn mua; phần còn lại: giá vốn hàng mua nội bộ đã bán ra)
        var left = icRevenue;
        foreach (var (code, amt) in icExpense.OrderByDescending(x => x.Value))
        {
            var part = Math.Min(left, amt);
            if (part <= 0) break;
            Elim($"Loại trừ doanh thu nội bộ với chi phí TK {code} bên mua", revKey, code, part);
            left -= part;
        }
        Elim("Loại trừ doanh thu bán hàng nội bộ và giá vốn tương ứng", revKey, rows.Keys.Where(k => k.StartsWith("632")).OrderBy(k => k.Length).FirstOrDefault() ?? "632", left);
        if (icExpense.Values.Sum() > icRevenue) res.Notes.Add($"Chi phí mua nội bộ ({icExpense.Values.Sum().ToMoney()}) lớn hơn doanh thu bán nội bộ ({icRevenue.ToMoney()}) – kiểm tra hóa đơn nội bộ hai bên.");
        if (icRevenue > 0) res.Notes.Add("Chưa loại trừ lãi chưa thực hiện trong hàng tồn kho mua nội bộ còn tồn cuối kỳ (cần số liệu tồn kho mua nội bộ chi tiết).");
        if (companies.Any(c => !c.IsParent && c.OwnershipPct < 100)) res.Notes.Add("Có công ty con sở hữu dưới 100%: chưa tách lợi ích cổ đông không kiểm soát.");
        res.Notes.Add("Chưa loại trừ khoản đầu tư của công ty mẹ vào công ty con với vốn chủ sở hữu tương ứng (TK 221 ↔ 411).");
        res.Rows = rows.Values.OrderBy(r => r.Code).ToList();
        return res;
    }
}

// =====================================================================
//  BẢNG CÂN ĐỐI THEO CHUẨN MỰC KHÁC (ánh xạ tài khoản)
// =====================================================================
public record MappedRow(string Code, string Name, decimal Opening, decimal Debit, decimal Credit, decimal Closing, string Sources);

public class StandardMappingService
{
    private readonly ErpDbContext _db;
    public StandardMappingService(ErpDbContext db) => _db = db;

    public async Task<(List<MappedRow> Rows, List<string> Unmapped)> TrialBalanceAsync(string standard, DateTime from, DateTime to)
    {
        var maps = await _db.AccountMappings.Where(m => m.Standard == standard).ToListAsync();
        var accounts = await _db.Accounts.Where(a => a.IsPostable).ToListAsync();
        var lines = await _db.JournalLines.Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Date <= to)
            .Select(l => new { l.AccountId, l.Debit, l.Credit, l.JournalEntry!.Date }).ToListAsync();
        var by = lines.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        var agg = new Dictionary<string, (string Name, decimal O, decimal D, decimal C, SortedSet<string> Src)>();
        var unmapped = new List<string>();
        foreach (var a in accounts)
        {
            if (!by.TryGetValue(a.Id, out var ls)) continue;
            var m = maps.Where(x => a.Code.StartsWith(x.FromPrefix)).OrderByDescending(x => x.FromPrefix.Length).FirstOrDefault();
            var o = ls.Where(x => x.Date < from).Sum(x => x.Debit - x.Credit); var d = ls.Where(x => x.Date >= from).Sum(x => x.Debit); var c = ls.Where(x => x.Date >= from).Sum(x => x.Credit);
            if (o == 0 && d == 0 && c == 0) continue;
            if (m == null) { unmapped.Add(a.Code); m = new AccountMapping { ToCode = a.Code, ToName = a.Name + " (chưa ánh xạ)", Sign = 1 }; }
            var cur = agg.GetValueOrDefault(m.ToCode, (m.ToName, 0, 0, 0, new SortedSet<string>()));
            cur.Src.Add(a.Code);
            agg[m.ToCode] = (cur.Name, cur.O + o * m.Sign, cur.D + d, cur.C + c, cur.Src);
        }
        return (agg.OrderBy(x => x.Key).Select(x => new MappedRow(x.Key, x.Value.Name, x.Value.O, x.Value.D, x.Value.C, x.Value.O + x.Value.D - x.Value.C, string.Join(", ", x.Value.Src))).ToList(), unmapped);
    }
}
