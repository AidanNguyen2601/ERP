using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

public record BudgetUsage(Budget Budget, BudgetLine Line, int Month, decimal Plan, decimal Actual, decimal YtdPlan, decimal YtdActual)
{
    public decimal Pct => Plan == 0 ? 0 : Math.Round(Actual * 100 / Plan, 1);
    public decimal YtdPct => YtdPlan == 0 ? 0 : Math.Round(YtdActual * 100 / YtdPlan, 1);
}

/// <summary>Ngân sách theo khoản mục (đầu số TK chi phí) × bộ phận × tháng; kiểm soát khi ghi sổ và báo cáo thực hiện</summary>
public class BudgetService
{
    private readonly ErpDbContext _db;
    private readonly SettingsService _set;
    private readonly UserNotices _notices;
    public BudgetService(ErpDbContext db, SettingsService set, UserNotices notices) { _db = db; _set = set; _notices = notices; }

    private static bool Matches(BudgetLine l, string code, string? dept) =>
        code.StartsWith(l.AccountPrefix) && (string.IsNullOrEmpty(l.Department) || string.Equals(l.Department, dept, StringComparison.OrdinalIgnoreCase));

    /// <summary>Thực hiện (Nợ − Có) của dòng ngân sách trong khoảng ngày, không tính kết chuyển</summary>
    private async Task<decimal> ActualAsync(Budget b, BudgetLine l, DateTime from, DateTime to)
    {
        var q = _db.JournalLines.IgnoreQueryFilters().Where(x => x.JournalEntry!.Status == DocStatus.Posted && x.JournalEntry.Source != JournalSource.Closing
            && x.JournalEntry.Date >= from && x.JournalEntry.Date <= to && x.Account!.Code.StartsWith(l.AccountPrefix));
        if (b.BranchId.HasValue) q = q.Where(x => x.JournalEntry!.BranchId == b.BranchId);
        if (!string.IsNullOrEmpty(l.Department)) q = q.Where(x => x.Department == l.Department);
        return await q.SumAsync(x => (decimal?)(x.Debit - x.Credit)) ?? 0;
    }

    /// <summary>Kiểm tra bút toán sắp ghi sổ: vượt ngân sách tháng thì cảnh báo hoặc chặn theo cấu hình ngân sách</summary>
    public async Task CheckAsync(JournalEntry entry, IEnumerable<(string Code, decimal Amount, string? Dept)> lines)
    {
        if (entry.Source is JournalSource.Closing || entry.ReversalOfId != null) return;
        if (!await _set.BoolAsync("Budget.Enabled", true)) return;
        var exp = lines.Where(l => l.Amount > 0 && (l.Code[0] is '6' or '8')).ToList();
        if (exp.Count == 0) return;
        List<Budget> budgets;
        try { budgets = await _db.Budgets.Include(b => b.Lines).Where(b => b.IsActive && b.Year == entry.Date.Year && (b.BranchId == null || b.BranchId == entry.BranchId)).ToListAsync(); }
        catch { return; }
        var m = entry.Date.Month; var from = new DateTime(entry.Date.Year, m, 1); var to = from.AddMonths(1).AddDays(-1);
        foreach (var b in budgets)
            foreach (var bl in b.Lines)
            {
                var add = exp.Where(x => Matches(bl, x.Code, x.Dept)).Sum(x => x.Amount);
                if (add <= 0) continue;
                var plan = bl.Month(m);
                var used = await ActualAsync(b, bl, from, to);
                if (used + add <= plan) continue;
                var msg = $"Vượt ngân sách \"{b.Name}\" – khoản mục {bl.AccountPrefix}{(string.IsNullOrEmpty(bl.Department) ? "" : " / " + bl.Department)} tháng {m:00}: " +
                          $"kế hoạch {plan.ToMoney()}, đã dùng {used.ToMoney()}, chứng từ này {add.ToMoney()} (vượt {(used + add - plan).ToMoney()}).";
                if (b.Control == BudgetControl.Block) throw new BusinessException(msg + " Ngân sách đang ở chế độ chặn – điều chỉnh ngân sách hoặc chứng từ.");
                _notices.Warn(msg);
            }
    }

    public async Task<List<BudgetUsage>> UsageAsync(Budget b, int month)
    {
        var list = new List<BudgetUsage>();
        var from = new DateTime(b.Year, month, 1); var to = from.AddMonths(1).AddDays(-1);
        foreach (var l in b.Lines.OrderBy(x => x.AccountPrefix).ThenBy(x => x.Department))
        {
            var ytdPlan = Enumerable.Range(1, month).Sum(l.Month);
            list.Add(new BudgetUsage(b, l, month, l.Month(month), await ActualAsync(b, l, from, to), ytdPlan, await ActualAsync(b, l, new DateTime(b.Year, 1, 1), to)));
        }
        return list;
    }

    public async Task<decimal[]> MonthlyActualAsync(Budget b, BudgetLine l)
    {
        var r = new decimal[12];
        for (var m = 1; m <= 12; m++) { var f = new DateTime(b.Year, m, 1); r[m - 1] = await ActualAsync(b, l, f, f.AddMonths(1).AddDays(-1)); }
        return r;
    }
}
