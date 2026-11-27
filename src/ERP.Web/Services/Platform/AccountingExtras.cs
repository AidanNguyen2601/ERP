using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.ViewModels;

namespace ERP.Web.Services;

// =====================================================================
//  BÚT TOÁN ĐỊNH KỲ & PHÂN BỔ
// =====================================================================
public class RecurringService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    public RecurringService(ErpDbContext db, AccountingService acc, CurrentUser user) { _db = db; _acc = acc; _user = user; }

    public static DateTime PeriodDate(DateTime d, int day) => new(d.Year, d.Month, Math.Min(day, DateTime.DaysInMonth(d.Year, d.Month)));

    /// <summary>Số cần phân bổ: số dư Nợ − Có phát sinh trong tháng của TK nguồn (không tính kết chuyển)</summary>
    public async Task<decimal> SourceAmountAsync(int accountId, DateTime date)
    {
        var f = new DateTime(date.Year, date.Month, 1);
        return await _db.JournalLines.Where(l => l.AccountId == accountId && l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source != JournalSource.Closing
            && l.JournalEntry.Date >= f && l.JournalEntry.Date <= date).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
    }

    /// <summary>Sinh các bút toán đến hạn (tối đa 24 kỳ mỗi mẫu, bỏ qua kỳ đã khóa sổ)</summary>
    public async Task<List<string>> GenerateDueAsync(DateTime asOf)
    {
        var log = new List<string>();
        var list = await _db.RecurringJournals.Include(r => r.Lines).ThenInclude(l => l.Account).Where(r => r.IsActive && r.NextDate <= asOf).ToListAsync();
        foreach (var r in list)
        {
            for (var n = 0; n < 24 && r.NextDate <= asOf && (r.EndDate == null || r.NextDate <= r.EndDate); n++)
            {
                var date = PeriodDate(r.NextDate, r.DayOfMonth);
                try
                {
                    var lines = new List<JLine>();
                    if (r.IsAllocation)
                    {
                        var amount = r.SourceAccountId.HasValue ? await SourceAmountAsync(r.SourceAccountId.Value, date) : 0;
                        if (amount > 0)
                        {
                            var targets = r.Lines.Where(l => l.Percent > 0).ToList();
                            decimal used = 0;
                            for (var i = 0; i < targets.Count; i++)
                            {
                                var part = i == targets.Count - 1 ? amount - used : Math.Round(amount * targets[i].Percent / 100, 0);
                                used += part;
                                lines.Add(new JLine(targets[i].AccountId, part, 0, targets[i].PartnerId, r.Name, targets[i].Department));
                            }
                            lines.Add(new JLine(r.SourceAccountId!.Value, 0, amount, null, r.Name));
                        }
                    }
                    else lines.AddRange(r.Lines.Select(l => new JLine(l.AccountId, l.Debit, l.Credit, l.PartnerId, r.Name, l.Department)));
                    if (lines.Count >= 2)
                    {
                        var desc = $"{r.Description} – kỳ {date:MM/yyyy}";
                        if (r.AutoPost) await _acc.PostAsync(date, desc, JournalSource.Allocation, $"DK{r.Id}", r.Id, lines);
                        else
                        {
                            var j = new JournalEntry { Date = date, Description = desc, Source = JournalSource.Manual, CreatedById = _user.Id, Number = await DocNumbers.NextAsync(_db.JournalEntries.Select(x => x.Number), "PKT", date) };
                            foreach (var l in lines) j.Lines.Add(new JournalLine { AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, PartnerId = l.PartnerId, Description = l.Description, Department = l.Department });
                            _db.JournalEntries.Add(j);
                        }
                        log.Add($"{r.Name}: kỳ {date:MM/yyyy}" + (r.AutoPost ? " đã ghi sổ" : " đã lập nháp"));
                    }
                    else log.Add($"{r.Name}: kỳ {date:MM/yyyy} không có số phát sinh cần phân bổ");
                }
                catch (BusinessException ex) { log.Add($"{r.Name}: kỳ {date:MM/yyyy} bỏ qua – {ex.Message}"); }
                r.NextDate = r.NextDate.AddMonths(r.IntervalMonths); r.LastRunAt = DateTime.Now;
                await _db.SaveChangesAsync();
            }
        }
        return log;
    }
}

