using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Trả hàng nhà cung cấp, đồng bộ kho – mua hàng – công nợ – thuế – sổ cái:
///  1. Phiếu xuất kho trả hàng (Nợ 331 / Có 15x theo giá vốn bình quân) – do InventoryService ghi sổ.
///  2. Bút toán điều chỉnh: chênh lệch giá mua so với giá vốn (Nợ / Có TK chênh lệch) và hoàn thuế GTGT đầu vào (Nợ 331 / Có 133) cho phần đã có hóa đơn
///     → tổng giảm 331 = giá mua + thuế, đúng bằng giá trị nhà cung cấp ghi giảm.
///  3. Đơn mua: phần chưa lập hóa đơn giảm số lượng phải lập hóa đơn (ReturnedQty); phần đã lập hóa đơn được cấn trừ vào công nợ hóa đơn (InvoiceOffset, Invoice.PaidAmount).
///  4. Phần giảm trừ không cấn trừ được (hóa đơn đã trả hết) ghi nhận là nhà cung cấp còn nợ lại (Unapplied).
/// </summary>
public class PurchaseReturnService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    public PurchaseReturnService(ErpDbContext db, InventoryService inv, AccountingService acc, CurrentUser user) { _db = db; _inv = inv; _acc = acc; _user = user; }

    private record Alloc(PurchaseOrderLine Line, decimal Qty, decimal Unbilled, decimal Billed);

    /// <summary>Lập và ghi sổ phiếu xuất trả hàng cho NCR, đồng thời ghi nhận đủ các tác động lên đơn mua, hóa đơn, thuế</summary>
    public async Task<InventoryDoc> ReturnAsync(QcNonconformance n, DateTime date)
    {
        var vendor = n.PartnerId.HasValue ? await _db.Partners.FindAsync(n.PartnerId) : null;
        if (vendor == null || !vendor.IsVendor) throw new BusinessException("Trả lại nhà cung cấp: chọn nhà cung cấp trên NCR.");
        var item = await _db.Items.FindAsync(n.ItemId) ?? throw new BusinessException("Không tìm thấy mặt hàng.");
        var allocs = await AllocateAsync(n, vendor, item);

        // 1. Phiếu xuất kho: giảm tồn theo giá vốn bình quân, Nợ 331 (kèm đối tượng) / Có 15x
        var doc = new InventoryDoc
        {
            Type = InvDocType.Issue, Date = date.Date, WarehouseId = n.WarehouseId!.Value, Origin = DocOrigin.Quality, OriginRef = n.Number, OriginId = n.Id,
            PartnerId = vendor.Id, CounterAccountId = await _acc.RuleAsync(PostingCatalog.QcReturnPayable),
            Reason = $"Trả lại {vendor.Name} hàng không đạt theo {n.Number}", CreatedById = _user.Id, Number = await _inv.NextNumberAsync(InvDocType.Issue, date.Date)
        };
        doc.Lines.Add(new InventoryDocLine { ItemId = item.Id, Quantity = n.Quantity });
        await _inv.PostAsync(doc);
        var stockValue = doc.Lines.Sum(l => l.Amount);

        // 2. Giá trị theo giá mua và thuế hoàn
        var returns = new List<PurchaseReturn>();
        foreach (var a in allocs)
        {
            var p = a.Line.UnitPrice;
            var unbilledAmt = Math.Round(a.Unbilled * p, 0);
            var billedAmt = Math.Round(a.Billed * p, 0);
            returns.Add(new PurchaseReturn
            {
                NcrId = n.Id, InventoryDocId = doc.Id, PurchaseOrderLineId = a.Line.Id, PurchaseOrderLine = a.Line, Date = date.Date,
                Quantity = a.Qty, QtyUnbilled = a.Unbilled, QtyBilled = a.Billed, UnitPrice = p,
                Amount = unbilledAmt + billedAmt, BilledAmount = billedAmt, VatAmount = Math.Round(billedAmt * a.Line.VatRate / 100m, 0)
            });
        }
        var priceTotal = returns.Sum(r => r.Amount);
        var vatTotal = returns.Sum(r => r.VatAmount);
        var variance = priceTotal - stockValue; // > 0: giá mua cao hơn giá vốn kho → công nợ giảm nhiều hơn tồn kho
        var adj = new List<JLine>();
        if (variance != 0)
        {
            var varAcc = await _acc.RuleAsync(PostingCatalog.QcReturnVariance);
            var ap = await _acc.RuleAsync(PostingCatalog.QcReturnPayable);
            if (variance > 0) { adj.Add(new JLine(ap, variance, 0, vendor.Id)); adj.Add(new JLine(varAcc, 0, variance)); }
            else { adj.Add(new JLine(varAcc, -variance, 0)); adj.Add(new JLine(ap, 0, -variance, vendor.Id)); }
        }
        if (vatTotal > 0)
        {
            adj.Add(new JLine(await _acc.RuleAsync(PostingCatalog.QcReturnPayable), vatTotal, 0, vendor.Id));
            adj.Add(new JLine(await _acc.RuleAsync(PostingCatalog.PiVatIn), 0, vatTotal));
        }
        JournalEntry? je = null;
        if (adj.Any())
            je = await _acc.PostAsync(date.Date, $"Điều chỉnh giá trị & thuế GTGT trả hàng {doc.Number} – {vendor.Name}", JournalSource.PurchaseInvoice, doc.Number, doc.Id, adj);

        // 3. Đơn mua & cấn trừ hóa đơn
        foreach (var r in returns)
        {
            r.AdjustJournalEntryId = je?.Id;
            r.PurchaseOrderLine!.ReturnedQty += r.QtyUnbilled;
            var credit = r.BilledAmount + r.VatAmount;
            if (credit > 0) await OffsetInvoicesAsync(r, credit);
            _db.PurchaseReturns.Add(r);
        }
        foreach (var po in returns.Select(r => r.PurchaseOrderLine!.PurchaseOrderId).Distinct())
        {
            var order = await _db.PurchaseOrders.Include(p => p.Lines).FirstAsync(p => p.Id == po);
            PurchaseService.RefreshStatus(order);
        }
        n.InventoryDocId = doc.Id;
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Phân bổ số lượng trả vào các dòng đơn mua đã nhận của nhà cung cấp (ưu tiên đơn của phiếu kiểm tra, rồi mới nhất trước)</summary>
    private async Task<List<Alloc>> AllocateAsync(QcNonconformance n, Partner vendor, Item item)
    {
        int? preferredPo = null;
        if (n.InspectionId.HasValue)
        {
            var insp = await _db.QcInspections.AsNoTracking().FirstOrDefaultAsync(i => i.Id == n.InspectionId);
            if (insp is { Origin: DocOrigin.Purchase }) preferredPo = insp.OriginId;
        }
        var lines = await _db.PurchaseOrderLines.Include(l => l.PurchaseOrder)
            .Where(l => l.ItemId == item.Id && l.PurchaseOrder!.VendorId == vendor.Id && l.PurchaseOrder.Status != PoStatus.Cancelled && l.ReceivedQty > 0).ToListAsync();
        var returned = (await _db.PurchaseReturns.GroupBy(r => r.PurchaseOrderLineId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity), B = g.Sum(x => x.QtyBilled) }).ToListAsync()).ToDictionary(x => x.Key);
        var ordered = lines.OrderBy(l => l.PurchaseOrderId == preferredPo ? 0 : 1).ThenByDescending(l => l.PurchaseOrder!.Date).ThenByDescending(l => l.Id).ToList();

        var left = n.Quantity; var result = new List<Alloc>();
        foreach (var l in ordered)
        {
            if (left <= 0) break;
            var ret = returned.GetValueOrDefault(l.Id);
            var returnable = l.ReceivedQty - (ret?.Q ?? 0);
            if (returnable <= 0) continue;
            var q = Math.Min(left, returnable);
            // Phần chưa lập hóa đơn trả trước; phần còn lại (đã lập hóa đơn) cấn trừ công nợ
            var unbilledAvail = Math.Max(0, l.BillableQty);
            var u = Math.Min(q, unbilledAvail);
            result.Add(new Alloc(l, q, u, q - u));
            left -= q;
        }
        if (left > 0)
            throw new BusinessException($"Không thể trả lại {n.Quantity.ToQty()} {item.Unit} {item.Code} cho {vendor.Name}: chỉ còn {(n.Quantity - left).ToQty()} đã nhận theo các đơn mua " +
                "(chưa trừ phần đã trả). Hàng không có đơn mua của nhà cung cấp (ví dụ tồn đầu kỳ) hãy xử lý bằng nhập / xuất kho thủ công kèm bút toán.");
        return result;
    }

    /// <summary>Cấn trừ giá trị trả hàng đã lập hóa đơn vào công nợ các hóa đơn chứa dòng đơn mua đó; phần dư ghi nhận là chưa cấn trừ</summary>
    private async Task OffsetInvoicesAsync(PurchaseReturn r, decimal credit)
    {
        var invs = await _db.InvoiceLines.Where(l => l.SourceLineId == r.PurchaseOrderLineId && l.Invoice!.Direction == InvoiceDirection.Purchase && l.Invoice.Status == InvoiceStatus.Issued)
            .Select(l => l.Invoice!).Distinct().OrderBy(i => i.Date).ThenBy(i => i.Id).ToListAsync();
        var left = credit;
        foreach (var inv in invs)
        {
            if (left <= 0) break;
            var open = inv.Total - inv.PaidAmount;
            if (open <= 0) continue;
            var a = Math.Min(left, open);
            inv.PaidAmount += a; left -= a;
            r.Offsets.Add(new InvoiceOffset { InvoiceId = inv.Id, Amount = a });
        }
        r.Unapplied = left;
    }

    /// <summary>
    /// Dữ liệu cũ: phiếu trả hàng NCR lập trước khi có cơ chế này chỉ giảm TK 331 theo giá vốn kho.
    /// Bổ sung phiếu trả hàng theo dòng đơn mua, bút toán điều chỉnh giá / thuế và cấn trừ hóa đơn (không lập lại phiếu kho).
    /// </summary>
    public async Task<int> BackfillLegacyAsync()
    {
        var done = (await _db.PurchaseReturns.Select(r => r.NcrId).Distinct().ToListAsync()).ToHashSet();
        var legacy = await _db.QcNonconformances.Include(n => n.InventoryDoc).ThenInclude(d => d!.Lines)
            .Where(n => n.Disposition == NcrDisposition.ReturnToVendor && n.InventoryDocId != null).ToListAsync();
        var count = 0;
        foreach (var n in legacy.Where(n => !done.Contains(n.Id)).OrderBy(n => n.Id))
        {
            var vendor = await _db.Partners.FindAsync(n.PartnerId);
            var item = await _db.Items.FindAsync(n.ItemId);
            if (vendor == null || item == null) continue;
            List<Alloc> allocs;
            try { allocs = await AllocateAsync(n, vendor, item); }
            catch (BusinessException) { continue; } // không xác định được đơn mua: giữ nguyên để đối chiếu báo lệch
            var doc = n.InventoryDoc!;
            var returns = allocs.Select(a =>
            {
                var unbilled = Math.Round(a.Unbilled * a.Line.UnitPrice, 0); var billed = Math.Round(a.Billed * a.Line.UnitPrice, 0);
                return new PurchaseReturn
                {
                    NcrId = n.Id, InventoryDocId = doc.Id, PurchaseOrderLineId = a.Line.Id, PurchaseOrderLine = a.Line, Date = doc.Date, Quantity = a.Qty, QtyUnbilled = a.Unbilled,
                    QtyBilled = a.Billed, UnitPrice = a.Line.UnitPrice, Amount = unbilled + billed, BilledAmount = billed, VatAmount = Math.Round(billed * a.Line.VatRate / 100m, 0)
                };
            }).ToList();
            var stockValue = doc.Lines.Sum(l => l.Amount);
            var variance = returns.Sum(r => r.Amount) - stockValue; var vat = returns.Sum(r => r.VatAmount);
            var adj = new List<JLine>();
            var ap = await _acc.RuleAsync(PostingCatalog.QcReturnPayable); var varAcc = await _acc.RuleAsync(PostingCatalog.QcReturnVariance);
            if (variance > 0) { adj.Add(new JLine(ap, variance, 0, vendor.Id)); adj.Add(new JLine(varAcc, 0, variance)); }
            else if (variance < 0) { adj.Add(new JLine(varAcc, -variance, 0)); adj.Add(new JLine(ap, 0, -variance, vendor.Id)); }
            if (vat > 0) { adj.Add(new JLine(ap, vat, 0, vendor.Id)); adj.Add(new JLine(await _acc.RuleAsync(PostingCatalog.PiVatIn), 0, vat)); }
            // Ngày điều chỉnh: ngày phiếu nếu kỳ còn mở, ngược lại hôm nay
            var closed = await _db.FiscalPeriods.AnyAsync(p => p.Year == doc.Date.Year && p.Month == doc.Date.Month && p.IsClosed);
            JournalEntry? je = adj.Any() ? await _acc.PostAsync(closed ? DateTime.Today : doc.Date, $"Điều chỉnh giá trị & thuế GTGT trả hàng {doc.Number} (bổ sung)", JournalSource.PurchaseInvoice, doc.Number, doc.Id, adj) : null;
            foreach (var r in returns)
            {
                r.AdjustJournalEntryId = je?.Id; r.PurchaseOrderLine!.ReturnedQty += r.QtyUnbilled;
                if (r.BilledAmount + r.VatAmount > 0) await OffsetInvoicesAsync(r, r.BilledAmount + r.VatAmount);
                _db.PurchaseReturns.Add(r);
            }
            await _db.SaveChangesAsync();
            foreach (var po in returns.Select(r => r.PurchaseOrderLine!.PurchaseOrderId).Distinct())
            {
                var order = await _db.PurchaseOrders.Include(p => p.Lines).FirstAsync(p => p.Id == po);
                PurchaseService.RefreshStatus(order);
            }
            await _db.SaveChangesAsync();
            count++;
        }
        return count;
    }
}
