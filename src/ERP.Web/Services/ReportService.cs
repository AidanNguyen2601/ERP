using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.ViewModels;

namespace ERP.Web.Services;

public class ReportService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    public ReportService(ErpDbContext db, AccountingService acc) { _db = db; _acc = acc; }

    private record Mv(int AccountId, decimal D, decimal C);

    private async Task<List<Mv>> MovesAsync(DateTime? from, DateTime to, bool excludeClosing = false)
    {
        var q = _acc.PostedLines();
        var t = to.Date;
        q = q.Where(l => l.JournalEntry!.Date <= t);
        if (from.HasValue) { var f = from.Value.Date; q = q.Where(l => l.JournalEntry!.Date >= f); }
        if (excludeClosing) q = q.Where(l => l.JournalEntry!.Source != JournalSource.Closing);
        var rows = await q.Select(l => new { l.AccountId, l.Debit, l.Credit }).ToListAsync();
        return rows.GroupBy(r => r.AccountId).Select(g => new Mv(g.Key, g.Sum(x => x.Debit), g.Sum(x => x.Credit))).ToList();
    }

    // ------------------------------------------------------------------ Bảng cân đối số phát sinh
    public async Task<List<TrialBalanceRow>> TrialBalanceAsync(DateTime from, DateTime to)
    {
        var accounts = await _db.Accounts.OrderBy(a => a.Code).ToListAsync();
        var open = (await MovesAsync(null, from.AddDays(-1))).ToDictionary(m => m.AccountId);
        var period = (await MovesAsync(from, to)).ToDictionary(m => m.AccountId);
        var leaf = new List<TrialBalanceRow>();
        foreach (var a in accounts.Where(a => a.IsPostable))
        {
            var o = open.TryGetValue(a.Id, out var ov) ? ov.D - ov.C : 0;
            var p = period.TryGetValue(a.Id, out var pv) ? pv : new Mv(a.Id, 0, 0);
            var c = o + p.D - p.C;
            if (o == 0 && p.D == 0 && p.C == 0) continue;
            leaf.Add(new TrialBalanceRow
            {
                Account = a, OpenDebit = Math.Max(o, 0), OpenCredit = Math.Max(-o, 0), Debit = p.D, Credit = p.C,
                CloseDebit = Math.Max(c, 0), CloseCredit = Math.Max(-c, 0)
            });
        }
        // Dòng tổng hợp TK cấp 1 (3 chữ số) – cộng các TK chi tiết
        var result = new List<TrialBalanceRow>();
        foreach (var g in leaf.GroupBy(r => r.Account.Code[..3]).OrderBy(g => g.Key))
        {
            var head = accounts.FirstOrDefault(a => a.Code == g.Key);
            if (g.Count() == 1 && g.First().Account.Code == g.Key) { result.Add(g.First()); continue; }
            result.Add(new TrialBalanceRow
            {
                Account = head ?? new Account { Code = g.Key, Name = "" }, IsGroup = true,
                OpenDebit = g.Sum(x => x.OpenDebit), OpenCredit = g.Sum(x => x.OpenCredit), Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit),
                CloseDebit = g.Sum(x => x.CloseDebit), CloseCredit = g.Sum(x => x.CloseCredit)
            });
            result.AddRange(g);
        }
        return result;
    }

    // ------------------------------------------------------------------ Sổ cái / sổ chi tiết tài khoản
    public async Task<(decimal Opening, List<LedgerRow> Rows)> LedgerAsync(string accountPrefix, DateTime from, DateTime to, int? partnerId = null)
    {
        var opening = await _acc.BalanceAsync(accountPrefix, from.AddDays(-1), partnerId);
        var f = from.Date; var t = to.Date;
        var q = _acc.PostedLines().Include(l => l.JournalEntry).Include(l => l.Account).Include(l => l.Partner)
            .Where(l => l.Account!.Code.StartsWith(accountPrefix) && l.JournalEntry!.Date >= f && l.JournalEntry.Date <= t);
        if (partnerId.HasValue) q = q.Where(l => l.PartnerId == partnerId);
        var lines = await q.ToListAsync();
        var entryIds = lines.Select(l => l.JournalEntryId).Distinct().ToList();
        // TK đối ứng: các TK phía ngược lại trong cùng bút toán
        var siblings = (await _db.JournalLines.Include(l => l.Account).Where(l => entryIds.Contains(l.JournalEntryId)).ToListAsync())
            .GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<LedgerRow>();
        var bal = opening;
        foreach (var l in lines.OrderBy(l => l.JournalEntry!.Date).ThenBy(l => l.JournalEntryId).ThenBy(l => l.Id))
        {
            bal += l.Debit - l.Credit;
            var counter = siblings[l.JournalEntryId].Where(s => !s.Account!.Code.StartsWith(accountPrefix) && (l.Debit > 0 ? s.Credit > 0 : s.Debit > 0))
                .Select(s => s.Account!.Code).Distinct();
            rows.Add(new LedgerRow
            {
                Date = l.JournalEntry!.Date, Number = l.JournalEntry.Number, EntryId = l.JournalEntryId, Description = l.Description ?? l.JournalEntry.Description,
                AccountCode = l.Account!.Code, CounterAccounts = string.Join(", ", counter), PartnerName = l.Partner?.Name,
                Debit = l.Debit, Credit = l.Credit, Balance = bal
            });
        }
        return (opening, rows);
    }

    // ------------------------------------------------------------------ Báo cáo kết quả kinh doanh (B02-DN rút gọn)
    public async Task<List<ReportLine>> IncomeStatementAsync(DateTime from, DateTime to, DateTime? prevFrom = null, DateTime? prevTo = null)
    {
        async Task<Dictionary<string, decimal>> Values(DateTime f, DateTime t)
        {
            var mv = await MovesAsync(f, t, excludeClosing: true);
            var codes = (await _db.Accounts.ToListAsync()).ToDictionary(a => a.Id, a => a.Code);
            decimal Net(string prefix, bool credit) => mv.Where(m => codes[m.AccountId].StartsWith(prefix)).Sum(m => credit ? m.C - m.D : m.D - m.C);
            var v = new Dictionary<string, decimal>
            {
                ["01"] = Net("511", true), ["02"] = Net("521", false), ["11"] = Net("632", false),
                ["21"] = Net("515", true), ["22"] = Net("635", false), ["25"] = Net("641", false), ["26"] = Net("642", false),
                ["31"] = Net("711", true), ["32"] = Net("811", false), ["51"] = Net("821", false)
            };
            v["10"] = v["01"] - v["02"]; v["20"] = v["10"] - v["11"];
            v["30"] = v["20"] + v["21"] - v["22"] - v["25"] - v["26"];
            v["40"] = v["31"] - v["32"]; v["50"] = v["30"] + v["40"]; v["60"] = v["50"] - v["51"];
            return v;
        }
        var cur = await Values(from, to);
        var prev = prevFrom.HasValue ? await Values(prevFrom.Value, prevTo!.Value) : null;
        ReportLine L(string code, string name, bool bold = false, int indent = 0) =>
            new() { Code = code, Name = name, Amount = cur[code], Previous = prev?[code], Bold = bold, Indent = indent };
        return new List<ReportLine>
        {
            L("01", "Doanh thu bán hàng và cung cấp dịch vụ"),
            L("02", "Các khoản giảm trừ doanh thu"),
            L("10", "Doanh thu thuần về bán hàng và cung cấp dịch vụ", true),
            L("11", "Giá vốn hàng bán"),
            L("20", "Lợi nhuận gộp về bán hàng và cung cấp dịch vụ", true),
            L("21", "Doanh thu hoạt động tài chính"),
            L("22", "Chi phí tài chính"),
            L("25", "Chi phí bán hàng"),
            L("26", "Chi phí quản lý doanh nghiệp"),
            L("30", "Lợi nhuận thuần từ hoạt động kinh doanh", true),
            L("31", "Thu nhập khác"),
            L("32", "Chi phí khác"),
            L("40", "Lợi nhuận khác", true),
            L("50", "Tổng lợi nhuận kế toán trước thuế", true),
            L("51", "Chi phí thuế TNDN hiện hành"),
            L("60", "Lợi nhuận sau thuế thu nhập doanh nghiệp", true)
        };
    }

    // ------------------------------------------------------------------ Báo cáo tình hình tài chính (B01-DN theo TT99, trước đây là Bảng cân đối kế toán; rút gọn)
    public async Task<(List<ReportLine> Assets, List<ReportLine> Sources)> BalanceSheetAsync(DateTime at)
    {
        var mv = await MovesAsync(null, at);
        var codes = (await _db.Accounts.ToListAsync()).ToDictionary(a => a.Id, a => a.Code);
        decimal Bal(params string[] prefixes) => mv.Where(m => prefixes.Any(p => codes[m.AccountId].StartsWith(p))).Sum(m => m.D - m.C);
        var ar = await _acc.PartnerBalancesAsync("131", at);
        var ap = await _acc.PartnerBalancesAsync("331", at);
        var arDebit = ar.Values.Where(v => v > 0).Sum(); var arCredit = -ar.Values.Where(v => v < 0).Sum();
        var apDebit = ap.Values.Where(v => v > 0).Sum(); var apCredit = -ap.Values.Where(v => v < 0).Sum();

        var cash = Bal("111", "112", "113");
        var vatIn = Bal("133");
        var otherRec = Bal("138", "141");
        var inventory = Bal("151", "152", "153", "154", "155", "156", "157");
        var prepaid = Bal("242");
        var faCost = Bal("211", "213");
        var faDep = Bal("214");
        var mapped = new[] { "111", "112", "113", "131", "133", "138", "141", "151", "152", "153", "154", "155", "156", "157", "242", "211", "213", "214" };
        var otherAssets = mv.Where(m => (codes[m.AccountId][0] is '1' or '2') && !mapped.Any(p => codes[m.AccountId].StartsWith(p))).Sum(m => m.D - m.C);

        var tax = -Bal("333"); var payroll = -Bal("334"); var otherPay = -Bal("332", "335", "336", "338"); var loans = -Bal("341");
        var mappedL = new[] { "331", "332", "333", "334", "335", "336", "338", "341" };
        var otherLiab = -mv.Where(m => codes[m.AccountId][0] == '3' && !mappedL.Any(p => codes[m.AccountId].StartsWith(p))).Sum(m => m.D - m.C);
        var capital = -Bal("411");
        // LNST chưa phân phối = TK 421 + kết quả kinh doanh chưa kết chuyển (đầu 5–9)
        var retained = -Bal("421") - mv.Where(m => codes[m.AccountId][0] is '5' or '6' or '7' or '8' or '9').Sum(m => m.D - m.C);
        var otherEquity = -mv.Where(m => codes[m.AccountId][0] == '4' && !codes[m.AccountId].StartsWith("411") && !codes[m.AccountId].StartsWith("421")).Sum(m => m.D - m.C);

        ReportLine R(string code, string name, decimal v, bool bold = false, int indent = 1) => new() { Code = code, Name = name, Amount = v, Bold = bold, Indent = indent };
        var shortAssets = cash + arDebit + apDebit + vatIn + otherRec + inventory;
        var longAssets = faCost + faDep + prepaid + otherAssets;
        var liabilities = apCredit + arCredit + tax + payroll + otherPay + loans + otherLiab;
        var equity = capital + retained + otherEquity;
        var assets = new List<ReportLine>
        {
            R("100", "A. TÀI SẢN NGẮN HẠN", shortAssets, true, 0),
            R("110", "Tiền và các khoản tương đương tiền", cash),
            R("131", "Phải thu ngắn hạn của khách hàng", arDebit),
            R("132", "Trả trước cho người bán", apDebit),
            R("136", "Phải thu khác, tạm ứng", otherRec),
            R("140", "Hàng tồn kho", inventory),
            R("152", "Thuế GTGT được khấu trừ", vatIn),
            R("200", "B. TÀI SẢN DÀI HẠN", longAssets, true, 0),
            R("221", "Tài sản cố định – nguyên giá", faCost),
            R("223", "Giá trị hao mòn lũy kế", faDep),
            R("260", "Chi phí chờ phân bổ, tài sản dài hạn khác", prepaid + otherAssets),
            R("270", "TỔNG CỘNG TÀI SẢN", shortAssets + longAssets, true, 0)
        };
        var sources = new List<ReportLine>
        {
            R("300", "C. NỢ PHẢI TRẢ", liabilities, true, 0),
            R("311", "Phải trả người bán", apCredit),
            R("312", "Người mua trả tiền trước", arCredit),
            R("313", "Thuế và các khoản phải nộp Nhà nước", tax),
            R("314", "Phải trả người lao động", payroll),
            R("319", "Phải trả khác", otherPay + otherLiab),
            R("320", "Vay và nợ thuê tài chính", loans),
            R("400", "D. VỐN CHỦ SỞ HỮU", equity, true, 0),
            R("411", "Vốn góp của chủ sở hữu", capital),
            R("421", "Lợi nhuận sau thuế chưa phân phối", retained),
            R("429", "Vốn chủ sở hữu khác", otherEquity),
            R("440", "TỔNG CỘNG NGUỒN VỐN", liabilities + equity, true, 0)
        };
        return (assets, sources);
    }

    // ------------------------------------------------------------------ Tuổi nợ phải thu / phải trả
    public async Task<List<AgingRow>> AgingAsync(InvoiceDirection dir, DateTime at)
    {
        var invs = await _db.Invoices.Include(i => i.Partner)
            .Where(i => i.Direction == dir && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).ToListAsync();
        return invs.GroupBy(i => i.Partner!).Select(g =>
        {
            var r = new AgingRow { Partner = g.Key };
            foreach (var i in g)
            {
                var amt = i.Total - i.PaidAmount;
                var days = (at.Date - i.DueDate.Date).Days;
                if (days <= 0) r.NotDue += amt; else if (days <= 30) r.D1_30 += amt; else if (days <= 60) r.D31_60 += amt; else if (days <= 90) r.D61_90 += amt; else r.Over90 += amt;
            }
            return r;
        }).OrderByDescending(r => r.Total).ToList();
    }
}
