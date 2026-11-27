using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Ghi sổ phiếu kho: cập nhật tồn kho theo kho, thẻ kho, giá vốn bình quân gia quyền di động và bút toán kế toán.
///  – Nhập mua:   Nợ 152/155/1561 / Có 331       – Nhập thành phẩm SX: Nợ 155 / Có 154
///  – Xuất bán:   Nợ 632 / Có 15x                 – Xuất cho SX:        Nợ 154 / Có 152
///  – Nhập / xuất thủ công: đối ứng TK chọn trên phiếu
///  – Kiểm kê: thừa Nợ 15x / Có 3381; thiếu Nợ 1381 / Có 15x       – Chuyển kho: không hạch toán
/// </summary>
public class InventoryService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    public InventoryService(ErpDbContext db, AccountingService acc) { _db = db; _acc = acc; }

    public static string Prefix(InvDocType t) => t switch { InvDocType.Receipt => "PN", InvDocType.Issue => "PX", InvDocType.Transfer => "CK", _ => "KK" };

    public Task<string> NextNumberAsync(InvDocType t, DateTime date) =>
        DocNumbers.NextAsync(_db.InventoryDocs.Select(d => d.Number), Prefix(t), date);

    /// <summary>Tồn kho hiện tại theo kho (dictionary ItemId → Qty)</summary>
    public async Task<Dictionary<int, decimal>> OnHandAsync(int warehouseId, IEnumerable<int> itemIds)
    {
        var ids = itemIds.Distinct().ToList();
        return (await _db.StockLevels.Where(s => s.WarehouseId == warehouseId && ids.Contains(s.ItemId)).ToListAsync())
            .GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
    }

    /// <summary>Ghi sổ phiếu kho. Phiếu phải đã có Lines và đang ở trạng thái Nháp.</summary>
    public async Task PostAsync(InventoryDoc doc)
    {
        if (doc.Status != DocStatus.Draft) throw new BusinessException("Phiếu đã được ghi sổ hoặc đã hủy.");
        await _acc.EnsurePeriodOpenAsync(doc.Date);
        if (!doc.Lines.Any()) throw new BusinessException("Phiếu chưa có dòng hàng.");
        if (doc.Type == InvDocType.Transfer && (doc.ToWarehouseId == null || doc.ToWarehouseId == doc.WarehouseId))
            throw new BusinessException("Chọn kho nhận khác kho xuất.");

        var itemIds = doc.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = (await _db.Items.Where(i => itemIds.Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id);
        if (items.Values.Any(i => !i.IsStocked)) throw new BusinessException("Hàng dịch vụ không quản lý tồn kho.");
        // Nạp tồn kho mọi kho của các mặt hàng liên quan (xử lý trong bộ nhớ để tránh trùng dòng khi chưa lưu)
        var tracker = new StockTracker(_db, doc.Date, doc.Number, doc.Type.GetDisplayName(), doc.PartnerId, doc.OriginRef);
        await tracker.LoadAsync(itemIds);
        var allowExpired = !(doc.Type == InvDocType.Issue && doc.Origin is DocOrigin.Sales or DocOrigin.Production);
        foreach (var b in doc.Lines.Where(x => x.BinId != null || x.ToBinId != null))
        {
            var ids = new[] { b.BinId, b.ToBinId }.Where(x => x != null).Select(x => x!.Value).ToList();
            var bl = await _db.BinLocations.Where(x => ids.Contains(x.Id)).ToListAsync();
            if (b.BinId != null && bl.FirstOrDefault(x => x.Id == b.BinId)?.WarehouseId != doc.WarehouseId) throw new BusinessException("Vị trí lưu kho không thuộc kho của phiếu.");
            if (b.ToBinId != null && bl.FirstOrDefault(x => x.Id == b.ToBinId)?.WarehouseId != (doc.Type == InvDocType.Transfer ? doc.ToWarehouseId : doc.WarehouseId)) throw new BusinessException("Vị trí nhận không thuộc kho nhận.");
        }
        var levels = await _db.StockLevels.Where(s => itemIds.Contains(s.ItemId)).ToListAsync();

        StockLevel Level(int itemId, int whId)
        {
            var lv = levels.FirstOrDefault(s => s.ItemId == itemId && s.WarehouseId == whId);
            if (lv == null) { lv = new StockLevel { ItemId = itemId, WarehouseId = whId }; levels.Add(lv); _db.StockLevels.Add(lv); }
            return lv;
        }
        decimal TotalQty(int itemId) => levels.Where(s => s.ItemId == itemId).Sum(s => s.Quantity);

        // Kiểm tra trước khi thay đổi dữ liệu: số lượng hợp lệ, đủ tồn cho phiếu xuất / chuyển
        if (doc.Type != InvDocType.Adjustment && doc.Lines.Any(l => l.Quantity <= 0)) throw new BusinessException("Số lượng phải lớn hơn 0.");
        if (doc.Type is InvDocType.Issue or InvDocType.Transfer)
        {
            var lack = doc.Lines.GroupBy(l => l.ItemId)
                .Select(g => (Item: items[g.Key], Need: g.Sum(x => x.Quantity), Have: levels.Where(s => s.ItemId == g.Key && s.WarehouseId == doc.WarehouseId).Sum(s => s.Quantity)))
                .Where(x => x.Have < x.Need).Select(x => $"{x.Item.Code} (tồn {x.Have.ToQty()}, cần {x.Need.ToQty()})").ToList();
            if (lack.Any()) throw new BusinessException("Không đủ tồn kho: " + string.Join("; ", lack));
        }

        // Kiểm kê: tính chênh lệch = thực tế − sổ sách
        if (doc.Type == InvDocType.Adjustment)
            foreach (var l in doc.Lines) l.Quantity = (l.CountedQty ?? 0) - Level(l.ItemId, doc.WarehouseId).Quantity;

        var moves = new List<StockMove>();
        void Move(InventoryDocLine l, int whId, decimal qty, decimal unitCost, decimal? amount = null) => moves.Add(new StockMove
        {
            Date = doc.Date, ItemId = l.ItemId, WarehouseId = whId, Quantity = qty, UnitCost = unitCost,
            Amount = amount ?? Math.Round(qty * unitCost, 0), DocNumber = doc.Number, Description = doc.Reason
        });

        var shortages = new List<string>();
        foreach (var l in doc.Lines)
        {
            var item = items[l.ItemId];
            var isIn = doc.Type == InvDocType.Receipt || (doc.Type == InvDocType.Adjustment && l.Quantity > 0);
            if (doc.Type != InvDocType.Adjustment && l.Quantity <= 0) throw new BusinessException($"Số lượng của {item.Code} phải lớn hơn 0.");
            if (doc.Type == InvDocType.Adjustment && l.Quantity == 0) { l.UnitCost = item.AvgCost; l.Amount = 0; continue; }

            if (isIn)
            {
                var qty = Math.Abs(l.Quantity);
                var cost = doc.Type == InvDocType.Receipt ? l.UnitCost : item.AvgCost;
                if (cost < 0) throw new BusinessException("Đơn giá nhập không hợp lệ.");
                // Giá vốn bình quân gia quyền di động
                var onHand = TotalQty(item.Id);
                item.AvgCost = onHand + qty <= 0 ? cost : Math.Round(((onHand > 0 ? onHand * item.AvgCost : 0) + qty * cost) / ((onHand > 0 ? onHand : 0) + qty), 2);
                Level(item.Id, doc.WarehouseId).Quantity += qty;
                l.UnitCost = cost; l.Amount = Math.Round(qty * cost, 0);
                if (StockTracker.IsTracked(item)) tracker.AddLot(item, doc.WarehouseId, l.LotNo, l.MfgDate, l.ExpiryDate, qty, cost, doc.Number);
                tracker.AddBin(item, doc.WarehouseId, l.BinId, qty);
                Move(l, doc.WarehouseId, qty, cost);
            }
            else
            {
                var qty = Math.Abs(l.Quantity);
                var lv = Level(item.Id, doc.WarehouseId);
                if (lv.Quantity < qty) { shortages.Add($"{item.Code} (tồn {lv.Quantity.ToQty()}, cần {qty.ToQty()})"); continue; }
                decimal? lotAmount = null; var lotParts = new List<(StockLot Lot, decimal Qty)>();
                if (StockTracker.IsTracked(item))
                {
                    var (parts, rem) = tracker.Consume(item, doc.WarehouseId, qty, l.LotNo, allowExpired, lv.Quantity);
                    lotParts = parts;
                    if (item.CostMethod != CostMethod.Average) lotAmount = parts.Sum(p => p.Qty * p.Lot.UnitCost) + rem * item.AvgCost;
                }
                tracker.ConsumeBin(item, doc.WarehouseId, l.BinId, qty, strict: l.BinId != null);
                // Xuất hết toàn bộ tồn của mặt hàng (mọi kho): giá trị xuất = toàn bộ giá trị còn trên sổ kho, không để dư do làm tròn giá bình quân
                var amount = lotAmount.HasValue ? Math.Round(lotAmount.Value, 0) : Math.Round(qty * item.AvgCost, 0);
                if (doc.Type != InvDocType.Transfer && qty == TotalQty(item.Id))
                {
                    var book = await _db.StockMoves.Where(m => m.ItemId == item.Id).SumAsync(m => m.Amount) + moves.Where(m => m.ItemId == item.Id).Sum(m => m.Amount);
                    if (book >= 0) amount = book;
                }
                lv.Quantity -= qty;
                l.UnitCost = qty == 0 ? item.AvgCost : Math.Round(amount / qty, 2); l.Amount = amount;
                Move(l, doc.WarehouseId, -qty, item.AvgCost, doc.Type == InvDocType.Transfer ? null : -amount);
                if (doc.Type == InvDocType.Transfer)
                {
                    Level(item.Id, doc.ToWarehouseId!.Value).Quantity += qty;
                    foreach (var (pl, pq) in lotParts) tracker.AddLot(item, doc.ToWarehouseId!.Value, pl.LotNo, pl.MfgDate, pl.ExpiryDate, pq, pl.UnitCost, doc.Number);
                    tracker.AddBin(item, doc.ToWarehouseId!.Value, l.ToBinId, qty);
                    Move(l, doc.ToWarehouseId!.Value, qty, item.AvgCost);
                }
            }
        }
        if (shortages.Any()) throw new BusinessException("Không đủ tồn kho: " + string.Join("; ", shortages));

        // Bút toán
        if (doc.Type != InvDocType.Transfer)
        {
            var lines = new List<JLine>();
            foreach (var l in doc.Lines.Where(x => x.Amount != 0))
            {
                var inv = await _acc.InventoryRuleAsync(items[l.ItemId].Type);
                var amt = l.Amount;
                switch (doc.Type)
                {
                    case InvDocType.Receipt:
                        var cr = doc.Origin switch
                        {
                            DocOrigin.Purchase => await _acc.RuleAsync(PostingCatalog.PrPayable),
                            DocOrigin.Production => await _acc.RuleAsync(PostingCatalog.PrdWip),
                            _ => doc.CounterAccountId ?? throw new BusinessException("Chọn tài khoản đối ứng cho phiếu nhập.")
                        };
                        lines.Add(new JLine(inv, amt, 0));
                        lines.Add(new JLine(cr, 0, amt, doc.Origin == DocOrigin.Purchase ? doc.PartnerId : null));
                        break;
                    case InvDocType.Issue:
                        var dr = doc.Origin switch
                        {
                            DocOrigin.Sales => await _acc.RuleAsync(PostingCatalog.SdCogs),
                            DocOrigin.Production => await _acc.RuleAsync(PostingCatalog.PrdWip),
                            _ => doc.CounterAccountId ?? throw new BusinessException("Chọn tài khoản đối ứng cho phiếu xuất.")
                        };
                        // Xuất trả nhà cung cấp từ quản lý chất lượng: TK đối ứng công nợ cần đối tượng
                        lines.Add(new JLine(dr, amt, 0, doc.Origin == DocOrigin.Quality ? doc.PartnerId : null));
                        lines.Add(new JLine(inv, 0, amt));
                        break;
                    case InvDocType.Adjustment:
                        if (l.Quantity > 0) { lines.Add(new JLine(inv, amt, 0)); lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.ScSurplus), 0, amt)); }
                        else { lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.ScShortage), amt, 0)); lines.Add(new JLine(inv, 0, amt)); }
                        break;
                }
            }
            if (lines.Any())
            {
                if (doc.Id == 0) { _db.InventoryDocs.Add(doc); await _db.SaveChangesAsync(); }
                var je = await _acc.PostAsync(doc.Date, $"{doc.Type.GetDisplayName()} {doc.Number}" + (string.IsNullOrWhiteSpace(doc.Reason) ? "" : $" – {doc.Reason}"),
                    JournalSource.Inventory, doc.Number, doc.Id, lines);
                doc.JournalEntryId = je.Id;
            }
        }
        if (doc.Id == 0) _db.InventoryDocs.Add(doc);
        doc.Status = DocStatus.Posted;
        await _db.SaveChangesAsync();
        foreach (var m in moves) m.InventoryDocId = doc.Id;
        _db.StockMoves.AddRange(moves);
        foreach (var m in tracker.Moves) m.InventoryDocId = doc.Id;
        _db.StockLotMoves.AddRange(tracker.Moves);
        await _db.SaveChangesAsync();
    }

    /// <summary>Nguồn phiếu kho chỉ hủy được từ chứng từ gốc tương ứng (không hủy trực tiếp từ phiếu kho)</summary>
    public static string? OwnerCancelHint(InventoryDoc d) => d.Origin switch
    {
        DocOrigin.Production => "Phiếu phát sinh từ lệnh sản xuất: không hủy trực tiếp (lệnh đã xuất NVL / hoàn thành không hủy được).",
        DocOrigin.Quality => "Phiếu phát sinh từ xử lý hàng không đạt (NCR): không hủy trực tiếp.",
        DocOrigin.SalesReturn => "Phiếu nhập hàng bán bị trả lại: hủy từ hóa đơn điều chỉnh giảm tương ứng.",
        _ => null
    };

    /// <summary>
    /// Hủy (đảo) phiếu kho đã ghi sổ: trả tồn kho về trước phiếu (ghi thẻ kho bút toán đảo, không xóa lịch sử), đảo bút toán kế toán,
    /// và hoàn lại số lượng đã nhận / đã giao trên đơn mua / đơn bán gốc.
    /// Điều kiện: đủ tồn để rút lại (phiếu nhập), chưa lập hóa đơn / chưa trả hàng cho phần hàng đó (phải hủy hóa đơn trước).
    /// </summary>
    public async Task ReverseAsync(InventoryDoc doc, DateTime date, string reason, bool viaOwner = false)
    {
        if (doc.Status != DocStatus.Posted) throw new BusinessException("Chỉ hủy được phiếu đã ghi sổ.");
        if (!viaOwner && OwnerCancelHint(doc) is { } hint) throw new BusinessException(hint);
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do hủy phiếu.");
        date = date.Date;
        await _acc.EnsurePeriodOpenAsync(date);
        if (doc.Lines.Count == 0) await _db.Entry(doc).Collection(d => d.Lines).LoadAsync();

        var itemIds = doc.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = (await _db.Items.Where(i => itemIds.Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id);
        var levels = await _db.StockLevels.Where(s => itemIds.Contains(s.ItemId)).ToListAsync();
        StockLevel Level(int itemId, int whId)
        {
            var lv = levels.FirstOrDefault(s => s.ItemId == itemId && s.WarehouseId == whId);
            if (lv == null) { lv = new StockLevel { ItemId = itemId, WarehouseId = whId }; levels.Add(lv); _db.StockLevels.Add(lv); }
            return lv;
        }

        // Mỗi dòng đảo thành: hàng ra khỏi kho A hoặc vào kho B
        var ops = new List<(InventoryDocLine Line, int? OutWh, int? InWh, decimal Qty, decimal Value)>();
        foreach (var l in doc.Lines)
        {
            var qty = Math.Abs(l.Quantity);
            if (qty == 0) continue;
            switch (doc.Type)
            {
                case InvDocType.Receipt: ops.Add((l, doc.WarehouseId, null, qty, l.Amount)); break;
                case InvDocType.Issue: ops.Add((l, null, doc.WarehouseId, qty, l.Amount)); break;
                case InvDocType.Adjustment:
                    if (l.Quantity > 0) ops.Add((l, doc.WarehouseId, null, qty, l.Amount)); else ops.Add((l, null, doc.WarehouseId, qty, l.Amount));
                    break;
                case InvDocType.Transfer: ops.Add((l, doc.ToWarehouseId!.Value, doc.WarehouseId, qty, l.Amount)); break;
            }
        }
        // Đủ tồn để rút lại
        var lack = ops.Where(o => o.OutWh.HasValue).GroupBy(o => (o.Line.ItemId, o.OutWh!.Value))
            .Select(g => (Item: items[g.Key.ItemId], Need: g.Sum(x => x.Qty), Have: levels.Where(s => s.ItemId == g.Key.ItemId && s.WarehouseId == g.Key.Item2).Sum(s => s.Quantity)))
            .Where(x => x.Have < x.Need).Select(x => $"{x.Item.Code} (tồn {x.Have.ToQty()}, cần rút lại {x.Need.ToQty()})").ToList();
        if (lack.Any()) throw new BusinessException("Hàng của phiếu đã được xuất / chuyển đi, không đủ tồn để hủy: " + string.Join("; ", lack));

        await UnwindOrderAsync(doc);
        var tracker = new StockTracker(_db, date, doc.Number, doc.Type.GetDisplayName(), doc.PartnerId, doc.OriginRef);
        await tracker.LoadAsync(itemIds);
        await tracker.ReverseAsync(doc, items);
        foreach (var o in ops.Where(x => x.OutWh.HasValue && doc.Type != InvDocType.Transfer)) tracker.ConsumeBin(items[o.Line.ItemId], o.OutWh!.Value, null, o.Qty, strict: false);

        // Giá vốn bình quân tính lại theo tổng số lượng và giá trị còn trên sổ kho
        var qtyNow = new Dictionary<int, decimal>(); var valNow = new Dictionary<int, decimal>();
        foreach (var id in itemIds)
        {
            qtyNow[id] = levels.Where(s => s.ItemId == id).Sum(s => s.Quantity);
            valNow[id] = await _db.StockMoves.Where(m => m.ItemId == id).SumAsync(m => m.Amount);
        }
        var moves = new List<StockMove>();
        void Move(int itemId, int whId, decimal qty, decimal value) => moves.Add(new StockMove
        {
            Date = date, ItemId = itemId, WarehouseId = whId, Quantity = qty, Amount = value, UnitCost = qty == 0 ? 0 : Math.Round(Math.Abs(value / qty), 2),
            DocNumber = $"{doc.Number} (hủy)", Description = $"Hủy phiếu {doc.Number}: {reason}", InventoryDocId = doc.Id
        });
        foreach (var o in ops)
        {
            var item = items[o.Line.ItemId];
            if (o.OutWh.HasValue) { Level(item.Id, o.OutWh.Value).Quantity -= o.Qty; Move(item.Id, o.OutWh.Value, -o.Qty, -o.Value); }
            if (o.InWh.HasValue) { Level(item.Id, o.InWh.Value).Quantity += o.Qty; Move(item.Id, o.InWh.Value, o.Qty, o.Value); }
            if (doc.Type == InvDocType.Transfer) continue; // chuyển kho không đổi tổng tồn và giá vốn
            var sign = o.OutWh.HasValue ? -1 : 1;
            qtyNow[item.Id] += sign * o.Qty; valNow[item.Id] += sign * o.Value;
            if (qtyNow[item.Id] > 0 && valNow[item.Id] >= 0) item.AvgCost = Math.Round(valNow[item.Id] / qtyNow[item.Id], 2);
        }

        if (doc.JournalEntryId != null) await _acc.ReverseAsync(doc.JournalEntryId, date, $"Hủy phiếu {doc.Number}: {reason}");
        _db.StockMoves.AddRange(moves);
        _db.StockLotMoves.AddRange(tracker.Moves);
        doc.Status = DocStatus.Cancelled;
        var note = $" [Hủy {date:dd/MM/yyyy}: {reason}]";
        doc.Reason = ((doc.Reason ?? "") + note).Length <= 500 ? (doc.Reason ?? "") + note : (doc.Reason ?? "")[..Math.Max(0, 500 - note.Length)] + note;
        await _db.SaveChangesAsync();
    }

    /// <summary>Hoàn lại số lượng đã nhận / đã giao trên đơn gốc khi hủy phiếu; không cho hủy nếu phần hàng đó đã lập hóa đơn hoặc đã trả nhà cung cấp</summary>
    private async Task UnwindOrderAsync(InventoryDoc doc)
    {
        if (doc.Origin == DocOrigin.Purchase && doc.Type == InvDocType.Receipt && doc.OriginId.HasValue)
        {
            var po = await _db.PurchaseOrders.Include(p => p.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(p => p.Id == doc.OriginId);
            if (po == null) return;
            var returned = (await _db.PurchaseReturns.Where(r => r.PurchaseOrderLine!.PurchaseOrderId == po.Id).GroupBy(r => r.PurchaseOrderLineId)
                .Select(g => new { g.Key, Q = g.Sum(x => x.Quantity) }).ToListAsync()).ToDictionary(x => x.Key, x => x.Q);
            foreach (var l in doc.Lines.Where(x => x.Quantity > 0))
            {
                var left = l.Quantity;
                foreach (var pl in po.Lines.Where(x => x.ItemId == l.ItemId && x.ReceivedQty > 0).OrderByDescending(x => x.Id))
                {
                    if (left <= 0) break;
                    var take = Math.Min(left, pl.ReceivedQty);
                    pl.ReceivedQty -= take; left -= take;
                    if (pl.ReceivedQty < pl.InvoicedQty + pl.ReturnedQty || pl.ReceivedQty < returned.GetValueOrDefault(pl.Id))
                        throw new BusinessException($"Không hủy được: {pl.Item?.Code} đã lập hóa đơn mua hoặc đã trả lại nhà cung cấp. Hủy hóa đơn / xử lý trả hàng trước.");
                }
            }
            PurchaseService.RefreshStatus(po);
        }
        else if (doc.Origin == DocOrigin.Sales && doc.Type == InvDocType.Issue && doc.OriginId.HasValue)
        {
            var so = await _db.SalesOrders.Include(s => s.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(s => s.Id == doc.OriginId);
            if (so == null) return;
            foreach (var l in doc.Lines.Where(x => x.Quantity > 0))
            {
                var left = l.Quantity;
                foreach (var sl in so.Lines.Where(x => x.ItemId == l.ItemId && x.DeliveredQty > 0).OrderByDescending(x => x.Id))
                {
                    if (left <= 0) break;
                    var take = Math.Min(left, sl.DeliveredQty);
                    sl.DeliveredQty -= take; left -= take;
                    if (sl.DeliveredQty < sl.InvoicedQty)
                        throw new BusinessException($"Không hủy được: {sl.Item?.Code} đã lập hóa đơn bán cho số lượng này. Hủy hóa đơn trước (hoặc lập hóa đơn điều chỉnh giảm nếu hóa đơn đã phát hành).");
                }
            }
            SalesService.RefreshStatus(so);
        }
    }

    /// <summary>Tạo và ghi sổ ngay một phiếu kho (dùng cho nhận hàng PO, giao hàng SO, sản xuất)</summary>
    public async Task<InventoryDoc> CreateAndPostAsync(InvDocType type, DateTime date, int warehouseId, DocOrigin origin, string originRef, int originId,
        int? partnerId, string reason, IEnumerable<(int ItemId, decimal Qty, decimal UnitCost)> lines, int? userId, IDictionary<int, (string? Lot, DateTime? Expiry)>? lotInfo = null)
    {
        var doc = new InventoryDoc
        {
            Type = type, Date = date, WarehouseId = warehouseId, Origin = origin, OriginRef = originRef, OriginId = originId,
            PartnerId = partnerId, Reason = reason, CreatedById = userId, Number = await NextNumberAsync(type, date)
        };
        foreach (var l in lines)
            doc.Lines.Add(new InventoryDocLine { ItemId = l.ItemId, Quantity = l.Qty, UnitCost = l.UnitCost, LotNo = lotInfo != null && lotInfo.TryGetValue(l.ItemId, out var li) ? li.Lot : null, ExpiryDate = lotInfo != null && lotInfo.TryGetValue(l.ItemId, out var li2) ? li2.Expiry : null });
        await PostAsync(doc);
        return doc;
    }
}
