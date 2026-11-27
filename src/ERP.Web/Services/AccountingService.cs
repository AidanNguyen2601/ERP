using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Một dòng định khoản: Nợ / Có một tài khoản (có thể kèm đối tượng công nợ)</summary>
public record JLine(int AccountId, decimal Debit, decimal Credit, int? PartnerId = null, string? Description = null, string? Department = null);

/// <summary>
/// Sổ cái: mọi nghiệp vụ (hóa đơn, phiếu thu chi, kho, khấu hao, sản xuất) đều ghi sổ qua service này.
/// Kiểm tra: kỳ kế toán chưa khóa, tổng Nợ = tổng Có, tài khoản chi tiết được phép hạch toán.
/// </summary>
public class AccountingService
{
    // Tài khoản hệ thống dùng trong hạch toán tự động
    public const string Cash = "1111", Bank = "11211", Receivable = "131", VatIn = "1331", ShortageAsset = "1381",
        Material = "152", Wip = "154", Finished = "155", Goods = "156", FixedAssetAcc = "211", AccumDep = "2141",
        Payable = "331", VatOut = "33311", Payroll = "3341", SurplusAsset = "3381", OtherPayable = "3388",
        RetainedThisYear = "4212", RevenueGoods = "5111", RevenueProducts = "5112", RevenueServices = "5113",
        DirectLabor = "622", Overhead = "6278", Cogs = "632", AdminServices = "6427", OtherExpense = "811", OtherIncome = "711",
        IncomeSummary = "911";

    private readonly ErpDbContext _db;
    private readonly CurrentUser _user;
    private Dictionary<string, Account>? _byCode;

    private readonly BudgetService _budget;
    public AccountingService(ErpDbContext db, CurrentUser user, BudgetService budget) { _db = db; _user = user; _budget = budget; }

    public async Task<Account> AccAsync(string code)
    {
        _byCode ??= (await _db.Accounts.ToListAsync()).ToDictionary(a => a.Code);
        return _byCode.TryGetValue(code, out var a) ? a : throw new BusinessException($"Chưa khai báo tài khoản {code} trong hệ thống tài khoản.");
    }
    public async Task<int> IdAsync(string code) => (await AccAsync(code)).Id;

    // ------------------------------------------------------------------ Cấu hình bút toán theo giao dịch
    private Dictionary<string, Account>? _rules;

