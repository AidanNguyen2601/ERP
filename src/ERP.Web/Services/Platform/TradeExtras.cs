using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

// =====================================================================
//  ĐỀ NGHỊ MUA HÀNG (PR) → YÊU CẦU BÁO GIÁ (RFQ) → ĐƠN MUA (PO)
// =====================================================================
public class RequisitionService
{
    private readonly ErpDbContext _db; private readonly WorkflowService _wf; private readonly CurrentUser _user;
    public RequisitionService(ErpDbContext db, WorkflowService wf, CurrentUser user) { _db = db; _wf = wf; _user = user; }

    public async Task<string> NextNumberAsync(DateTime d) => await DocNumbers.NextAsync(_db.PurchaseRequisitions.Select(x => x.Number), "DNM", d);

    /// <summary>Gửi duyệt theo quy tắc phê duyệt; không có quy tắc thì tự duyệt</summary>
    public async Task<string> SubmitAsync(PurchaseRequisition pr)
    {
        if (pr.Status != RequestStatus.Draft) throw new BusinessException("Chỉ gửi duyệt đề nghị đang nháp.");
        if (!pr.Lines.Any()) throw new BusinessException("Đề nghị chưa có dòng hàng.");
        if (pr.Lines.Any(l => l.Quantity <= 0)) throw new BusinessException("Số lượng phải lớn hơn 0.");
        if (await _wf.HasRulesAsync(ApprovalDocType.PurchaseRequisition))
        {
            var req = await _wf.StartAsync(ApprovalDocType.PurchaseRequisition, pr.Id, pr.Number, pr.Estimated, $"Đề nghị mua {pr.Number} – {pr.Department}", pr.BranchId);
            if (req != null) { pr.Status = RequestStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Đề nghị {pr.Number} đã gửi duyệt – {WorkflowService.Describe(req)}."; }
        }
        pr.Status = RequestStatus.Approved; await _db.SaveChangesAsync();
        return $"Đề nghị {pr.Number} không cần duyệt theo quy tắc, đã được duyệt tự động.";
    }

    /// <summary>Tạo đề nghị từ các mặt hàng dưới mức tồn tối thiểu (bổ sung đến mức tồn tối đa, hoặc 2 lần tồn tối thiểu nếu chưa khai báo tối đa)</summary>
    public async Task<PurchaseRequisition?> FromLowStockAsync(DateTime date, int? userId)
    {
        var items = await _db.Items.Where(i => i.IsActive && i.Type != ItemType.Service && i.MinStock > 0 && i.Type != ItemType.FinishedGood).ToListAsync();
        var onHand = (await _db.StockLevels.ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        var open = (await _db.PurchaseOrderLines.Where(l => l.PurchaseOrder!.Status != PoStatus.Cancelled && l.PurchaseOrder.Status != PoStatus.Draft && l.Quantity > l.ReceivedQty)
            .Select(l => new { l.ItemId, Q = l.Quantity - l.ReceivedQty }).ToListAsync()).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Q));
        var pendingPr = (await _db.PurchaseRequisitionLines.Where(l => l.PurchaseRequisition!.Status != RequestStatus.Cancelled && l.PurchaseRequisition.Status != RequestStatus.Converted)
            .Select(l => new { l.ItemId, Q = l.Quantity - l.OrderedQty }).ToListAsync()).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Q));
        var pr = new PurchaseRequisition { Date = date, Number = await NextNumberAsync(date), Source = "Tồn tối thiểu", Purpose = "Bổ sung hàng dưới mức tồn tối thiểu", RequesterId = userId, Department = "Kho vận", RequiredDate = date.AddDays(7) };
        foreach (var i in items)
        {
            var have = onHand.GetValueOrDefault(i.Id) + open.GetValueOrDefault(i.Id) + pendingPr.GetValueOrDefault(i.Id);
            if (have > i.MinStock) continue;
            var target = i.MaxStock > i.MinStock ? i.MaxStock : i.MinStock * 2;
            pr.Lines.Add(new PurchaseRequisitionLine { ItemId = i.Id, Quantity = Math.Ceiling(target - have), EstimatedPrice = i.AvgCost, Note = $"Tồn {onHand.GetValueOrDefault(i.Id):0.##}, tối thiểu {i.MinStock:0.##}" });
        }
        if (pr.Lines.Count == 0) return null;
        _db.PurchaseRequisitions.Add(pr); await _db.SaveChangesAsync();
        return pr;
    }

    /// <summary>Tạo đơn mua nháp từ các dòng đề nghị đã chọn (số lượng còn lại chưa đặt)</summary>
    public async Task<PurchaseOrder> ToPurchaseOrderAsync(PurchaseRequisition pr, int vendorId, int warehouseId, IEnumerable<int> lineIds, IDictionary<int, decimal>? priceByItem = null, int? rfqId = null)
    {
        if (pr.Status is not (RequestStatus.Approved or RequestStatus.Converted)) throw new BusinessException("Đề nghị chưa được duyệt.");
        var vendor = await _db.Partners.FindAsync(vendorId) ?? throw new BusinessException("Chọn nhà cung cấp.");
        if (!vendor.IsVendor) throw new BusinessException("Đối tượng không phải nhà cung cấp.");
        if (vendor.IsBlocked) throw new BusinessException($"Nhà cung cấp {vendor.Name} đang tạm ngừng giao dịch: {vendor.BlockReason}");
        var ids = lineIds.ToHashSet();
        var po = new PurchaseOrder { VendorId = vendorId, WarehouseId = warehouseId, Date = DateTime.Today, ExpectedDate = pr.RequiredDate < DateTime.Today ? DateTime.Today.AddDays(7) : pr.RequiredDate,
            Note = $"Từ đề nghị mua {pr.Number}", RequisitionId = pr.Id, RfqId = rfqId, CreatedById = _user.Id,
            Number = await DocNumbers.NextAsync(_db.PurchaseOrders.Select(x => x.Number), "DMH", DateTime.Today) };
        foreach (var l in pr.Lines.Where(x => ids.Contains(x.Id)))
        {
            var left = l.Quantity - l.OrderedQty;
            if (left <= 0) continue;
            var item = l.Item ?? await _db.Items.FindAsync(l.ItemId);
            var price = priceByItem != null && priceByItem.TryGetValue(l.ItemId, out var p) ? p : l.EstimatedPrice > 0 ? l.EstimatedPrice : item!.AvgCost;
            po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Quantity = left, UnitPrice = price, VatRate = item!.VatRate });
            l.OrderedQty += left;
        }
        if (!po.Lines.Any()) throw new BusinessException("Các dòng đã chọn đều đã được đặt hàng.");
        PurchaseService.Recalc(po);
        _db.PurchaseOrders.Add(po);
        if (pr.Lines.All(l => l.OrderedQty >= l.Quantity)) pr.Status = RequestStatus.Converted;
        await _db.SaveChangesAsync();
        return po;
    }

    /// <summary>Tạo RFQ từ đề nghị mua đã duyệt</summary>
    public async Task<Rfq> ToRfqAsync(PurchaseRequisition pr, IEnumerable<int> vendorIds)
    {
        if (pr.Status != RequestStatus.Approved) throw new BusinessException("Chỉ gửi yêu cầu báo giá cho đề nghị đã duyệt và chưa đặt hàng.");
        var rfq = new Rfq { PurchaseRequisitionId = pr.Id, Date = DateTime.Today, DueDate = DateTime.Today.AddDays(5), CreatedById = _user.Id, Status = RfqStatus.Sent,
            Number = await DocNumbers.NextAsync(_db.Rfqs.Select(x => x.Number), "RFQ", DateTime.Today), Note = $"Theo đề nghị mua {pr.Number}" };
        foreach (var l in pr.Lines.Where(x => x.Quantity > x.OrderedQty)) rfq.Lines.Add(new RfqLine { ItemId = l.ItemId, Quantity = l.Quantity - l.OrderedQty, RequisitionLineId = l.Id });
        foreach (var v in vendorIds.Distinct()) rfq.Vendors.Add(new RfqVendor { PartnerId = v });
        if (!rfq.Lines.Any() || !rfq.Vendors.Any()) throw new BusinessException("Chọn ít nhất một nhà cung cấp và có dòng hàng chưa đặt.");
        _db.Rfqs.Add(rfq); await _db.SaveChangesAsync();
        return rfq;
    }

    /// <summary>Chọn nhà cung cấp trúng thầu theo báo giá: tạo đơn mua nháp với giá đã báo</summary>
    public async Task<PurchaseOrder> AwardAsync(Rfq rfq, int vendorId, int warehouseId)
    {
        if (rfq.Status == RfqStatus.Awarded) throw new BusinessException("RFQ đã chọn nhà cung cấp.");
        var vendor = await _db.Partners.FindAsync(vendorId) ?? throw new BusinessException("Chọn nhà cung cấp.");
        if (vendor.IsBlocked) throw new BusinessException($"Nhà cung cấp {vendor.Name} đang tạm ngừng giao dịch.");
        var quotes = rfq.Quotes.Where(q => q.PartnerId == vendorId && q.UnitPrice > 0).ToDictionary(q => q.RfqLineId, q => q.UnitPrice);
        if (rfq.Lines.Any(l => !quotes.ContainsKey(l.Id))) throw new BusinessException("Nhà cung cấp này chưa báo giá đủ các dòng hàng.");
        var rv = rfq.Vendors.First(v => v.PartnerId == vendorId);
        var po = new PurchaseOrder { VendorId = vendorId, WarehouseId = warehouseId, Date = DateTime.Today, ExpectedDate = DateTime.Today.AddDays(Math.Max(rv.LeadDays, 1)), RfqId = rfq.Id, RequisitionId = rfq.PurchaseRequisitionId,
            Note = $"Từ báo giá {rfq.Number}. {rv.Terms}".Trim(), CreatedById = _user.Id, Number = await DocNumbers.NextAsync(_db.PurchaseOrders.Select(x => x.Number), "DMH", DateTime.Today) };
        var items = (await _db.Items.Where(i => rfq.Lines.Select(l => l.ItemId).Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id);
        foreach (var l in rfq.Lines) po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = quotes[l.Id], VatRate = items[l.ItemId].VatRate });
        PurchaseService.Recalc(po);
        _db.PurchaseOrders.Add(po);
        rfq.Status = RfqStatus.Awarded; rfq.AwardedPartnerId = vendorId;
        await _db.SaveChangesAsync();
        rfq.PurchaseOrderId = po.Id;
        if (rfq.PurchaseRequisitionId != null)
        {
            var pr = await _db.PurchaseRequisitions.Include(p => p.Lines).FirstAsync(p => p.Id == rfq.PurchaseRequisitionId);
            foreach (var l in rfq.Lines.Where(x => x.RequisitionLineId != null)) { var pl = pr.Lines.FirstOrDefault(x => x.Id == l.RequisitionLineId); if (pl != null) pl.OrderedQty += l.Quantity; }
            if (pr.Lines.All(x => x.OrderedQty >= x.Quantity)) pr.Status = RequestStatus.Converted;
        }
        await _db.SaveChangesAsync();
        return po;
    }
}

