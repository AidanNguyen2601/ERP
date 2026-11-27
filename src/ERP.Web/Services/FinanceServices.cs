using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.ViewModels;

namespace ERP.Web.Services;

// =====================================================================
//  TIỀN: phiếu thu / chi / chuyển tiền, cấn trừ công nợ
// =====================================================================
public class CashService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    private readonly FxService _fx;
    public CashService(ErpDbContext db, AccountingService acc, CurrentUser user, FxService fx) { _db = db; _acc = acc; _user = user; _fx = fx; }

    public static string Prefix(CashVoucherType t) => t switch { CashVoucherType.Receipt => "PT", CashVoucherType.Payment => "PC", _ => "CT" };

    /// <summary>Mỗi quỹ / tài khoản ngân hàng gắn một TK chi tiết riêng (1111, 11211, 11212…) → số dư lấy từ sổ cái</summary>
    public async Task<decimal> BalanceAsync(CashAccount ca, DateTime? to = null)
    {
        var code = ca.Account?.Code ?? (await _db.Accounts.FindAsync(ca.AccountId))!.Code;
        return await _acc.BalanceAsync(code, to);
    }

    public async Task<CashVoucher> CreateAsync(CashVoucherForm f)
    {
        var ca = await _db.CashAccounts.Include(c => c.Account).FirstOrDefaultAsync(c => c.Id == f.CashAccountId) ?? throw new BusinessException("Chọn quỹ / tài khoản tiền.");
        if (f.Amount <= 0) throw new BusinessException("Số tiền phải lớn hơn 0.");
        await _acc.EnsurePeriodOpenAsync(f.Date);
        var v = new CashVoucher
        {
            Type = f.Type, Date = f.Date.Date, CashAccountId = ca.Id, Amount = Math.Round(f.Amount, 0), Description = f.Description.Trim(),
            PayerPayee = f.PayerPayee, CreatedById = _user.Id, Status = DocStatus.Posted,
            Number = await DocNumbers.NextAsync(_db.CashVouchers.Select(x => x.Number), Prefix(f.Type), f.Date)
        };
        // Quỹ / tài khoản ngoại tệ: số tiền nhập theo nguyên tệ, quy đổi VND theo tỷ giá (nhập hoặc tỷ giá gần nhất)
        v.CurrencyCode = string.IsNullOrEmpty(ca.CurrencyCode) ? "VND" : ca.CurrencyCode;
        if (v.IsForeign)
        {
            var rate = f.ExchangeRate is > 0 ? f.ExchangeRate.Value : await _fx.RateAsync(v.CurrencyCode, f.Date);
            if (rate <= 0) throw new BusinessException($"Chưa có tỷ giá {v.CurrencyCode} – nhập tỷ giá hoặc khai báo ở danh mục tỷ giá.");
            v.ExchangeRate = rate; v.FcAmount = Math.Round(f.Amount, 2); v.Amount = Math.Round(v.FcAmount * rate, 0);
        }
        var lines = new List<JLine>();
        Partner? partner = f.PartnerId.HasValue ? await _db.Partners.FindAsync(f.PartnerId) : null;

        if (f.Type is CashVoucherType.Payment or CashVoucherType.Transfer)
        {
            var bal = await BalanceAsync(ca);
            if (bal < v.Amount) throw new BusinessException($"Số dư {ca.Name} ({bal.ToMoney()}) không đủ để chi {v.Amount.ToMoney()}.");
        }

        if (f.Type == CashVoucherType.Transfer)
        {
            var to = await _db.CashAccounts.FindAsync(f.ToCashAccountId) ?? throw new BusinessException("Chọn quỹ / tài khoản nhận.");
            if (to.Id == ca.Id) throw new BusinessException("Tài khoản nhận phải khác tài khoản chuyển.");
            if (to.CurrencyCode != ca.CurrencyCode) throw new BusinessException("Chuyển tiền nội bộ giữa hai tài khoản khác loại tiền: dùng phiếu chi + phiếu thu (mua bán ngoại tệ).");
            v.ToCashAccountId = to.Id; v.Purpose = CashPurpose.Internal;
            lines.Add(new JLine(to.AccountId, v.Amount, 0)); lines.Add(new JLine(ca.AccountId, 0, v.Amount));
        }
        else
        {
            v.Purpose = f.Purpose;
            v.PartnerId = partner?.Id;
            switch (f.Purpose)
            {
                case CashPurpose.CustomerReceipt:
                case CashPurpose.VendorPayment:
                    var receipt = f.Purpose == CashPurpose.CustomerReceipt;
                    if (receipt && f.Type != CashVoucherType.Receipt) throw new BusinessException("Thu tiền khách hàng phải dùng phiếu thu.");
                    if (!receipt && f.Type != CashVoucherType.Payment) throw new BusinessException("Trả tiền nhà cung cấp phải dùng phiếu chi.");
                    if (partner == null || (receipt ? !partner.IsCustomer : !partner.IsVendor)) throw new BusinessException(receipt ? "Chọn khách hàng." : "Chọn nhà cung cấp.");
                    // Cấn trừ hóa đơn trước khi hạch toán: hóa đơn chỉ định trước, còn lại theo hạn sớm nhất (FIFO), cùng loại tiền với phiếu.
                    // Hóa đơn ngoại tệ: giảm công nợ theo giá trị ghi sổ (tỷ giá hóa đơn), chênh lệch với tiền thực thu / chi là lãi / lỗ tỷ giá đã thực hiện.
                    var dir = receipt ? InvoiceDirection.Sales : InvoiceDirection.Purchase;
                    var open = (await _db.Invoices.Where(i => i.PartnerId == partner.Id && i.Direction == dir && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total && i.CurrencyCode == v.CurrencyCode).ToListAsync())
                        .OrderByDescending(i => i.Id == f.InvoiceId).ThenBy(i => i.DueDate).ThenBy(i => i.Id).ToList();
                    decimal book = 0, pay = 0;
                    var leftFc = v.IsForeign ? v.FcAmount : v.Amount;
                    foreach (var inv in open)
                    {
                        if (leftFc <= 0) break;
                        decimal bookPart, payPart, fcPart;
                        if (v.IsForeign)
                        {
                            fcPart = Math.Min(leftFc, inv.FcTotal - inv.FcPaid);
                            if (fcPart <= 0) continue;
                            bookPart = fcPart == inv.FcTotal - inv.FcPaid ? inv.Total - inv.PaidAmount : Math.Min(inv.Total - inv.PaidAmount, Math.Round(fcPart * inv.ExchangeRate, 0));
                            payPart = leftFc == fcPart ? v.Amount - pay : Math.Round(fcPart * v.ExchangeRate, 0);
                            inv.FcPaid += fcPart;
                        }
                        else { fcPart = bookPart = payPart = Math.Min(leftFc, inv.Total - inv.PaidAmount); }
                        inv.PaidAmount += bookPart; leftFc -= fcPart; book += bookPart; pay += payPart;
                        v.Allocations.Add(new PaymentAllocation { InvoiceId = inv.Id, Amount = bookPart, FcAmount = v.IsForeign ? fcPart : 0 });
                    }
                    v.FxDifference = pay - book;
                    var partnerSide = book + (v.Amount - pay);   // giá trị ghi sổ phần đã cấn trừ + phần chưa cấn trừ theo tiền thực tế
                    var arap = await _acc.RuleAsync(receipt ? PostingCatalog.CrReceivable : PostingCatalog.CpPayable);
                    if (receipt) { lines.Add(new JLine(ca.AccountId, v.Amount, 0)); lines.Add(new JLine(arap, 0, partnerSide, partner.Id)); }
                    else { lines.Add(new JLine(arap, partnerSide, 0, partner.Id)); lines.Add(new JLine(ca.AccountId, 0, v.Amount)); }
                    var gain = receipt ? v.FxDifference : -v.FxDifference;
                    if (gain > 0) lines.Add(new JLine(await _acc.IdAsync("515"), 0, gain, null, "Lãi chênh lệch tỷ giá đã thực hiện"));
                    else if (gain < 0) lines.Add(new JLine(await _acc.IdAsync("635"), -gain, 0, null, "Lỗ chênh lệch tỷ giá đã thực hiện"));
                    break;
                default:
                    var counter = await _db.Accounts.FindAsync(f.CounterAccountId) ?? throw new BusinessException("Chọn tài khoản đối ứng.");
                    if (counter.TrackPartner && partner == null) throw new BusinessException($"TK {counter.Code} theo dõi công nợ, hãy chọn đối tượng.");
                    if (counter.Id == ca.AccountId) throw new BusinessException("TK đối ứng phải khác TK tiền.");
                    v.CounterAccountId = counter.Id;
                    if (f.Type == CashVoucherType.Receipt) { lines.Add(new JLine(ca.AccountId, v.Amount, 0)); lines.Add(new JLine(counter.Id, 0, v.Amount, counter.TrackPartner ? partner?.Id : null)); }
                    else { lines.Add(new JLine(counter.Id, v.Amount, 0, counter.TrackPartner ? partner?.Id : null)); lines.Add(new JLine(ca.AccountId, 0, v.Amount)); }
                    break;
            }
        }

        _db.CashVouchers.Add(v);
        await _db.SaveChangesAsync();
        var je = await _acc.PostAsync(v.Date, $"{v.Type.GetDisplayName()} {v.Number}: {v.Description}", JournalSource.Cash, v.Number, v.Id, lines);
        v.JournalEntryId = je.Id;

        await _db.SaveChangesAsync();
        return v;
    }

    public async Task CancelAsync(CashVoucher v, string reason)
    {
        if (v.Status != DocStatus.Posted) throw new BusinessException("Phiếu đã hủy.");
        await _acc.ReverseAsync(v.JournalEntryId, DateTime.Today, reason);
        var ids = v.Allocations.Select(a => a.InvoiceId).ToList();
        var invs = await _db.Invoices.Where(i => ids.Contains(i.Id)).ToListAsync();
        foreach (var a in v.Allocations) { var inv = invs.First(i => i.Id == a.InvoiceId); inv.PaidAmount -= a.Amount; inv.FcPaid -= a.FcAmount; }
        v.Status = DocStatus.Cancelled;
        v.Description += $" [Hủy: {reason}]";
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  TÀI SẢN CỐ ĐỊNH
// =====================================================================
public class AssetService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    /// <summary>Ký hiệu chứng từ công nợ phát sinh khi ghi tăng TSCĐ mua chịu</summary>
    public const string AssetInvoiceSeries = "TSCD";
    public AssetService(ErpDbContext db, AccountingService acc, CurrentUser user) { _db = db; _acc = acc; _user = user; }

    /// <summary>Số khấu hao của tài sản trong tháng (đường thẳng, tính tròn tháng bắt đầu khấu hao)</summary>
    public static decimal DepreciationFor(FixedAsset a, int year, int month)
    {
        var end = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
        if (a.Status != AssetStatus.InUse || a.DepreciationStartDate > end) return 0;
        return Math.Max(0, Math.Min(a.MonthlyDepreciation, a.OriginalCost - a.AccumulatedDepreciation));
    }

    public async Task<DepreciationRun> RunDepreciationAsync(int year, int month)
    {
        if (await _db.DepreciationRuns.AnyAsync(r => r.Year == year && r.Month == month)) throw new BusinessException($"Đã trích khấu hao tháng {month:00}/{year}.");
        var date = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
        await _acc.EnsurePeriodOpenAsync(date);
        var assets = await _db.FixedAssets.Include(a => a.Category).Where(a => a.Status == AssetStatus.InUse).ToListAsync();
        var run = new DepreciationRun { Year = year, Month = month, CreatedBy = _user.Name };
        var lines = new List<JLine>();
        foreach (var a in assets)
        {
            var amt = DepreciationFor(a, year, month);
            if (amt <= 0) continue;
            run.Lines.Add(new DepreciationLine { FixedAssetId = a.Id, Amount = amt });
            a.AccumulatedDepreciation += amt;
            if (a.AccumulatedDepreciation >= a.OriginalCost) a.Status = AssetStatus.FullyDepreciated;
            lines.Add(new JLine(a.Category!.ExpenseAccountId, amt, 0, null, a.Code));
            lines.Add(new JLine(a.Category!.DepreciationAccountId, 0, amt, null, a.Code));
        }
        if (!run.Lines.Any()) throw new BusinessException("Không có tài sản nào cần trích khấu hao trong tháng.");
        run.Total = run.Lines.Sum(l => l.Amount);
        _db.DepreciationRuns.Add(run);
        await _db.SaveChangesAsync();
        var je = await _acc.PostAsync(date, $"Trích khấu hao TSCĐ tháng {month:00}/{year}", JournalSource.Depreciation, $"KH{month:00}{year % 100:00}", run.Id, lines);
        run.JournalEntryId = je.Id;
        await _db.SaveChangesAsync();
        return run;
    }

    /// <summary>Ghi tăng TSCĐ: Nợ 211x / Có TK đối ứng (331, 112, 241…)</summary>
    public async Task RegisterAsync(FixedAsset a, int? counterAccountId, int? partnerId)
    {
        _db.FixedAssets.Add(a);
        await _db.SaveChangesAsync();
        if (counterAccountId == null) return;
        var cat = await _db.AssetCategories.FindAsync(a.CategoryId) ?? throw new BusinessException("Chọn nhóm tài sản.");
        var je = await _acc.PostAsync(a.AcquisitionDate, $"Ghi tăng TSCĐ {a.Code} – {a.Name}", JournalSource.FixedAsset, a.Code, a.Id, new[]
        {
            new JLine(cat.AssetAccountId, a.OriginalCost, 0),
            new JLine(counterAccountId.Value, 0, a.OriginalCost, partnerId)
        });

        // Mua chịu: khoản phải trả nhà cung cấp phải nằm trong sổ chi tiết công nợ (tuổi nợ, cấn trừ khi thanh toán) như mọi hóa đơn mua
        var counter = await _db.Accounts.FindAsync(counterAccountId.Value);
        if (counter is { TrackPartner: true } && counter.Code.StartsWith("331") && partnerId.HasValue)
        {
            var vendor = await _db.Partners.FindAsync(partnerId.Value);
            _db.Invoices.Add(new Invoice
            {
                Direction = InvoiceDirection.Purchase, Number = await DocNumbers.NextAsync(_db.Invoices.Select(i => i.Number), "HDM", a.AcquisitionDate),
                Date = a.AcquisitionDate, DueDate = a.AcquisitionDate.AddDays(vendor?.PaymentTermDays ?? 30), PartnerId = partnerId, Series = AssetInvoiceSeries, InvoiceNo = a.Code,
                Status = InvoiceStatus.Issued, Subtotal = a.OriginalCost, VatAmount = 0, Total = a.OriginalCost, JournalEntryId = je.Id, CreatedById = _user.Id,
                Note = $"Công nợ mua TSCĐ {a.Code} – {a.Name}",
                Lines = { new InvoiceLine { Description = $"Mua TSCĐ {a.Code} – {a.Name}", Quantity = 1, UnitPrice = a.OriginalCost, Amount = a.OriginalCost, AccountId = cat.AssetAccountId } }
            });
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Thanh lý: Nợ 2141 hao mòn, Nợ 811 giá trị còn lại / Có 211x nguyên giá; thu thanh lý Nợ tiền / Có 711</summary>
    public async Task DisposeAsync(FixedAsset a, DateTime date, decimal proceeds, int? cashAccountId)
    {
        if (a.Status == AssetStatus.Disposed) throw new BusinessException("Tài sản đã thanh lý.");
        var cat = await _db.AssetCategories.FindAsync(a.CategoryId)!;
        var lines = new List<JLine>
        {
            new(cat!.DepreciationAccountId, a.AccumulatedDepreciation, 0),
            new(await _acc.RuleAsync(PostingCatalog.AdLoss), a.NetBookValue, 0),
            new(cat.AssetAccountId, 0, a.OriginalCost)
        };
        if (proceeds > 0)
        {
            var ca = await _db.CashAccounts.FindAsync(cashAccountId) ?? throw new BusinessException("Chọn quỹ / tài khoản nhận tiền thanh lý.");
            lines.Add(new JLine(ca.AccountId, proceeds, 0));
            lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.AdIncome), 0, proceeds));
        }
        await _acc.PostAsync(date, $"Thanh lý TSCĐ {a.Code} – {a.Name}", JournalSource.FixedAsset, a.Code, a.Id, lines);
        a.Status = AssetStatus.Disposed; a.DisposalDate = date; a.DisposalValue = proceeds;
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  SẢN XUẤT
// =====================================================================
public class ProductionService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly InventoryService _inv;
    private readonly CurrentUser _user;
    private readonly WorkflowService _wf;
    public ProductionService(ErpDbContext db, AccountingService acc, InventoryService inv, CurrentUser user, WorkflowService wf) { _db = db; _acc = acc; _inv = inv; _user = user; _wf = wf; }

    public static decimal Required(BomLine b, decimal outputQty, decimal qty) =>
        Math.Round(b.Quantity * qty / outputQty * (100 + b.ScrapPct) / 100, 3);

    /// <summary>Tạo lệnh sản xuất, nhu cầu vật tư tính từ định mức</summary>
    public async Task<ProductionOrder> CreateAsync(ProductionOrder po)
    {
        var bom = await _db.Boms.Include(b => b.Lines).Include(b => b.Operations).FirstOrDefaultAsync(b => b.Id == po.BomId) ?? throw new BusinessException("Chọn định mức.");
        if (!bom.IsActive) throw new BusinessException("Định mức đã ngưng áp dụng.");
        if (po.Quantity <= 0) throw new BusinessException("Số lượng sản xuất phải lớn hơn 0.");
        po.ItemId = bom.FinishedItemId!.Value;
        po.Number = await DocNumbers.NextAsync(_db.ProductionOrders.Select(x => x.Number), "LSX", po.StartDate);
        po.CreatedById = _user.Id;
        po.Status = ProductionStatus.Draft;
        foreach (var b in bom.Lines) po.Lines.Add(new ProductionOrderLine { ItemId = b.ItemId, RequiredQty = Required(b, bom.OutputQuantity, po.Quantity) });
        if (po.SalesOrderId != null) po.OrderType = ProductionType.MakeToOrder;
        foreach (var op in bom.Operations.OrderBy(x => x.Seq))
            po.Operations.Add(new ProductionOperation { Seq = op.Seq, Name = op.Name, WorkCenterId = op.WorkCenterId, PlannedMinutes = Math.Round(op.SetupMinutes + op.RunMinutesPerUnit * po.Quantity, 1) });
        _db.ProductionOrders.Add(po);
        await _db.SaveChangesAsync();
        return po;
    }

    /// <summary>Gửi duyệt theo quy tắc phê duyệt (giá trị = giá vốn NVL dự kiến). Trả về thông báo nếu phải chờ duyệt, null nếu được duyệt ngay (không có quy tắc áp dụng)</summary>
    public async Task<string?> SubmitAsync(ProductionOrder po)
    {
        if (po.Status != ProductionStatus.Draft) throw new BusinessException("Chỉ gửi duyệt lệnh nháp.");
        if (await _wf.HasRulesAsync(ApprovalDocType.ProductionOrder))
        {
            var ids = po.Lines.Select(l => l.ItemId).ToList();
            var avg = await _db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.AvgCost);
            var amount = po.Lines.Sum(l => l.RequiredQty * avg.GetValueOrDefault(l.ItemId));
            var req = await _wf.StartAsync(ApprovalDocType.ProductionOrder, po.Id, po.Number, amount, $"Lệnh sản xuất {po.Number} – {po.Quantity.ToQty()} {po.Item?.Name}", po.BranchId);
            if (req != null) { po.Status = ProductionStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Lệnh {po.Number} đã gửi duyệt – {WorkflowService.Describe(req)}."; }
        }
        po.Status = ProductionStatus.Approved; po.ApprovedById = _user.Id; po.ApprovedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>Phát lệnh: lệnh nháp phải qua bước duyệt (tự duyệt nếu không có quy tắc nào áp dụng)</summary>
    public async Task ReleaseAsync(ProductionOrder po)
    {
        if (po.Status == ProductionStatus.PendingApproval) throw new PendingApprovalException($"Lệnh {po.Number} đang chờ duyệt.");
        if (po.Status == ProductionStatus.Draft)
        {
            var msg = await SubmitAsync(po);
            if (msg != null) throw new PendingApprovalException(msg);
        }
        if (po.Status != ProductionStatus.Approved) throw new BusinessException("Chỉ phát lệnh với lệnh nháp hoặc đã duyệt.");
        po.Status = ProductionStatus.Released;
        await _db.SaveChangesAsync();
    }

    /// <summary>Xuất nguyên vật liệu cho sản xuất: Nợ 154 / Có 152</summary>
    public async Task<InventoryDoc> IssueMaterialsAsync(ProductionOrder po, DateTime date)
    {
        if (po.Status is not (ProductionStatus.Released or ProductionStatus.InProgress)) throw new BusinessException("Lệnh chưa phát hoặc đã kết thúc.");
        var take = po.Lines.Where(l => l.RequiredQty > l.IssuedQty).Select(l => (l.ItemId, l.RequiredQty - l.IssuedQty, 0m)).ToList();
        if (!take.Any()) throw new BusinessException("Đã xuất đủ nguyên vật liệu.");
        var doc = await _inv.CreateAndPostAsync(InvDocType.Issue, date, po.MaterialWarehouseId!.Value, DocOrigin.Production, po.Number, po.Id, null,
            $"Xuất NVL cho lệnh sản xuất {po.Number}", take, _user.Id);
        foreach (var l in po.Lines)
        {
            var dl = doc.Lines.Where(x => x.ItemId == l.ItemId).ToList();
            l.IssuedQty += dl.Sum(x => x.Quantity);
            l.Cost += dl.Sum(x => x.Amount);
        }
        po.MaterialCost = po.Lines.Sum(l => l.Cost);
        po.Status = ProductionStatus.InProgress;
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Hoàn thành: kết chuyển nhân công, SXC vào 154 rồi nhập kho thành phẩm Nợ 155 / Có 154</summary>
    public async Task CompleteAsync(ProductionOrder po, DateTime date, decimal completedQty, decimal labor, decimal overhead)
    {
        if (po.Status != ProductionStatus.InProgress) throw new BusinessException("Lệnh chưa xuất nguyên vật liệu.");
        if (po.Lines.Any(l => l.IssuedQty < l.RequiredQty)) throw new BusinessException("Chưa xuất đủ nguyên vật liệu theo định mức.");
        if (completedQty <= 0) throw new BusinessException("Số lượng hoàn thành phải lớn hơn 0.");
        if (labor < 0 || overhead < 0) throw new BusinessException("Chi phí không âm.");
        // Cộng thêm vào chi phí đã tập hợp (lệnh có thể đã nhận phân bổ chi phí sản xuất chung trong kỳ)
        var lab = Math.Round(labor, 0); var ohd = Math.Round(overhead, 0);
        po.LaborCost += lab; po.OverheadCost += ohd;
        var wip = await _acc.RuleAsync(PostingCatalog.PrdWip);
        var lines = new List<JLine>();
        if (lab > 0) { lines.Add(new JLine(wip, lab, 0)); lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.PrdLabor), 0, lab)); }
        if (ohd > 0) { lines.Add(new JLine(wip, ohd, 0)); lines.AddRange(await _acc.CreditByBalanceAsync("627", ohd, date, await _acc.RuleAsync(PostingCatalog.PrdOverhead), "Kết chuyển chi phí sản xuất chung")); }
        if (lines.Any()) await _acc.PostAsync(date, $"Kết chuyển chi phí nhân công, SXC vào lệnh {po.Number}", JournalSource.Production, po.Number, po.Id, lines);
        po.CompletedQty = completedQty;
        po.UnitCost = Math.Round(po.TotalCost / completedQty, 2);   // hiển thị 2 số lẻ
        // Nhập kho theo giá thành chính xác (không làm tròn đơn giá) để giá trị thành phẩm = tổng chi phí tập hợp, TK 154 về 0
        await _inv.CreateAndPostAsync(InvDocType.Receipt, date, po.FinishedWarehouseId!.Value, DocOrigin.Production, po.Number, po.Id, null,
            $"Nhập kho thành phẩm lệnh {po.Number}", new[] { (po.ItemId, completedQty, po.TotalCost / completedQty) }, _user.Id);
        po.Status = ProductionStatus.Completed;
        po.CompletedDate = date;
        foreach (var op in po.Operations.Where(x => x.Status != OperationStatus.Done)) { op.Status = OperationStatus.Done; op.FinishedAt = date; }
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  KỲ KẾ TOÁN: kết chuyển cuối kỳ & khóa sổ
// =====================================================================
public class PeriodService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    public PeriodService(ErpDbContext db, AccountingService acc, CurrentUser user) { _db = db; _acc = acc; _user = user; }

    public static DateTime MonthEnd(int year, int month) => new DateTime(year, month, 1).AddMonths(1).AddDays(-1);

    public async Task<bool> HasClosingEntryAsync(int year, int month)
    {
        var end = MonthEnd(year, month);
        var entries = await _db.JournalEntries.Where(j => j.Source == JournalSource.Closing && j.Date == end && j.Status == DocStatus.Posted)
            .Select(j => new { j.Id, j.ReversalOfId }).ToListAsync();
        var reversed = entries.Where(e => e.ReversalOfId != null).Select(e => e.ReversalOfId!.Value).ToHashSet();
        return entries.Any(e => e.ReversalOfId == null && !reversed.Contains(e.Id));
    }

    /// <summary>Kết chuyển doanh thu, chi phí sang 911 và kết quả sang 4212</summary>
    public async Task<JournalEntry?> CloseEntriesAsync(int year, int month)
    {
        if (await HasClosingEntryAsync(year, month)) throw new BusinessException($"Đã kết chuyển tháng {month:00}/{year}.");
        var from = new DateTime(year, month, 1); var to = MonthEnd(year, month);
        var rows = await _acc.PostedLines()
            .Where(l => l.JournalEntry!.Date >= from && l.JournalEntry.Date <= to && l.JournalEntry.Source != JournalSource.Closing)
            .Select(l => new { l.AccountId, l.Account!.Code, l.Debit, l.Credit }).ToListAsync();
        var i911 = await _acc.RuleAsync(PostingCatalog.PcSummary);
        var lines = new List<JLine>();
        decimal profit = 0;
        foreach (var g in rows.Where(r => r.Code[0] is '5' or '6' or '7' or '8').GroupBy(r => r.AccountId))
        {
            var net = g.Sum(x => x.Debit - x.Credit);
            if (net == 0) continue;
            // Số dư Nợ (chi phí) → Có TK, Nợ 911; số dư Có (doanh thu) → Nợ TK, Có 911
            if (net > 0) { lines.Add(new JLine(g.Key, 0, net)); lines.Add(new JLine(i911, net, 0)); }
            else { lines.Add(new JLine(g.Key, -net, 0)); lines.Add(new JLine(i911, 0, -net)); }
            profit -= net;
        }
        if (!lines.Any()) return null;
        var r4212 = await _acc.RuleAsync(PostingCatalog.PcRetained);
        if (profit > 0) { lines.Add(new JLine(i911, profit, 0)); lines.Add(new JLine(r4212, 0, profit)); }
        else if (profit < 0) { lines.Add(new JLine(r4212, -profit, 0)); lines.Add(new JLine(i911, 0, -profit)); }
        return await _acc.PostAsync(to, $"Kết chuyển xác định kết quả kinh doanh tháng {month:00}/{year} (lãi/lỗ {profit:N0})", JournalSource.Closing, $"KC{month:00}{year % 100:00}", null, lines);
    }

    public async Task CloseAsync(int year, int month, bool withClosingEntries)
    {
        if (withClosingEntries && !await HasClosingEntryAsync(year, month)) await CloseEntriesAsync(year, month);
        var p = await _db.FiscalPeriods.FirstOrDefaultAsync(x => x.Year == year && x.Month == month);
        if (p == null) { p = new FiscalPeriod { Year = year, Month = month }; _db.FiscalPeriods.Add(p); }
        if (p.IsClosed) throw new BusinessException($"Kỳ {month:00}/{year} đã khóa.");
        p.IsClosed = true; p.ClosedAt = DateTime.Now; p.ClosedBy = _user.Name;
        await _db.SaveChangesAsync();
    }

    public async Task ReopenAsync(int year, int month)
    {
        var p = await _db.FiscalPeriods.FirstOrDefaultAsync(x => x.Year == year && x.Month == month && x.IsClosed) ?? throw new BusinessException("Kỳ chưa khóa.");
        if (await _db.FiscalPeriods.AnyAsync(x => x.IsClosed && (x.Year > year || (x.Year == year && x.Month > month))))
            throw new BusinessException("Phải mở khóa các kỳ sau trước.");
        p.IsClosed = false; p.ClosedAt = null; p.ClosedBy = null;
        await _db.SaveChangesAsync();
    }
}