    /// <summary>
    /// Tài khoản hạch toán cho một dòng định khoản đã khai báo trong PostingCatalog:
    /// tài khoản cấu hình (nếu còn hạch toán được và đúng đầu số cho phép), nếu không thì tài khoản mặc định.
    /// </summary>
    public async Task<Account> RuleAccountAsync(string key)
    {
        var def = PostingCatalog.Find(key) ?? throw new InvalidOperationException($"Chưa khai báo cấu hình bút toán '{key}'.");
        _rules ??= await _db.PostingRules.Include(r => r.Account).Where(r => r.Account != null)
            .ToDictionaryAsync(r => r.Key, r => r.Account!);
        if (_rules.TryGetValue(key, out var acc) && acc.IsPostable && acc.IsActive && PostingCatalog.Allows(def, acc.Code)) return acc;
        return await AccAsync(def.DefaultCode!);
    }
    /// <summary>
    /// Kết chuyển chi phí theo từng TK cấp 2 (VD 6271…6278): ghi Có mỗi TK theo số phát sinh Nợ còn lại từ đầu tháng đến ngày kết chuyển;
    /// kết chuyển một phần thì chia theo tỷ lệ; phần vượt (phân bổ theo định mức cao hơn thực tế) ghi vào TK mặc định.
    /// </summary>
    public async Task<List<JLine>> CreditByBalanceAsync(string prefix, decimal amount, DateTime date, int fallbackId, string? description = null)
    {
        var res = new List<JLine>(); if (amount <= 0) return res;
        var from = new DateTime(date.Year, date.Month, 1);
        var nets = (await _db.JournalLines.Where(l => l.Account!.Code.StartsWith(prefix) && l.Account.IsPostable && l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source != JournalSource.Closing
                && l.JournalEntry.Date >= from && l.JournalEntry.Date <= date).GroupBy(l => l.AccountId).Select(g => new { g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToListAsync())
            .Where(x => x.B > 0).OrderBy(x => x.Key).ToList();
        var total = nets.Sum(x => x.B);
        if (total <= 0) { res.Add(new JLine(fallbackId, 0, amount, null, description)); return res; }
        if (amount >= total) { res.AddRange(nets.Select(x => new JLine(x.Key, 0, x.B, null, description))); if (amount > total) res.Add(new JLine(fallbackId, 0, amount - total, null, description)); return res; }
        decimal used = 0;
        for (var k = 0; k < nets.Count; k++) { var a = k == nets.Count - 1 ? amount - used : Math.Round(amount * nets[k].B / total, 0); used += a; if (a != 0) res.Add(new JLine(nets[k].Key, 0, a, null, description)); }
        return res;
    }

    public async Task<int> RuleAsync(string key) => (await RuleAccountAsync(key)).Id;
    public Task<int> InventoryRuleAsync(ItemType t) => RuleAsync(PostingCatalog.InventoryKey(t));
    public Task<int> RevenueRuleAsync(ItemType? t) => RuleAsync(PostingCatalog.RevenueKey(t));
    /// <summary>Bỏ bộ nhớ đệm sau khi đổi cấu hình trong cùng request</summary>
    public void ResetRuleCache() => _rules = null;

    public static string InventoryAccount(ItemType t) => t switch
    {
        ItemType.Material => Material,
        ItemType.FinishedGood => Finished,
        _ => Goods
    };
    public static string RevenueAccount(ItemType? t) => t switch
    {
        ItemType.FinishedGood => RevenueProducts,
        ItemType.Merchandise or ItemType.Material => RevenueGoods,
        _ => RevenueServices
    };

    // ------------------------------------------------------------------ Kỳ kế toán
    public async Task EnsurePeriodOpenAsync(DateTime date)
    {
        var closed = await _db.FiscalPeriods.AnyAsync(p => p.Year == date.Year && p.Month == date.Month && p.IsClosed);
        if (closed) throw new BusinessException($"Kỳ kế toán {date:MM/yyyy} đã khóa sổ, không thể ghi sổ chứng từ ngày {date:dd/MM/yyyy}.");
    }

    // ------------------------------------------------------------------ Ghi sổ
    public async Task<JournalEntry> PostAsync(DateTime date, string description, JournalSource source, string? sourceRef, int? sourceId, IEnumerable<JLine> lines)
    {
        var entry = new JournalEntry
        {
            Date = date.Date, Description = description, Source = source, SourceRef = sourceRef, SourceId = sourceId,
            CreatedById = _user.Id, CreatedAt = DateTime.Now
        };
        foreach (var l in Merge(lines))
            entry.Lines.Add(new JournalLine { AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, PartnerId = l.PartnerId, Description = l.Description, Department = l.Department });
        await PostEntryAsync(entry);
        return entry;
    }

    /// <summary>Ghi sổ một bút toán (mới hoặc nháp đã có)</summary>
    public async Task PostEntryAsync(JournalEntry entry)
    {
        await EnsurePeriodOpenAsync(entry.Date);
        foreach (var l in entry.Lines) { l.Debit = Math.Round(l.Debit, 0); l.Credit = Math.Round(l.Credit, 0); }
        var lines = entry.Lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (lines.Count < 2) throw new BusinessException("Bút toán phải có ít nhất một dòng Nợ và một dòng Có.");
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0)))
            throw new BusinessException("Mỗi dòng chỉ ghi Nợ hoặc Có, số tiền không âm.");
        var d = lines.Sum(l => l.Debit); var c = lines.Sum(l => l.Credit);
        if (d != c) throw new BusinessException($"Bút toán không cân: tổng Nợ {d.ToMoney()} ≠ tổng Có {c.ToMoney()}.");
        var ids = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await _db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync();
        var bad = accounts.Where(a => !a.IsPostable || !a.IsActive).Select(a => a.Code).ToList();
        if (accounts.Count != ids.Count) throw new BusinessException("Bút toán có tài khoản không tồn tại.");
        if (bad.Any()) throw new BusinessException($"Tài khoản {string.Join(", ", bad)} là tài khoản tổng hợp hoặc ngưng sử dụng, hãy chọn tài khoản chi tiết.");
        // Kiểm soát ngân sách chi phí (cảnh báo / chặn theo cấu hình ngân sách)
        entry.BranchId ??= _db.DefaultBranchId;
        var codeOf = accounts.ToDictionary(a => a.Id, a => a.Code);
        await _budget.CheckAsync(entry, lines.Select(l => (codeOf[l.AccountId], l.Debit - l.Credit, l.Department)));
        var needPartner = accounts.Where(a => a.TrackPartner).Select(a => a.Id).ToHashSet();
        if (lines.Any(l => needPartner.Contains(l.AccountId) && l.PartnerId == null))
            throw new BusinessException("Tài khoản công nợ (131, 331…) phải chọn đối tượng.");