// =====================================================================
//  ĐÁNH GIÁ NHÀ CUNG CẤP & ĐỐI CHIẾU 3 BÊN
// =====================================================================
public record SupplierStats(int Orders, int Deliveries, int OnTime, decimal OnTimePct, int QcChecked, int QcPassed, decimal QcPassPct, decimal Purchased, int Ncrs);

public class SupplierService
{
    private readonly ErpDbContext _db;
    public SupplierService(ErpDbContext db) => _db = db;

    public static string GradeOf(decimal total) => total >= 8.5m ? "A" : total >= 7 ? "B" : total >= 5 ? "C" : "D";

    /// <summary>Chỉ số tự động: giao đúng hạn (ngày nhận so với ngày giao dự kiến trên đơn mua), tỷ lệ đạt kiểm tra đầu vào, số NCR</summary>
    public async Task<SupplierStats> StatsAsync(int partnerId, DateTime from, DateTime to)
    {
        var pos = await _db.PurchaseOrders.Where(p => p.VendorId == partnerId && p.Status != PoStatus.Draft && p.Status != PoStatus.Cancelled && p.Date >= from && p.Date <= to).ToListAsync();
        var poIds = pos.Select(p => p.Id).ToList();
        var docs = await _db.InventoryDocs.Where(d => d.Origin == DocOrigin.Purchase && d.Type == InvDocType.Receipt && d.Status != DocStatus.Cancelled && d.OriginId != null && poIds.Contains(d.OriginId.Value)).ToListAsync();
        var onTime = docs.Count(d => d.Date <= pos.First(p => p.Id == d.OriginId).ExpectedDate);
        var qc = await _db.QcInspections.Where(q => q.PartnerId == partnerId && q.Status == QcStatus.Completed && q.Date >= from && q.Date <= to).Select(q => q.Result).ToListAsync();
        var passed = qc.Count(r => r is QcResult.Pass or QcResult.Conditional);
        var ncr = await _db.QcNonconformances.CountAsync(n => n.Inspection != null && n.Inspection.PartnerId == partnerId && n.Inspection.Date >= from && n.Inspection.Date <= to);
        return new SupplierStats(pos.Count, docs.Count, onTime, docs.Count == 0 ? 0 : Math.Round(onTime * 100m / docs.Count, 1), qc.Count, passed, qc.Count == 0 ? 0 : Math.Round(passed * 100m / qc.Count, 1), pos.Sum(p => p.Total), ncr);
    }