// =====================================================================
//  CÔNG CỤ DỤNG CỤ, ĐIỀU CHUYỂN TSCĐ
// =====================================================================
public class ToolService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    public ToolService(ErpDbContext db, AccountingService acc, CurrentUser user) { _db = db; _acc = acc; _user = user; }

    /// <summary>Ghi tăng CCDC: Nợ 242 / Có TK đối ứng (331, 111, 112, 153). Không chọn TK đối ứng = CCDC đầu kỳ, không hạch toán.</summary>
    public async Task RegisterAsync(ToolItem t, int? counterAccountId, int? partnerId)
    {
        _db.ToolItems.Add(t);
        await _db.SaveChangesAsync();
        if (counterAccountId == null) return;
        await _acc.PostAsync(t.StartDate, $"Ghi tăng CCDC {t.Code} – {t.Name}", JournalSource.Allocation, t.Code, t.Id, new[]
        {
            new JLine(await _acc.IdAsync("242"), t.Cost, 0, null, t.Name, t.Department),
            new JLine(counterAccountId.Value, 0, t.Cost, partnerId)
        });
    }

    public async Task<ToolAllocationRun> RunAsync(int year, int month)
    {
        if (await _db.ToolAllocationRuns.AnyAsync(r => r.Year == year && r.Month == month)) throw new BusinessException($"Đã phân bổ CCDC tháng {month:00}/{year}.");
        var end = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
        await _acc.EnsurePeriodOpenAsync(end);
        var tools = await _db.ToolItems.Where(t => t.Status == ToolStatus.InUse && t.StartDate <= end).ToListAsync();
        var a242 = await _acc.IdAsync("242");
        var lines = new List<JLine>(); decimal total = 0;
        foreach (var t in tools)
        {
            var amt = Math.Min(t.Monthly, t.Remaining);
            if (amt <= 0) continue;
            t.Allocated += amt; total += amt;
            if (t.Allocated >= t.Cost) t.Status = ToolStatus.FullyAllocated;
            lines.Add(new JLine(t.ExpenseAccountId, amt, 0, null, t.Code, t.Department));
            lines.Add(new JLine(a242, 0, amt, null, t.Code));
        }
        if (total == 0) throw new BusinessException("Không có công cụ dụng cụ cần phân bổ trong tháng.");
        var run = new ToolAllocationRun { Year = year, Month = month, Total = total, CreatedBy = _user.Name };
        _db.ToolAllocationRuns.Add(run);
        var je = await _acc.PostAsync(end, $"Phân bổ chi phí công cụ dụng cụ tháng {month:00}/{year}", JournalSource.Allocation, $"PBCC{month:00}{year % 100:00}", null, lines);
        run.JournalEntryId = je.Id;
        await _db.SaveChangesAsync();
        return run;
    }

    /// <summary>Báo hỏng / thanh lý CCDC: phân bổ nốt giá trị còn lại vào chi phí</summary>
    public async Task DisposeAsync(ToolItem t, DateTime date, string reason)
    {
        if (t.Status == ToolStatus.Disposed) throw new BusinessException("CCDC đã báo hỏng.");
        if (t.Remaining > 0)
            await _acc.PostAsync(date, $"Báo hỏng CCDC {t.Code}: phân bổ nốt giá trị còn lại – {reason}", JournalSource.Allocation, t.Code, t.Id, new[]
            {
                new JLine(t.ExpenseAccountId, t.Remaining, 0, null, t.Code, t.Department), new JLine(await _acc.IdAsync("242"), 0, t.Remaining, null, t.Code)
            });
        t.Allocated = t.Cost; t.Status = ToolStatus.Disposed; t.Note = $"{t.Note} [Báo hỏng {date:dd/MM/yyyy}: {reason}]".Trim();
        await _db.SaveChangesAsync();
    }

    /// <summary>Điều chuyển TSCĐ giữa bộ phận / chi nhánh (lưu lịch sử; khấu hao từ tháng sau tính cho bộ phận mới)</summary>
    public async Task TransferAssetAsync(FixedAsset a, DateTime date, string? toDept, int? toBranch, string? note)
    {
        if (a.Status == AssetStatus.Disposed) throw new BusinessException("Tài sản đã thanh lý.");
        if (toDept == a.Department && (toBranch ?? a.BranchId) == a.BranchId) throw new BusinessException("Chọn bộ phận hoặc chi nhánh nhận khác hiện tại.");
        _db.AssetTransfers.Add(new AssetTransfer { FixedAssetId = a.Id, Date = date.Date, FromDepartment = a.Department, ToDepartment = toDept, FromBranchId = a.BranchId, ToBranchId = toBranch ?? a.BranchId, Note = note, CreatedBy = _user.Name });
        a.Department = toDept; if (toBranch.HasValue) a.BranchId = toBranch;
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  BÙ TRỪ CÔNG NỢ
// =====================================================================
public class OffsetService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    public OffsetService(ErpDbContext db, AccountingService acc, CurrentUser user) { _db = db; _acc = acc; _user = user; }

    public async Task<(decimal Ar, decimal Ap)> OpenAsync(int partnerId)
    {
        var inv = await _db.Invoices.Where(i => i.PartnerId == partnerId && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total && i.CurrencyCode == "VND").ToListAsync();
        return (inv.Where(i => i.Direction == InvoiceDirection.Sales).Sum(i => i.Total - i.PaidAmount), inv.Where(i => i.Direction == InvoiceDirection.Purchase).Sum(i => i.Total - i.PaidAmount));
    }

    /// <summary>Bù trừ phải thu / phải trả cùng đối tượng: Nợ 331 / Có 131, cấn trừ hóa đơn hai phía theo hạn thanh toán</summary>
    public async Task<DebtOffset> CreateAsync(int partnerId, DateTime date, decimal? amount, string? note)
    {
        var p = await _db.Partners.FindAsync(partnerId) ?? throw new BusinessException("Không tìm thấy đối tượng.");
        var (ar, ap) = await OpenAsync(partnerId);
        var max = Math.Min(ar, ap);
        if (max <= 0) throw new BusinessException($"{p.Name} không đồng thời có công nợ phải thu và phải trả theo hóa đơn để bù trừ.");
        var amt = Math.Round(amount is > 0 ? Math.Min(amount.Value, max) : max, 0);
        var off = new DebtOffset { Date = date.Date, PartnerId = p.Id, Amount = amt, Note = note, CreatedById = _user.Id, Number = await DocNumbers.NextAsync(_db.DebtOffsets.Select(x => x.Number), "BTCN", date) };
        foreach (var dir in new[] { InvoiceDirection.Sales, InvoiceDirection.Purchase })
        {
            var left = amt;
            foreach (var i in (await _db.Invoices.Where(i => i.PartnerId == p.Id && i.Direction == dir && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total && i.CurrencyCode == "VND").ToListAsync()).OrderBy(i => i.DueDate).ThenBy(i => i.Id))
            {
                if (left <= 0) break;
                var a = Math.Min(left, i.Total - i.PaidAmount);
                i.PaidAmount += a; left -= a;
                off.Lines.Add(new DebtOffsetLine { InvoiceId = i.Id, Amount = a });
            }
        }
        _db.DebtOffsets.Add(off);
        await _db.SaveChangesAsync();
        var je = await _acc.PostAsync(date, $"Bù trừ công nợ {off.Number} – {p.Name}", JournalSource.Offset, off.Number, off.Id, new[]
        {
            new JLine(await _acc.RuleAsync(PostingCatalog.CpPayable), amt, 0, p.Id, "Bù trừ phải trả"), new JLine(await _acc.RuleAsync(PostingCatalog.CrReceivable), 0, amt, p.Id, "Bù trừ phải thu")
        });
        off.JournalEntryId = je.Id;
        await _db.SaveChangesAsync();
        return off;
    }

    public async Task CancelAsync(DebtOffset off, string reason)
    {
        if (off.Status != DocStatus.Posted) throw new BusinessException("Chứng từ đã hủy.");
        await _acc.ReverseAsync(off.JournalEntryId, DateTime.Today, reason);
        foreach (var l in await _db.DebtOffsetLines.Include(x => x.Invoice).Where(x => x.DebtOffsetId == off.Id).ToListAsync()) l.Invoice!.PaidAmount -= l.Amount;
        off.Status = DocStatus.Cancelled; off.Note = $"{off.Note} [Hủy: {reason}]".Trim();
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  SAO KÊ NGÂN HÀNG
// =====================================================================
public class BankService
{
    private readonly ErpDbContext _db;
    private readonly CashService _cash;
    public BankService(ErpDbContext db, CashService cash) { _db = db; _cash = cash; }

    public record StatementRow(DateTime Date, string Description, string? Reference, decimal Amount);

    /// <summary>Số tiền có dấu của phiếu đối với tài khoản (thu +, chi −; chuyển: nguồn −, đích +) theo loại tiền của tài khoản</summary>
    public static decimal Signed(CashVoucher v, int cashAccountId, bool foreign)
    {
        var a = foreign ? v.FcAmount : v.Amount;
        return v.Type == CashVoucherType.Receipt ? a : v.Type == CashVoucherType.Payment ? -a : v.ToCashAccountId == cashAccountId ? a : -a;
    }

    public async Task<BankStatement> ImportAsync(int cashAccountId, string name, decimal opening, decimal? closing, List<StatementRow> rows, string source)
    {
        var ca = await _db.CashAccounts.FindAsync(cashAccountId) ?? throw new BusinessException("Chọn tài khoản ngân hàng.");
        if (ca.Type != CashAccountType.Bank) throw new BusinessException("Chỉ đối chiếu sao kê cho tài khoản ngân hàng.");
        if (rows.Count == 0) throw new BusinessException("Sao kê không có dòng giao dịch.");
        var st = new BankStatement
        {
            CashAccountId = ca.Id, BranchId = ca.BranchId, Name = string.IsNullOrWhiteSpace(name) ? $"Sao kê {ca.Code} {rows.Min(r => r.Date):dd/MM}–{rows.Max(r => r.Date):dd/MM/yyyy}" : name.Trim(),
            FromDate = rows.Min(r => r.Date).Date, ToDate = rows.Max(r => r.Date).Date, OpeningBalance = opening, ClosingBalance = closing ?? opening + rows.Sum(r => r.Amount), Source = source
        };
        foreach (var r in rows.OrderBy(r => r.Date)) st.Lines.Add(new BankStatementLine { Date = r.Date.Date, Description = r.Description.Length > 500 ? r.Description[..500] : r.Description, Reference = r.Reference, Amount = r.Amount });
        _db.BankStatements.Add(st);
        await _db.SaveChangesAsync();
        await AutoMatchAsync(st.Id);
        return st;
    }

    /// <summary>Tự khớp: cùng số tiền (có dấu), ngày lệch ≤ 5 ngày, ưu tiên số phiếu xuất hiện trong nội dung / tham chiếu</summary>
    public async Task<int> AutoMatchAsync(int statementId)
    {
        var st = await _db.BankStatements.Include(s => s.Lines).Include(s => s.CashAccount).FirstAsync(s => s.Id == statementId);
        var foreign = st.CashAccount!.CurrencyCode != "VND";
        var used = (await _db.BankStatementLines.Where(l => l.CashVoucherId != null).Select(l => l.CashVoucherId!.Value).ToListAsync()).ToHashSet();
        var from = st.FromDate.AddDays(-5); var to = st.ToDate.AddDays(5);
        var vouchers = await _db.CashVouchers.Where(v => v.Status == DocStatus.Posted && (v.CashAccountId == st.CashAccountId || v.ToCashAccountId == st.CashAccountId) && v.Date >= from && v.Date <= to).ToListAsync();
        var n = 0;
        foreach (var l in st.Lines.Where(x => x.CashVoucherId == null).OrderBy(x => x.Date))
        {
            var cands = vouchers.Where(v => !used.Contains(v.Id) && Signed(v, st.CashAccountId, foreign) == l.Amount && Math.Abs((v.Date - l.Date).TotalDays) <= 5).ToList();
            var text = (l.Description + " " + l.Reference).ToUpperInvariant();
            var pick = cands.FirstOrDefault(v => text.Contains(v.Number.ToUpperInvariant())) ?? cands.OrderBy(v => Math.Abs((v.Date - l.Date).TotalDays)).FirstOrDefault();
            if (pick == null) continue;
            l.CashVoucherId = pick.Id; l.MatchType = "auto"; used.Add(pick.Id); n++;
        }
        await _db.SaveChangesAsync();
        return n;
    }

    public async Task MatchAsync(int lineId, int voucherId)
    {
        var l = await _db.BankStatementLines.Include(x => x.BankStatement).ThenInclude(s => s!.CashAccount).FirstAsync(x => x.Id == lineId);
        var v = await _db.CashVouchers.FindAsync(voucherId) ?? throw new BusinessException("Không tìm thấy phiếu.");
        if (await _db.BankStatementLines.AnyAsync(x => x.CashVoucherId == voucherId && x.Id != lineId)) throw new BusinessException($"Phiếu {v.Number} đã khớp với dòng sao kê khác.");
        if (Signed(v, l.BankStatement!.CashAccountId, l.BankStatement.CashAccount!.CurrencyCode != "VND") != l.Amount) throw new BusinessException("Số tiền phiếu không khớp số tiền trên sao kê.");
        l.CashVoucherId = v.Id; l.MatchType = "manual";
        await _db.SaveChangesAsync();
    }

    /// <summary>Lập phiếu thu / chi từ dòng sao kê chưa có trên sổ (phí ngân hàng, lãi tiền gửi, khách chuyển khoản...) rồi khớp</summary>
    public async Task<CashVoucher> CreateVoucherAsync(int lineId, CashPurpose purpose, int? partnerId, int? counterAccountId)
    {
        var l = await _db.BankStatementLines.Include(x => x.BankStatement).FirstAsync(x => x.Id == lineId);
        if (l.CashVoucherId != null) throw new BusinessException("Dòng sao kê đã được khớp.");
        var v = await _cash.CreateAsync(new CashVoucherForm
        {
            Type = l.Amount > 0 ? CashVoucherType.Receipt : CashVoucherType.Payment, Date = l.Date, CashAccountId = l.BankStatement!.CashAccountId, Purpose = purpose,
            PartnerId = partnerId, CounterAccountId = counterAccountId, Amount = Math.Abs(l.Amount), Description = l.Description.Length > 0 ? l.Description : "Theo sao kê ngân hàng"
        });
        l.CashVoucherId = v.Id; l.MatchType = "created";
        await _db.SaveChangesAsync();
        return v;
    }

    public async Task<decimal> BookBalanceAsync(CashAccount ca, DateTime date)
    {
        if (ca.CurrencyCode == "VND") return await _cash.BalanceAsync(ca, date);
        var vs = await _db.CashVouchers.Where(v => v.Status == DocStatus.Posted && v.Date <= date && (v.CashAccountId == ca.Id || v.ToCashAccountId == ca.Id)).ToListAsync();
        return vs.Sum(v => Signed(v, ca.Id, true));
    }
}

// =====================================================================
//  PHÂN BỔ CHI PHÍ SẢN XUẤT CHUNG & GIÁ THÀNH
// =====================================================================
public record CostRow(string Key, string Name, decimal Qty, decimal Material, decimal Labor, decimal Overhead, decimal StdMaterial, decimal StdLabor, decimal StdOverhead)
{
    public decimal Total => Material + Labor + Overhead;
    public decimal Std => StdMaterial + StdLabor + StdOverhead;
    public decimal Variance => Total - Std;
    public decimal Unit => Qty == 0 ? 0 : Math.Round(Total / Qty, 0);
}

public class CostingService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    public CostingService(ErpDbContext db, AccountingService acc) { _db = db; _acc = acc; }

    /// <summary>Chi phí SXC phát sinh trong tháng còn trên TK 627 (Nợ − Có)</summary>
    public async Task<decimal> OverheadPoolAsync(int year, int month)
    {
        var f = new DateTime(year, month, 1); var t = f.AddMonths(1).AddDays(-1);
        return await _db.JournalLines.Where(l => l.Account!.Code.StartsWith("627") && l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Source != JournalSource.Closing
            && l.JournalEntry.Date >= f && l.JournalEntry.Date <= t).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
    }

    /// <summary>
    /// Phân bổ chi phí sản xuất chung thực tế trong tháng cho các lệnh đang sản xuất theo tiêu thức (chi phí NVL hoặc số lượng):
    /// Nợ 154 / Có 627; cộng vào chi phí SXC của lệnh → giá thành khi hoàn thành gồm cả phần được phân bổ.
    /// </summary>
    public async Task<(decimal Amount, int Orders)> AllocateOverheadAsync(int year, int month, string basis, decimal? amount)
    {
        var end = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
        var pool = amount ?? await OverheadPoolAsync(year, month);
        if (pool <= 0) throw new BusinessException($"Không còn chi phí sản xuất chung tháng {month:00}/{year} để phân bổ (số dư TK 627 phát sinh trong tháng ≤ 0).");
        var orders = await _db.ProductionOrders.Where(p => p.Status == ProductionStatus.InProgress).ToListAsync();
        if (orders.Count == 0) throw new BusinessException("Không có lệnh sản xuất đang sản xuất (đã xuất NVL, chưa hoàn thành) để nhận phân bổ.");
        var weights = orders.Select(o => basis == "qty" ? o.Quantity : o.MaterialCost).ToList();
        var sum = weights.Sum();
        if (sum <= 0) throw new BusinessException("Tiêu thức phân bổ bằng 0.");
        var wip = await _acc.RuleAsync(PostingCatalog.PrdWip); var oh = await _acc.RuleAsync(PostingCatalog.PrdOverhead);
        var lines = new List<JLine>(); decimal used = 0;
        for (var i = 0; i < orders.Count; i++)
        {
            var part = i == orders.Count - 1 ? pool - used : Math.Round(pool * weights[i] / sum, 0);
            used += part;
            orders[i].OverheadCost += part; orders[i].AllocatedOverhead += part;
            lines.Add(new JLine(wip, part, 0, null, orders[i].Number));
        }
        lines.Add(new JLine(oh, 0, pool, null, "Phân bổ chi phí sản xuất chung"));
        await _acc.PostAsync(end, $"Phân bổ chi phí sản xuất chung tháng {month:00}/{year} cho {orders.Count} lệnh sản xuất (tiêu thức {(basis == "qty" ? "số lượng" : "chi phí NVL")})",
            JournalSource.Production, $"PBSXC{month:00}{year % 100:00}", null, lines);
        await _db.SaveChangesAsync();
        return (pool, orders.Count);
    }

    /// <summary>Giá thành thực tế so với định mức theo lệnh / sản phẩm / đơn bán</summary>
    public async Task<List<CostRow>> ReportAsync(DateTime from, DateTime to, string by)
    {
        var orders = await _db.ProductionOrders.Include(p => p.Item).Include(p => p.SalesOrder).Include(p => p.Bom).ThenInclude(b => b!.Lines).ThenInclude(l => l.Item)
            .Where(p => p.Status == ProductionStatus.Completed && p.CompletedDate >= from && p.CompletedDate <= to).ToListAsync();
        var rows = orders.Select(o =>
        {
            var q = o.CompletedQty; var b = o.Bom!; var ratio = b.OutputQuantity == 0 ? 0 : q / b.OutputQuantity;
            var stdMat = Math.Round(b.Lines.Sum(l => l.Quantity * (1 + l.ScrapPct / 100) * (l.Item?.AvgCost ?? 0)) * ratio, 0);
            return (o, new CostRow("", "", q, o.MaterialCost, o.LaborCost, o.OverheadCost, stdMat, Math.Round(b.LaborCostPerUnit * q, 0), Math.Round(b.OverheadPerUnit * q, 0)));
        }).ToList();
        IEnumerable<IGrouping<(string, string), (ProductionOrder o, CostRow r)>> groups = by switch
        {
            "product" => rows.GroupBy(x => (x.o.Item!.Code, x.o.Item.Name)),
            "salesorder" => rows.GroupBy(x => (x.o.SalesOrder?.Number ?? "(Sản xuất để tồn kho)", x.o.SalesOrder != null ? "Đơn bán " + x.o.SalesOrder.Number : "Không gắn đơn bán")),
            _ => rows.GroupBy(x => (x.o.Number, x.o.Item!.Name))
        };
        return groups.Select(g => new CostRow(g.Key.Item1, g.Key.Item2, g.Sum(x => x.r.Qty), g.Sum(x => x.r.Material), g.Sum(x => x.r.Labor), g.Sum(x => x.r.Overhead),
            g.Sum(x => x.r.StdMaterial), g.Sum(x => x.r.StdLabor), g.Sum(x => x.r.StdOverhead))).OrderBy(r => r.Key).ToList();
    }
}
