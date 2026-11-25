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
        void Move(InventoryDocLine l, int whId, decimal qty, decimal unitCost) => moves.Add(new StockMove
        {
            Date = doc.Date, ItemId = l.ItemId, WarehouseId = whId, Quantity = qty, UnitCost = unitCost,
            Amount = Math.Round(qty * unitCost, 0), DocNumber = doc.Number, Description = doc.Reason
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
                Move(l, doc.WarehouseId, qty, cost);
            }
            else
            {
                var qty = Math.Abs(l.Quantity);
                var lv = Level(item.Id, doc.WarehouseId);
                if (lv.Quantity < qty) { shortages.Add($"{item.Code} (tồn {lv.Quantity.ToQty()}, cần {qty.ToQty()})"); continue; }
                lv.Quantity -= qty;
                l.UnitCost = item.AvgCost; l.Amount = Math.Round(qty * item.AvgCost, 0);
                Move(l, doc.WarehouseId, -qty, item.AvgCost);
                if (doc.Type == InvDocType.Transfer)
                {
                    Level(item.Id, doc.ToWarehouseId!.Value).Quantity += qty;
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
        await _db.SaveChangesAsync();
    }

    /// <summary>Tạo và ghi sổ ngay một phiếu kho (dùng cho nhận hàng PO, giao hàng SO, sản xuất)</summary>
    public async Task<InventoryDoc> CreateAndPostAsync(InvDocType type, DateTime date, int warehouseId, DocOrigin origin, string originRef, int originId,
        int? partnerId, string reason, IEnumerable<(int ItemId, decimal Qty, decimal UnitCost)> lines, int? userId)
    {
        var doc = new InventoryDoc
        {
            Type = type, Date = date, WarehouseId = warehouseId, Origin = origin, OriginRef = originRef, OriginId = originId,
            PartnerId = partnerId, Reason = reason, CreatedById = userId, Number = await NextNumberAsync(type, date)
        };
        foreach (var l in lines) doc.Lines.Add(new InventoryDocLine { ItemId = l.ItemId, Quantity = l.Qty, UnitCost = l.UnitCost });
        await PostAsync(doc);
        return doc;
    }
}
