using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

public class CreditLineInput
{
    public int InvoiceLineId { get; set; }
    /// <summary>Số lượng trả lại (loại Hàng bán bị trả lại)</summary>
    public decimal Quantity { get; set; }
    /// <summary>Số tiền giảm chưa thuế (loại Giảm giá hàng bán)</summary>
    public decimal Amount { get; set; }
}

public class CreditNoteForm
{
    public int InvoiceId { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    public CreditNoteKind Kind { get; set; } = CreditNoteKind.Return;
    public int? WarehouseId { get; set; }
    public string Reason { get; set; } = "";
    public List<CreditLineInput> Lines { get; set; } = new();
}

/// <summary>Số lượng / giá trị của một dòng hóa đơn đã được điều chỉnh giảm</summary>
public record CreditedLine(decimal Qty, decimal Amount, decimal Vat);

/// <summary>
/// Hóa đơn điều chỉnh giảm bán hàng (hàng bán bị trả lại, giảm giá hàng bán), đồng bộ kho – doanh thu – thuế – công nợ – sổ cái:
///  1. Hàng trả lại nhập kho theo giá vốn lúc bán: Nợ 15x / Có 632 (phiếu nhập kho, nguồn "Hàng bán bị trả lại").
///  2. Giảm trừ doanh thu: Nợ 5212 (hoặc 5213) + Nợ 33311 / Có 131 theo đối tượng.
///  3. Cấn trừ vào công nợ hóa đơn gốc (Invoice.PaidAmount); hóa đơn đã thanh toán hết thì ghi nhận Unapplied (khách còn được ghi có).
/// </summary>
public class SalesCreditService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;
    private readonly EInvoiceService _einv;
    public SalesCreditService(ErpDbContext db, InventoryService inv, AccountingService acc, CurrentUser user, EInvoiceService einv) { _db = db; _inv = inv; _acc = acc; _user = user; _einv = einv; }

