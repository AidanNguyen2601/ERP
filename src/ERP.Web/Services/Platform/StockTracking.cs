using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Theo dõi lô / serial / hạn dùng và vị trí lưu kho trong lúc ghi sổ phiếu kho. Tồn theo lô và theo vị trí là lớp theo dõi chi tiết
/// nằm trên tồn kho tổng (StockLevel): phần tồn chưa gán lô / vị trí (hàng có trước khi bật theo dõi) vẫn xuất bình thường.
/// </summary>
public class StockTracker
{
    private readonly ErpDbContext _db;
    public List<StockLot> Lots { get; } = new();
    public List<StockBin> Bins { get; } = new();
    public List<StockLotMove> Moves { get; } = new();
    private readonly DateTime _date; private readonly string _docNumber; private readonly string _docType; private readonly int? _partnerId; private readonly string? _origin;

    public StockTracker(ErpDbContext db, DateTime date, string docNumber, string docType, int? partnerId, string? origin)
    { _db = db; _date = date; _docNumber = docNumber; _docType = docType; _partnerId = partnerId; _origin = origin; }

    public static bool IsTracked(Item i) => i.TrackMode != TrackMode.None || i.CostMethod != CostMethod.Average;

    public async Task LoadAsync(IEnumerable<int> itemIds)
    {
        var ids = itemIds.Distinct().ToList();
        Lots.AddRange(await _db.StockLots.Where(l => ids.Contains(l.ItemId)).ToListAsync());
        Bins.AddRange(await _db.StockBins.Where(b => ids.Contains(b.ItemId)).ToListAsync());
    }

    private void Log(Item item, int wh, string lotNo, decimal qty, decimal cost, DateTime? exp, int? invDocId = null, string? suffix = null) =>
        Moves.Add(new StockLotMove { Date = _date, ItemId = item.Id, WarehouseId = wh, LotNo = lotNo, Quantity = qty, UnitCost = cost, DocNumber = _docNumber + suffix, DocType = _docType + suffix,
            InventoryDocId = invDocId ?? 0, PartnerId = _partnerId, OriginRef = _origin, ExpiryDate = exp });

    /// <summary>Nhập hàng vào lô (serial: mỗi đơn vị một số serial)</summary>
    public void AddLot(Item item, int wh, string? lotNo, DateTime? mfg, DateTime? exp, decimal qty, decimal cost, string docRef)
    {
        if (qty <= 0) return;
        if (item.TrackMode == TrackMode.Serial)
        {
            if (qty != Math.Floor(qty)) throw new BusinessException($"{item.Code} quản lý theo serial: số lượng phải là số nguyên.");
            if (!string.IsNullOrWhiteSpace(lotNo) && qty != 1) throw new BusinessException($"{item.Code}: mỗi dòng nhập một số serial thì số lượng phải bằng 1 (để trống serial để hệ thống tự cấp).");
            for (var k = 1; k <= qty; k++) Put(item, wh, string.IsNullOrWhiteSpace(lotNo) ? $"{docRef}-{k:000}" : lotNo.Trim(), mfg, exp, 1, cost);
            return;
        }
        Put(item, wh, string.IsNullOrWhiteSpace(lotNo) ? docRef : lotNo.Trim(), mfg, exp, qty, cost);
    }

    private void Put(Item item, int wh, string lotNo, DateTime? mfg, DateTime? exp, decimal qty, decimal cost)
    {
        if (item.TrackMode == TrackMode.Serial && Lots.Any(l => l.ItemId == item.Id && l.LotNo == lotNo && l.Quantity > 0)) throw new BusinessException($"Số serial {lotNo} của {item.Code} đã có trong kho.");
        var lot = Lots.FirstOrDefault(l => l.ItemId == item.Id && l.WarehouseId == wh && l.LotNo == lotNo);
        if (lot == null)
        {
            lot = new StockLot { ItemId = item.Id, WarehouseId = wh, LotNo = lotNo, MfgDate = mfg, ExpiryDate = exp, ReceivedDate = _date, UnitCost = cost, PartnerId = _partnerId, SourceDoc = _docNumber };
            Lots.Add(lot); _db.StockLots.Add(lot);
        }
        else
        {
            var q0 = Math.Max(0, lot.Quantity);
            lot.UnitCost = q0 + qty == 0 ? cost : Math.Round((q0 * lot.UnitCost + qty * cost) / (q0 + qty), 4);
            lot.ExpiryDate ??= exp; lot.MfgDate ??= mfg;
            if (q0 == 0) lot.ReceivedDate = _date;
        }
        lot.Quantity += qty;
        Log(item, wh, lotNo, qty, cost, lot.ExpiryDate);
    }