    public async Task<SupplierEvaluation> EvaluateAsync(SupplierEvaluation e, string by)
    {
        e.Total = Math.Round(e.Quality * 0.35m + e.Delivery * 0.30m + e.Price * 0.20m + e.Service * 0.15m, 2);
        e.Grade = GradeOf(e.Total); e.EvaluatedBy = by;
        _db.SupplierEvaluations.Add(e);
        var p = await _db.Partners.FindAsync(e.PartnerId) ?? throw new BusinessException("Không tìm thấy nhà cung cấp.");
        p.Grade = e.Grade;
        await _db.SaveChangesAsync();
        return e;
    }
}

public record MatchLine(PurchaseOrder Po, PurchaseOrderLine Line, decimal Received, decimal Invoiced, decimal InvoiceValue, decimal InvoicePrice, List<string> Issues);

public class ThreeWayMatchService
{
    private readonly ErpDbContext _db;
    public ThreeWayMatchService(ErpDbContext db) => _db = db;

    /// <summary>Đối chiếu từng dòng đơn mua: đặt hàng – nhận hàng (phiếu nhập kho) – hóa đơn nhà cung cấp; cảnh báo chênh lệch số lượng / đơn giá / giá trị</summary>
    public async Task<List<MatchLine>> RunAsync(DateTime from, DateTime to, int? vendorId, bool onlyIssues)
    {
        var pos = await _db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).ThenInclude(l => l.Item)
            .Where(p => p.Status != PoStatus.Draft && p.Status != PoStatus.PendingApproval && p.Status != PoStatus.Cancelled && p.Date >= from && p.Date <= to && (vendorId == null || p.VendorId == vendorId)).OrderBy(p => p.Date).ToListAsync();
        var poIds = pos.Select(p => p.Id).ToList();
        var invLines = await _db.InvoiceLines.Where(l => l.Invoice!.PurchaseOrderId != null && poIds.Contains(l.Invoice.PurchaseOrderId!.Value) && l.Invoice.Status != InvoiceStatus.Cancelled && l.ItemId != null)
            .Select(l => new { PoId = l.Invoice!.PurchaseOrderId!.Value, ItemId = l.ItemId!.Value, l.Quantity, l.UnitPrice, l.Amount }).ToListAsync();
        var res = new List<MatchLine>();
        foreach (var p in pos)
            foreach (var l in p.Lines)
            {
                var il = invLines.Where(x => x.PoId == p.Id && x.ItemId == l.ItemId).ToList();
                var iq = il.Sum(x => x.Quantity); var iv = il.Sum(x => x.Amount);
                var price = iq == 0 ? 0 : Math.Round(iv / iq, 2);
                var issues = new List<string>();
                if (l.ReceivedQty > l.Quantity) issues.Add($"Nhận vượt đặt {(l.ReceivedQty - l.Quantity).ToQty()}");
                if (iq > l.ReceivedQty) issues.Add($"Hóa đơn vượt số nhận {(iq - l.ReceivedQty).ToQty()}");
                if (iq > 0 && Math.Abs(price - l.UnitPrice) > 0.5m) issues.Add($"Đơn giá hóa đơn {price:N0} ≠ đơn mua {l.UnitPrice:N0}");
                if (l.ReceivedQty > 0 && l.ReceivedQty - l.ReturnedQty > iq && p.Status is PoStatus.Received or PoStatus.Completed && p.ExpectedDate < DateTime.Today.AddDays(-30)) issues.Add($"Nhận {l.ReceivedQty.ToQty()} chưa lập hóa đơn đủ (còn {(l.ReceivedQty - l.ReturnedQty - iq).ToQty()})");
                if (l.Quantity > l.ReceivedQty && p.ExpectedDate < DateTime.Today && p.Status != PoStatus.Completed) issues.Add($"Chưa giao đủ, quá hạn {(DateTime.Today - p.ExpectedDate).Days} ngày");
                if (!onlyIssues || issues.Count > 0) res.Add(new MatchLine(p, l, l.ReceivedQty, iq, iv, price, issues));
            }
        return res;
    }
}