        // Xóa dòng 0 đồng
        foreach (var z in entry.Lines.Where(l => l.Debit == 0 && l.Credit == 0).ToList()) { entry.Lines.Remove(z); if (z.Id != 0) _db.JournalLines.Remove(z); }
        if (string.IsNullOrEmpty(entry.Number)) entry.Number = await DocNumbers.NextAsync(_db.JournalEntries.Select(j => j.Number), "BT", entry.Date);
        entry.Status = DocStatus.Posted;
        entry.PostedAt = DateTime.Now;
        if (entry.Id == 0) _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();
    }

    /// <summary>Bút toán đảo (dùng khi hủy chứng từ đã ghi sổ)</summary>
    public async Task<JournalEntry?> ReverseAsync(int? journalEntryId, DateTime date, string reason)
    {
        if (journalEntryId == null) return null;
        var src = await _db.JournalEntries.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == journalEntryId);
        if (src == null || src.Status != DocStatus.Posted) return null;
        var rev = await PostAsync(date, $"Đảo bút toán {src.Number}: {reason}", src.Source, src.SourceRef, src.SourceId,
            src.Lines.Select(l => new JLine(l.AccountId, l.Credit, l.Debit, l.PartnerId, l.Description)).ToList());
        rev.ReversalOfId = src.Id;
        await _db.SaveChangesAsync();
        return rev;
    }

    /// <summary>Gộp các dòng cùng TK + đối tượng + phía Nợ/Có</summary>
    private static IEnumerable<JLine> Merge(IEnumerable<JLine> lines) =>
        lines.Where(l => l.Debit != 0 || l.Credit != 0)
             .GroupBy(l => (l.AccountId, l.PartnerId, l.Department, Side: l.Debit > 0))
             .Select(g => new JLine(g.Key.AccountId, g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Key.PartnerId, g.First().Description, g.Key.Department));

    // ------------------------------------------------------------------ Số dư
    public IQueryable<JournalLine> PostedLines() => _db.JournalLines.Where(l => l.JournalEntry!.Status == DocStatus.Posted);

    /// <summary>Số dư Nợ − Có của các tài khoản có số hiệu bắt đầu bằng prefix, đến hết ngày (to)</summary>
    public async Task<decimal> BalanceAsync(string prefix, DateTime? to = null, int? partnerId = null)
    {
        var q = PostedLines().Where(l => l.Account!.Code.StartsWith(prefix));
        if (to.HasValue) { var t = to.Value.Date; q = q.Where(l => l.JournalEntry!.Date <= t); }
        if (partnerId.HasValue) q = q.Where(l => l.PartnerId == partnerId);
        return await q.SumAsync(l => l.Debit - l.Credit);
    }

    /// <summary>Phát sinh Nợ − Có trong khoảng ngày (không tính bút toán kết chuyển)</summary>
    public async Task<decimal> MovementAsync(string prefix, DateTime from, DateTime to, bool excludeClosing = true)
    {
        var f = from.Date; var t = to.Date;
        var q = PostedLines().Where(l => l.Account!.Code.StartsWith(prefix) && l.JournalEntry!.Date >= f && l.JournalEntry.Date <= t);
        if (excludeClosing) q = q.Where(l => l.JournalEntry!.Source != JournalSource.Closing);
        return await q.SumAsync(l => l.Debit - l.Credit);
    }

    /// <summary>Số dư công nợ theo từng đối tượng của TK (131 / 331)</summary>
    public async Task<Dictionary<int, decimal>> PartnerBalancesAsync(string prefix, DateTime? to = null)
    {
        var q = PostedLines().Where(l => l.Account!.Code.StartsWith(prefix) && l.PartnerId != null);
        if (to.HasValue) { var t = to.Value.Date; q = q.Where(l => l.JournalEntry!.Date <= t); }
        var rows = await q.Select(l => new { l.PartnerId, l.Debit, l.Credit }).ToListAsync();
        return rows.GroupBy(r => r.PartnerId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Debit - x.Credit));
    }
}