    /// <summary>Xuất khỏi các lô: chỉ định lô, hoặc tự chọn theo FEFO (hết hạn trước xuất trước) với hàng có hạn dùng, FIFO với hàng còn lại.
    /// Phần chưa gán lô (tồn có trước khi bật theo dõi) trả về trong Remainder.</summary>
    public (List<(StockLot Lot, decimal Qty)> Parts, decimal Remainder) Consume(Item item, int wh, decimal qty, string? lotNo, bool allowExpired, decimal levelQty)
    {
        var parts = new List<(StockLot, decimal)>();
        var inWh = Lots.Where(l => l.ItemId == item.Id && l.WarehouseId == wh && l.Quantity > 0).ToList();
        var left = qty;
        if (!string.IsNullOrWhiteSpace(lotNo))
        {
            var lot = inWh.FirstOrDefault(l => l.LotNo == lotNo.Trim()) ?? throw new BusinessException($"Không có lô / serial {lotNo} của {item.Code} trong kho này.");
            if (lot.Quantity < qty) throw new BusinessException($"Lô {lotNo} của {item.Code} chỉ còn {lot.Quantity.ToQty()}, cần {qty.ToQty()}.");
            if (!allowExpired && lot.ExpiryDate != null && lot.ExpiryDate.Value.Date < _date.Date) throw new BusinessException($"Lô {lotNo} của {item.Code} đã hết hạn {lot.ExpiryDate:dd/MM/yyyy}.");
            parts.Add((lot, qty)); left = 0;
        }
        else
        {
            var order = item.TrackExpiry ? inWh.OrderBy(l => l.ExpiryDate ?? DateTime.MaxValue).ThenBy(l => l.ReceivedDate).ThenBy(l => l.Id) : inWh.OrderBy(l => l.ReceivedDate).ThenBy(l => l.Id);
            foreach (var l in order)
            {
                if (left <= 0) break;
                if (!allowExpired && l.ExpiryDate != null && l.ExpiryDate.Value.Date < _date.Date) continue;
                var take = Math.Min(left, l.Quantity);
                parts.Add((l, take)); left -= take;
            }
        }
        var untracked = levelQty - inWh.Sum(l => l.Quantity);
        if (left > 0 && left > Math.Max(0, untracked) + 0.0001m)
            throw new BusinessException($"Không đủ tồn{(allowExpired ? "" : " còn hạn dùng")} của {item.Code} theo lô (cần {qty.ToQty()}, có {(qty - left + Math.Max(0, untracked)).ToQty()}).");
        foreach (var (lot, q) in parts) { lot.Quantity -= q; Log(item, wh, lot.LotNo, -q, lot.UnitCost, lot.ExpiryDate); }
        return (parts, Math.Max(0, left));
    }

    // ------------------------------------------------------------------ vị trí lưu kho
    public void AddBin(Item item, int wh, int? binId, decimal qty)
    {
        if (binId == null || qty <= 0) return;
        var b = Bins.FirstOrDefault(x => x.ItemId == item.Id && x.WarehouseId == wh && x.BinId == binId);
        if (b == null) { b = new StockBin { ItemId = item.Id, WarehouseId = wh, BinId = binId.Value }; Bins.Add(b); _db.StockBins.Add(b); }
        b.Quantity += qty;
    }

    /// <summary>Lấy hàng từ vị trí chỉ định (phải đủ) hoặc tự lấy từ các vị trí có hàng (nhiều nhất trước)</summary>
    public void ConsumeBin(Item item, int wh, int? binId, decimal qty, bool strict = true)
    {
        if (qty <= 0) return;
        var mine = Bins.Where(x => x.ItemId == item.Id && x.WarehouseId == wh && x.Quantity > 0).ToList();
        if (binId != null)
        {
            var b = mine.FirstOrDefault(x => x.BinId == binId);
            if (b == null || b.Quantity < qty) { if (strict) throw new BusinessException($"Vị trí đã chọn không đủ {item.Code} (cần {qty.ToQty()}, có {(b?.Quantity ?? 0).ToQty()})."); }
            else { b.Quantity -= qty; return; }
        }
        var left = qty;
        foreach (var b in mine.OrderByDescending(x => x.Quantity)) { if (left <= 0) break; var t = Math.Min(left, b.Quantity); b.Quantity -= t; left -= t; }
    }

    // ------------------------------------------------------------------ hủy phiếu: đảo các biến động lô
    public async Task ReverseAsync(InventoryDoc doc, Dictionary<int, Item> items)
    {
        var originals = await _db.StockLotMoves.Where(m => m.InventoryDocId == doc.Id && !m.DocType.EndsWith("(hủy)")).ToListAsync();
        foreach (var m in originals)
        {
            var item = items[m.ItemId];
            var lot = Lots.FirstOrDefault(l => l.ItemId == m.ItemId && l.WarehouseId == m.WarehouseId && l.LotNo == m.LotNo);
            if (lot == null)
            {
                lot = new StockLot { ItemId = m.ItemId, WarehouseId = m.WarehouseId, LotNo = m.LotNo, ExpiryDate = m.ExpiryDate, ReceivedDate = m.Date, UnitCost = m.UnitCost, SourceDoc = doc.Number };
                Lots.Add(lot); _db.StockLots.Add(lot);
            }
            lot.Quantity -= m.Quantity;
            if (lot.Quantity < 0) throw new BusinessException($"Không hủy được: lô {m.LotNo} của {item.Code} đã được xuất / chuyển đi.");
            Log(item, m.WarehouseId, m.LotNo, -m.Quantity, m.UnitCost, m.ExpiryDate, doc.Id, " (hủy)");
        }
    }
}