    /// <summary>Đã điều chỉnh giảm theo từng dòng hóa đơn (chứng từ chưa hủy)</summary>
    public async Task<Dictionary<int, CreditedLine>> CreditedAsync(int invoiceId) =>
        (await _db.SalesCreditLines.AsNoTracking().Where(l => l.SalesCreditNote!.InvoiceId == invoiceId && l.SalesCreditNote.Status == DocStatus.Posted)
            .GroupBy(l => l.InvoiceLineId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity), A = g.Sum(x => x.Amount), V = g.Sum(x => x.VatAmount) }).ToListAsync())
        .ToDictionary(x => x.Key, x => new CreditedLine(x.Q, x.A, x.V));

    /// <summary>Giá vốn đơn vị lúc bán: bình quân các phiếu xuất bán của đơn (chưa hủy); không có thì dùng giá vốn hiện tại</summary>
    private async Task<decimal> SaleCostAsync(int? salesOrderId, Item item)
    {
        if (salesOrderId.HasValue)
        {
            var lines = await _db.InventoryDocLines.Where(l => l.ItemId == item.Id && l.InventoryDoc!.Origin == DocOrigin.Sales && l.InventoryDoc.OriginId == salesOrderId
                && l.InventoryDoc.Type == InvDocType.Issue && l.InventoryDoc.Status == DocStatus.Posted).Select(l => new { l.Quantity, l.Amount }).ToListAsync();
            var q = lines.Sum(x => x.Quantity);
            if (q > 0) return lines.Sum(x => x.Amount) / q;
        }
        return item.AvgCost;
    }

    public async Task<SalesCreditNote> CreateAsync(CreditNoteForm f)
    {
        var inv = await _db.Invoices.Include(i => i.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(i => i.Id == f.InvoiceId) ?? throw new BusinessException("Không tìm thấy hóa đơn gốc.");
        if (inv.Direction != InvoiceDirection.Sales || inv.Status != InvoiceStatus.Issued) throw new BusinessException("Chỉ điều chỉnh giảm hóa đơn bán đã phát hành.");
        if (inv.Series == "DK") throw new BusinessException("Hóa đơn công nợ đầu kỳ không lập điều chỉnh giảm.");
        if (string.IsNullOrWhiteSpace(f.Reason)) throw new BusinessException("Nhập lý do điều chỉnh.");
        var date = f.Date.Date;
        if (date < inv.Date.Date) throw new BusinessException("Ngày điều chỉnh không được trước ngày hóa đơn gốc.");
        await _acc.EnsurePeriodOpenAsync(date);

        var credited = await CreditedAsync(inv.Id);
        var lines = new List<SalesCreditLine>();
        foreach (var inp in f.Lines.Where(x => x.Quantity > 0 || x.Amount > 0))
        {
            var il = inv.Lines.FirstOrDefault(l => l.Id == inp.InvoiceLineId) ?? throw new BusinessException("Dòng hóa đơn không hợp lệ.");
            var c = credited.GetValueOrDefault(il.Id) ?? new CreditedLine(0, 0, 0);
            var remQty = il.Quantity - c.Qty; var remAmt = il.Amount - c.Amount; var remVat = il.VatAmount - c.Vat;
            decimal qty = 0, amt;
            if (f.Kind == CreditNoteKind.Return)
            {
                if (il.Item == null || !il.Item.IsStocked) throw new BusinessException($"Dòng \"{il.Description}\" là dịch vụ / không quản lý kho: dùng loại Giảm giá hàng bán.");
                qty = inp.Quantity;
                if (qty <= 0 || qty > remQty) throw new BusinessException($"Số lượng trả của \"{il.Description}\" phải từ 0 đến {remQty.ToQty()} (số lượng còn lại chưa điều chỉnh).");
                amt = qty == remQty ? remAmt : Math.Min(remAmt, Math.Round(qty * il.UnitPrice, 0));
            }
            else
            {
                amt = Math.Round(inp.Amount, 0);
                if (amt <= 0 || amt > remAmt) throw new BusinessException($"Số tiền giảm của \"{il.Description}\" phải từ 0 đến {remAmt.ToMoney()} (giá trị còn lại chưa điều chỉnh).");
            }
            var vat = amt == remAmt ? remVat : Math.Round(amt * il.VatRate / 100m, 0);
            lines.Add(new SalesCreditLine { InvoiceLineId = il.Id, ItemId = il.ItemId, Description = il.Description, Quantity = qty, UnitPrice = il.UnitPrice, Amount = amt, VatRate = il.VatRate, VatAmount = vat });
        }
        if (!lines.Any()) throw new BusinessException("Chọn ít nhất một dòng và nhập số lượng / số tiền điều chỉnh.");

        int? warehouseId = null;
        if (f.Kind == CreditNoteKind.Return)
        {
            warehouseId = f.WarehouseId ?? (inv.SalesOrderId.HasValue ? (await _db.SalesOrders.FindAsync(inv.SalesOrderId))?.WarehouseId : null);
            if (warehouseId == null) throw new BusinessException("Chọn kho nhận hàng trả lại.");
        }

        var note = new SalesCreditNote
        {
            Number = await DocNumbers.NextAsync(_db.SalesCreditNotes.Select(x => x.Number), f.Kind == CreditNoteKind.Return ? "TLB" : "GGB", date),
            Date = date, Kind = f.Kind, CustomerId = inv.PartnerId!.Value, InvoiceId = inv.Id, WarehouseId = warehouseId, Reason = f.Reason.Trim(), CreatedById = _user.Id
        };
        foreach (var l in lines) note.Lines.Add(l);
        note.Subtotal = lines.Sum(l => l.Amount); note.VatAmount = lines.Sum(l => l.VatAmount); note.Total = note.Subtotal + note.VatAmount;
        _db.SalesCreditNotes.Add(note);
        await _db.SaveChangesAsync();

        // 1. Nhập lại kho theo giá vốn lúc bán
        if (f.Kind == CreditNoteKind.Return)
        {
            var doc = new InventoryDoc
            {
                Type = InvDocType.Receipt, Date = date, WarehouseId = warehouseId!.Value, Origin = DocOrigin.SalesReturn, OriginRef = note.Number, OriginId = note.Id,
                PartnerId = inv.PartnerId, CounterAccountId = await _acc.RuleAsync(PostingCatalog.SdCogs), CreatedById = _user.Id,
                Reason = $"Nhập lại hàng bán bị trả lại theo {note.Number} (HĐ {inv.Series} {inv.InvoiceNo})", Number = await _inv.NextNumberAsync(InvDocType.Receipt, date)
            };
            foreach (var l in lines.Where(x => x.Quantity > 0))
            {
                l.UnitCost = await SaleCostAsync(inv.SalesOrderId, inv.Lines.First(x => x.Id == l.InvoiceLineId).Item!);
                doc.Lines.Add(new InventoryDocLine { ItemId = l.ItemId!.Value, Quantity = l.Quantity, UnitCost = l.UnitCost });
            }
            await _inv.PostAsync(doc);
            note.InventoryDocId = doc.Id;
        }

        // 2. Giảm trừ doanh thu, thuế đầu ra, công nợ phải thu
        var revenue = await _acc.RuleAsync(f.Kind == CreditNoteKind.Return ? PostingCatalog.CnSalesReturn : PostingCatalog.CnPriceReduction);
        var jl = new List<JLine> { new(revenue, note.Subtotal, 0, null, note.Number) };
        if (note.VatAmount > 0) jl.Add(new JLine(await _acc.RuleAsync(PostingCatalog.SiVatOut), note.VatAmount, 0));
        jl.Add(new JLine(await _acc.RuleAsync(PostingCatalog.SiReceivable), 0, note.Total, inv.PartnerId, $"Điều chỉnh giảm HĐ {inv.InvoiceNo}"));
        var je = await _acc.PostAsync(date, $"{f.Kind.GetDisplayName()} {note.Number} – HĐ {inv.Series} {inv.InvoiceNo}", JournalSource.SalesInvoice, note.Number, note.Id, jl);
        note.JournalEntryId = je.Id;

        // 3. Cấn trừ vào hóa đơn gốc
        var applied = Math.Min(note.Total, Math.Max(0, inv.Total - inv.PaidAmount));
        if (applied > 0) { inv.PaidAmount += applied; note.Offsets.Add(new SalesCreditOffset { InvoiceId = inv.Id, Amount = applied }); }
        note.Unapplied = note.Total - applied;
        note.EInvoiceLookupCode = await _einv.AdjustAsync(inv, note);
        await _db.SaveChangesAsync();
        return note;
    }

    /// <summary>Hủy hóa đơn điều chỉnh giảm: đảo bút toán, rút lại hàng đã nhập kho (cần đủ tồn), trả lại công nợ hóa đơn gốc</summary>
    public async Task CancelAsync(SalesCreditNote note, string reason)
    {
        if (note.Status != DocStatus.Posted) throw new BusinessException("Chứng từ đã hủy.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do hủy.");
        var date = DateTime.Today;
        await _acc.EnsurePeriodOpenAsync(date);
        if (note.InventoryDocId.HasValue)
        {
            var doc = await _db.InventoryDocs.Include(d => d.Lines).FirstAsync(d => d.Id == note.InventoryDocId);
            if (doc.Status == DocStatus.Posted) await _inv.ReverseAsync(doc, date, $"Hủy {note.Number}: {reason}", viaOwner: true);
        }
        await _acc.ReverseAsync(note.JournalEntryId, date, $"Hủy {note.Number}: {reason}");
        if (note.Offsets.Count == 0) await _db.Entry(note).Collection(n => n.Offsets).LoadAsync();
        foreach (var o in note.Offsets)
        {
            var inv = await _db.Invoices.FindAsync(o.InvoiceId);
            if (inv != null) inv.PaidAmount = Math.Max(0, inv.PaidAmount - o.Amount);
        }
        note.Status = DocStatus.Cancelled;
        note.Reason += $" [Hủy {date:dd/MM/yyyy}: {reason}]";
        await _db.SaveChangesAsync();
    }
}