// =====================================================================
//  CHI PHÍ MUA HÀNG (LANDED COST)
// =====================================================================
public class LandedCostService
{
    private readonly ErpDbContext _db; private readonly AccountingService _acc; private readonly CurrentUser _user; private readonly InvoiceService _invoices;
    public LandedCostService(ErpDbContext db, AccountingService acc, CurrentUser user, InvoiceService invoices) { _db = db; _acc = acc; _user = user; _invoices = invoices; }

    /// <summary>
    /// Phân bổ chi phí vào giá nhập của hàng đã nhận trên đơn mua theo giá trị hoặc số lượng. Phần hàng còn tồn cộng vào giá trị tồn kho
    /// (Nợ 15x) và giá vốn bình quân; phần đã xuất bán tính vào giá vốn (Nợ 632). Đối ứng: Có 331 / 111 / 112 / 3333 (thuế nhập khẩu); thuế GTGT đầu vào Nợ 133.
    /// </summary>
    public async Task<LandedCost> CreateAsync(int poId, DateTime date, LandedCostKind kind, string description, decimal amount, int vatRate, string basis, int counterAccountId, int? partnerId, string? vendorInvoiceNo = null, string? vendorSeries = null)
    {
        if (amount <= 0) throw new BusinessException("Nhập số tiền chi phí.");
        if (vatRate is not (0 or 5 or 8 or 10)) throw new BusinessException("Thuế suất GTGT 0, 5, 8 hoặc 10%.");
        var vat = Math.Round(amount * vatRate / 100m, 0);
        var counterAcc = await _db.Accounts.FindAsync(counterAccountId) ?? throw new BusinessException("Chọn tài khoản đối ứng.");
        var viaInvoice = counterAcc.Code.StartsWith("331");
        if (viaInvoice && (partnerId == null || string.IsNullOrWhiteSpace(vendorInvoiceNo))) throw new BusinessException("Ghi nợ phải trả 331: chọn nhà cung cấp dịch vụ và nhập số hóa đơn của họ.");
        var po = await _db.PurchaseOrders.Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == poId) ?? throw new BusinessException("Không tìm thấy đơn mua.");
        var lines = po.Lines.Where(l => l.ReceivedQty - l.ReturnedQty > 0 && l.Item!.IsStocked).ToList();
        if (lines.Count == 0) throw new BusinessException("Đơn mua chưa nhận hàng nên chưa phân bổ được chi phí.");
        await _acc.EnsurePeriodOpenAsync(date);
        var weights = lines.Select(l => basis == "qty" ? l.ReceivedQty - l.ReturnedQty : (l.ReceivedQty - l.ReturnedQty) * l.UnitPrice).ToList();
        var sum = weights.Sum(); if (sum <= 0) throw new BusinessException("Tiêu thức phân bổ bằng 0.");
        var onHand = (await _db.StockLevels.Where(s => lines.Select(l => l.ItemId).Contains(s.ItemId)).ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var lc = new LandedCost { Number = await DocNumbers.NextAsync(_db.LandedCosts.Select(x => x.Number), "CPMH", date), Date = date, PurchaseOrderId = poId, Kind = kind, PartnerId = partnerId, Description = description,
            Amount = amount, VatAmount = vat, Basis = basis, CounterAccountId = counterAccountId, CreatedById = _user.Id };
        decimal used = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i]; var net = l.ReceivedQty - l.ReturnedQty;
            var part = i == lines.Count - 1 ? amount - used : Math.Round(amount * weights[i] / sum, 0); used += part;
            // Phần còn tồn: tồn hiện tại của mặt hàng so với số đã nhận
            var keep = Math.Min(net, Math.Max(0, onHand.GetValueOrDefault(l.ItemId)));
            var inv = Math.Round(part * keep / net, 0);
            lc.Lines.Add(new LandedCostLine { PurchaseOrderLineId = l.Id, ItemId = l.ItemId, Allocated = part, ToInventory = inv, ToCogs = part - inv });
            onHand[l.ItemId] = Math.Max(0, onHand.GetValueOrDefault(l.ItemId) - keep);
        }
        _db.LandedCosts.Add(lc); await _db.SaveChangesAsync();

        var jl = new List<JLine>(); var invLines = new List<InvoiceLine>();
        foreach (var g in lc.Lines.GroupBy(x => x.ItemId))
        {
            var item = lines.First(l => l.ItemId == g.Key).Item!;
            var inv = g.Sum(x => x.ToInventory); var cogs = g.Sum(x => x.ToCogs);
            var invAcc = await _acc.InventoryRuleAsync(item.Type); var cogsAcc = await _acc.RuleAsync(PostingCatalog.SdCogs);
            if (inv > 0) { jl.Add(new JLine(invAcc, inv, 0, null, $"CP mua hàng {item.Code}")); invLines.Add(new InvoiceLine { Description = $"{description} – phân bổ vào tồn kho {item.Code}", Unit = "Lần", Quantity = 1, UnitPrice = inv, VatRate = vatRate, AccountId = invAcc }); }
            if (cogs > 0) { jl.Add(new JLine(cogsAcc, cogs, 0, null, $"CP mua hàng phân bổ vào giá vốn {item.Code}")); invLines.Add(new InvoiceLine { Description = $"{description} – phân bổ vào giá vốn {item.Code}", Unit = "Lần", Quantity = 1, UnitPrice = cogs, VatRate = vatRate, AccountId = cogsAcc }); }
        }
        if (viaInvoice)
        {
            // Nhà cung cấp dịch vụ xuất hóa đơn: ghi nhận như hóa đơn mua (công nợ, thuế GTGT, thanh toán đi theo hóa đơn)
            var vendor = await _db.Partners.FindAsync(partnerId!.Value) ?? throw new BusinessException("Không tìm thấy nhà cung cấp.");
            var pinv = new Invoice { Direction = InvoiceDirection.Purchase, Date = date, DueDate = date.AddDays(vendor.PaymentTermDays), PartnerId = vendor.Id, Series = vendorSeries, InvoiceNo = vendorInvoiceNo!.Trim(), BranchId = po.BranchId, CreatedById = _user.Id,
                Note = $"Chi phí mua hàng {lc.Number} – đơn mua {po.Number}", Number = await _invoices.NextNumberAsync(InvoiceDirection.Purchase, date) };
            foreach (var l in invLines) pinv.Lines.Add(l);
            await _invoices.IssueAsync(pinv);
            await _db.SaveChangesAsync();
            lc.InvoiceId = pinv.Id; lc.VendorInvoiceNo = pinv.InvoiceNo; lc.JournalEntryId = pinv.JournalEntryId;
        }
        else
        {
            if (vat > 0) jl.Add(new JLine(await _acc.RuleAsync(PostingCatalog.PiVatIn), vat, 0));
            jl.Add(new JLine(counterAccountId, 0, amount + vat, counterAcc.TrackPartner ? partnerId : null, description));
            var je = await _acc.PostAsync(date, $"Chi phí mua hàng {lc.Number} – đơn mua {po.Number}: {description}", JournalSource.Inventory, lc.Number, lc.Id, jl);
            lc.JournalEntryId = je.Id;
        }

        // Tăng giá trị tồn kho + giá vốn bình quân (thẻ kho ghi số lượng 0, giá trị dương để khớp sổ cái)
        foreach (var g in lc.Lines.GroupBy(x => x.ItemId))
        {
            var inv = g.Sum(x => x.ToInventory); if (inv <= 0) continue;
            var item = lines.First(l => l.ItemId == g.Key).Item!;
            var qty = onHand.TryGetValue(g.Key, out _) ? (await _db.StockLevels.Where(s => s.ItemId == g.Key).SumAsync(s => s.Quantity)) : 0;
            if (qty > 0) item.AvgCost = Math.Round(item.AvgCost + inv / qty, 2);
            var wh = po.WarehouseId ?? await _db.Warehouses.Select(w => w.Id).FirstAsync();
            _db.StockMoves.Add(new StockMove { Date = date, ItemId = g.Key, WarehouseId = wh, Quantity = 0, UnitCost = 0, Amount = inv, InventoryDocId = (await _db.InventoryDocs.Where(d => d.Origin == DocOrigin.Purchase && d.OriginId == po.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync()) ?? 0, DocNumber = lc.Number, Description = $"Chi phí mua hàng {description}" });
            // Hàng tính giá theo lô (FIFO / đích danh): cộng vào giá các lô còn tồn
            if (item.CostMethod != CostMethod.Average)
            {
                var lots = await _db.StockLots.Where(x => x.ItemId == g.Key && x.Quantity > 0).ToListAsync(); var lq = lots.Sum(x => x.Quantity);
                if (lq > 0) foreach (var lot in lots) lot.UnitCost += Math.Round(inv / lq, 4);
            }
        }
        await _db.SaveChangesAsync();
        return lc;
    }

    public async Task CancelAsync(LandedCost lc, string reason)
    {
        if (lc.Status == DocStatus.Cancelled) throw new BusinessException("Chứng từ đã hủy.");
        if (lc.InvoiceId != null) { var pinv = await _db.Invoices.Include(i => i.Lines).FirstAsync(i => i.Id == lc.InvoiceId); await _invoices.CancelAsync(pinv, $"Hủy chi phí mua hàng {lc.Number}: {reason}"); }
        else await _acc.ReverseAsync(lc.JournalEntryId, DateTime.Today, $"Hủy chi phí mua hàng {lc.Number}: {reason}");
        if (lc.Lines.Count == 0) await _db.Entry(lc).Collection(x => x.Lines).LoadAsync();
        foreach (var g in lc.Lines.GroupBy(x => x.ItemId))
        {
            var inv = g.Sum(x => x.ToInventory); if (inv <= 0) continue;
            var item = await _db.Items.FindAsync(g.Key); var qty = await _db.StockLevels.Where(s => s.ItemId == g.Key).SumAsync(s => s.Quantity);
            if (item != null && qty > 0) item.AvgCost = Math.Max(0, Math.Round(item.AvgCost - inv / qty, 2));
            var wh = await _db.Warehouses.Select(w => w.Id).FirstAsync();
            _db.StockMoves.Add(new StockMove { Date = DateTime.Today, ItemId = g.Key, WarehouseId = wh, Quantity = 0, Amount = -inv, InventoryDocId = 0, DocNumber = lc.Number + " (hủy)", Description = $"Hủy chi phí mua hàng: {reason}" });
        }
        lc.Status = DocStatus.Cancelled; lc.Description += $" [Hủy: {reason}]";
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  BẢNG GIÁ, CHIẾT KHẤU, KHUYẾN MẠI
// =====================================================================
public record PriceResult(decimal Price, decimal DiscountPct, string Source);

public class PricingService
{
    private readonly ErpDbContext _db;
    public PricingService(ErpDbContext db) => _db = db;

    public async Task<PriceResult> GetAsync(int itemId, int? partnerId, decimal qty, DateTime date, string currency = "VND")
    {
        var item = await _db.Items.FindAsync(itemId) ?? throw new BusinessException("Không tìm thấy hàng hóa.");
        var partner = partnerId == null ? null : await _db.Partners.FindAsync(partnerId);
        var group = partner?.PartnerGroup;
        var lists = await _db.PriceLists.Include(l => l.Lines.Where(x => x.ItemId == itemId))
            .Where(l => l.IsActive && l.CurrencyCode == currency && l.ValidFrom <= date && (l.ValidTo == null || l.ValidTo >= date)
                && ((l.PartnerId != null && l.PartnerId == partnerId) || (l.PartnerId == null && (l.PartnerGroup == null || l.PartnerGroup == group)))).ToListAsync();
        decimal price = item.SalePrice, disc = 0; var source = "Giá bán chuẩn của hàng";
        foreach (var l in lists.OrderByDescending(x => x.PartnerId != null).ThenByDescending(x => x.PartnerGroup != null).ThenByDescending(x => x.Priority))
        {
            var line = l.Lines.Where(x => x.MinQty <= qty).OrderByDescending(x => x.MinQty).FirstOrDefault();
            if (line == null) continue;
            price = line.Price > 0 ? line.Price : item.SalePrice; disc = line.DiscountPct; source = "Bảng giá " + l.Name; break;
        }
        var promos = await _db.Promotions.Where(p => p.IsActive && p.ValidFrom <= date && (p.ValidTo == null || p.ValidTo >= date) && (p.ItemId == null || p.ItemId == itemId)
            && (p.PartnerGroup == null || p.PartnerGroup == group) && p.MinQty <= qty).ToListAsync();
        var promo = promos.OrderByDescending(p => p.DiscountPct).FirstOrDefault();
        if (promo != null) { disc = Math.Min(100, disc + promo.DiscountPct); source += " + KM " + promo.Name; }
        return new PriceResult(price, disc, source);
    }

    /// <summary>Áp dụng bảng giá và khuyến mại cho đơn bán nháp</summary>
    public async Task<int> ApplyAsync(SalesOrder so)
    {
        if (so.Status != SoStatus.Draft) throw new BusinessException("Chỉ áp dụng bảng giá cho đơn nháp.");
        var n = 0;
        foreach (var l in so.Lines)
        {
            var r = await GetAsync(l.ItemId, so.CustomerId, l.Quantity, so.Date);
            if (l.UnitPrice != r.Price || l.DiscountPct != r.DiscountPct) { l.UnitPrice = r.Price; l.DiscountPct = r.DiscountPct; n++; }
        }
        SalesService.Recalc(so);
        await _db.SaveChangesAsync();
        return n;
    }
}

// =====================================================================
//  BÁO GIÁ BÁN HÀNG VÀ PHƯƠNG ÁN KINH DOANH
// =====================================================================
public class QuoteService
{
    private readonly ErpDbContext _db; private readonly SettingsService _set; private readonly CurrentUser _user;
    public QuoteService(ErpDbContext db, SettingsService set, CurrentUser user) { _db = db; _set = set; _user = user; }

    public static void Recalc(SalesQuote q)
    {
        q.Subtotal = q.Lines.Sum(l => l.Amount);
        q.VatAmount = q.Lines.Sum(l => Math.Round(l.Amount * l.VatRate / 100m, 0));
        q.Total = q.Subtotal + q.VatAmount;
        q.CostTotal = q.Lines.Sum(l => Math.Round(l.Quantity * l.UnitCost, 0));
        q.MarginPct = q.Subtotal == 0 ? 0 : Math.Round((q.Subtotal - q.CostTotal - q.OtherCost) * 100 / q.Subtotal, 1);
    }

    public async Task<decimal> MinMarginAsync() => await _set.DecAsync("Quote.MinMarginPct", 10);

    /// <summary>Gửi duyệt: lợi nhuận thấp hơn ngưỡng cần Ban Giám đốc duyệt, ngược lại duyệt tự động</summary>
    public async Task<string> SubmitAsync(SalesQuote q)
    {
        if (q.Status != QuoteStatus.Draft) throw new BusinessException("Chỉ gửi duyệt báo giá nháp.");
        if (!q.Lines.Any()) throw new BusinessException("Báo giá chưa có dòng hàng.");
        Recalc(q);
        var min = await MinMarginAsync();
        if (q.MarginPct < min) { q.Status = QuoteStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Lợi nhuận {q.MarginPct:0.#}% thấp hơn mức tối thiểu {min:0.#}% – đã chuyển Ban Giám đốc duyệt."; }
        q.Status = QuoteStatus.Approved; q.ApprovedById = _user.Id; await _db.SaveChangesAsync();
        return $"Lợi nhuận {q.MarginPct:0.#}% đạt yêu cầu, báo giá đã được duyệt.";
    }

    public async Task ApproveAsync(SalesQuote q, bool approve)
    {
        if (q.Status != QuoteStatus.PendingApproval) throw new BusinessException("Báo giá không ở trạng thái chờ duyệt.");
        var u = _user.Principal;
        if (u != null && !u.InAny(AppRoles.Approvers)) throw new BusinessException("Chỉ Ban Giám đốc / Quản trị duyệt báo giá có lợi nhuận thấp.");
        q.Status = approve ? QuoteStatus.Approved : QuoteStatus.Draft; if (approve) q.ApprovedById = _user.Id;
        await _db.SaveChangesAsync();
    }

    public async Task<SalesOrder> ToOrderAsync(SalesQuote q, int warehouseId)
    {
        if (q.Status is not (QuoteStatus.Approved or QuoteStatus.Sent)) throw new BusinessException("Báo giá cần được duyệt trước khi lập đơn bán.");
        if (q.CustomerId == null) throw new BusinessException("Báo giá chưa có khách hàng.");
        var so = new SalesOrder { CustomerId = q.CustomerId, WarehouseId = warehouseId, Date = DateTime.Today, DeliveryDate = DateTime.Today.AddDays(5), CreatedById = _user.Id, SalespersonId = q.SalespersonId ?? _user.Id, QuoteId = q.Id,
            Note = $"Từ báo giá {q.Number}", Number = await DocNumbers.NextAsync(_db.SalesOrders.Select(x => x.Number), "DBH", DateTime.Today) };
        foreach (var l in q.Lines) so.Lines.Add(new SalesOrderLine { ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPct = l.DiscountPct, VatRate = l.VatRate });
        SalesService.Recalc(so);
        _db.SalesOrders.Add(so);
        await _db.SaveChangesAsync();
        q.Status = QuoteStatus.Converted; q.SalesOrderId = so.Id;
        await _db.SaveChangesAsync();
        return so;
    }
}

// =====================================================================
//  HOA HỒNG & KPI BÁN HÀNG
// =====================================================================
public record SalesPerformance(AppUser User, decimal Revenue, decimal Returns, decimal Net, decimal Target, decimal AchievePct, decimal Rate, decimal Commission, int Orders, decimal Collected);

public class CommissionService
{
    private readonly ErpDbContext _db;
    public CommissionService(ErpDbContext db) => _db = db;

    /// <summary>Doanh số tháng của từng nhân viên kinh doanh = hóa đơn bán đã phát hành của đơn bán do họ phụ trách (trừ hóa đơn điều chỉnh giảm); hoa hồng theo bậc doanh số</summary>
    public async Task<List<SalesPerformance>> MonthlyAsync(int year, int month)
    {
        var f = new DateTime(year, month, 1); var t = f.AddMonths(1).AddDays(-1);
        var invs = await _db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.SalesOrderId != null && i.Date >= f && i.Date <= t).Select(i => new { i.Id, i.SalesOrderId, i.Subtotal, i.PaidAmount, i.Total }).ToListAsync();
        var soIds = invs.Select(i => i.SalesOrderId!.Value).Distinct().ToList();
        var owners = await _db.SalesOrders.Where(s => soIds.Contains(s.Id)).Select(s => new { s.Id, Owner = s.SalespersonId ?? s.CreatedById }).ToDictionaryAsync(x => x.Id, x => x.Owner);
        var credits = await _db.SalesCreditNotes.Where(n => n.Status == DocStatus.Posted && n.Date >= f && n.Date <= t).Select(n => new { n.Invoice!.SalesOrderId, n.Subtotal }).ToListAsync();
        var users = await _db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Sales || owners.Values.Contains(u.Id))).ToListAsync();
        var targets = await _db.SalesTargets.Where(x => x.Year == year && x.Month == month).ToDictionaryAsync(x => x.UserId, x => x.Amount);
        var tiers = await _db.CommissionTiers.Where(x => x.IsActive).ToListAsync();
        var res = new List<SalesPerformance>();
        foreach (var u in users)
        {
            var mine = invs.Where(i => owners.GetValueOrDefault(i.SalesOrderId!.Value) == u.Id).ToList();
            var rev = mine.Sum(i => i.Subtotal);
            var ret = credits.Where(c => c.SalesOrderId != null && owners.GetValueOrDefault(c.SalesOrderId.Value) == u.Id).Sum(c => c.Subtotal);
            var net = rev - ret;
            var applicable = tiers.Where(x => (x.UserId == u.Id || (x.UserId == null && (x.Department == null || x.Department == u.Department))) && x.MinRevenue <= net)
                .OrderByDescending(x => x.UserId != null).ThenByDescending(x => x.MinRevenue).FirstOrDefault();
            var rate = applicable?.RatePct ?? 0; var target = targets.GetValueOrDefault(u.Id);
            res.Add(new SalesPerformance(u, rev, ret, net, target, target == 0 ? 0 : Math.Round(net * 100 / target, 1), rate, Math.Round(net * rate / 100, 0), mine.Count, mine.Sum(i => i.Total == 0 ? 0 : i.PaidAmount * i.Subtotal / i.Total)));
        }
        return res.Where(r => r.Revenue != 0 || r.Target != 0).OrderByDescending(r => r.Net).ToList();
    }
}
