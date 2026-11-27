using System.Text;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

public record StockAlert(string Kind, Item Item, Warehouse? Warehouse, decimal Qty, decimal Limit, string? LotNo, DateTime? Expiry, string Message);
public record StockAgingRow(Item Item, Warehouse Warehouse, decimal Qty, decimal Value, decimal[] BucketQty, decimal[] BucketValue, int OldestDays);

public class StockReportService
{
    private readonly ErpDbContext _db;
    public StockReportService(ErpDbContext db) => _db = db;
    public static readonly string[] BucketNames = { "0–30 ngày", "31–60 ngày", "61–90 ngày", "91–180 ngày", "> 180 ngày" };
    private static int Bucket(int days) => days <= 30 ? 0 : days <= 60 ? 1 : days <= 90 ? 2 : days <= 180 ? 3 : 4;

    /// <summary>Cảnh báo: dưới tồn tối thiểu, vượt tồn tối đa, lô sắp hết hạn, lô đã hết hạn</summary>
    public async Task<List<StockAlert>> AlertsAsync(DateTime today)
    {
        var list = new List<StockAlert>();
        var items = await _db.Items.Where(i => i.IsActive && i.Type != ItemType.Service).ToListAsync();
        var onHand = (await _db.StockLevels.ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        foreach (var i in items)
        {
            var q = onHand.GetValueOrDefault(i.Id);
            if (i.MinStock > 0 && q < i.MinStock) list.Add(new StockAlert("min", i, null, q, i.MinStock, null, null, $"Tồn {q.ToQty()} thấp hơn mức tối thiểu {i.MinStock.ToQty()} – cần bổ sung {(Math.Max(i.MaxStock, i.MinStock * 2) - q).ToQty()}"));
            if (i.MaxStock > 0 && q > i.MaxStock) list.Add(new StockAlert("max", i, null, q, i.MaxStock, null, null, $"Tồn {q.ToQty()} vượt mức tối đa {i.MaxStock.ToQty()} (thừa {(q - i.MaxStock).ToQty()})"));
        }
        var lots = await _db.StockLots.Include(l => l.Item).Include(l => l.Warehouse).Where(l => l.Quantity > 0 && l.ExpiryDate != null).ToListAsync();
        foreach (var l in lots.OrderBy(l => l.ExpiryDate))
        {
            var days = (l.ExpiryDate!.Value.Date - today.Date).Days;
            if (days < 0) list.Add(new StockAlert("expired", l.Item!, l.Warehouse, l.Quantity, 0, l.LotNo, l.ExpiryDate, $"Lô {l.LotNo} đã hết hạn {-days} ngày ({l.ExpiryDate:dd/MM/yyyy}), còn {l.Quantity.ToQty()}"));
            else if (days <= l.Item!.ExpiryAlertDays) list.Add(new StockAlert("expiring", l.Item, l.Warehouse, l.Quantity, l.Item.ExpiryAlertDays, l.LotNo, l.ExpiryDate, $"Lô {l.LotNo} hết hạn sau {days} ngày ({l.ExpiryDate:dd/MM/yyyy}), còn {l.Quantity.ToQty()}"));
        }
        return list;
    }

    /// <summary>
    /// Tuổi hàng tồn theo từng kho: số lượng tồn hiện tại được gán ngược cho các lần nhập gần nhất (giả định xuất theo nguyên tắc nhập trước xuất trước),
    /// tuổi tính từ ngày nhập đến ngày báo cáo; giá trị theo giá vốn bình quân hiện hành.
    /// </summary>
    public async Task<List<StockAgingRow>> AgingAsync(DateTime at, int? warehouseId, ItemType? type)
    {
        var levels = await _db.StockLevels.Include(s => s.Item).Include(s => s.Warehouse).Where(s => s.Quantity > 0 && (warehouseId == null || s.WarehouseId == warehouseId) && (type == null || s.Item!.Type == type)).ToListAsync();
        var ids = levels.Select(l => l.ItemId).Distinct().ToList();
        var ins = (await _db.StockMoves.Where(m => ids.Contains(m.ItemId) && m.Quantity > 0 && m.Date <= at).Select(m => new { m.ItemId, m.WarehouseId, m.Date, m.Quantity }).ToListAsync())
            .GroupBy(m => (m.ItemId, m.WarehouseId)).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Date).ToList());
        var rows = new List<StockAgingRow>();
        foreach (var lv in levels)
        {
            var q = new decimal[5]; var left = lv.Quantity; var oldest = 0;
            foreach (var r in ins.GetValueOrDefault((lv.ItemId, lv.WarehouseId)) ?? new())
            {
                if (left <= 0) break;
                var take = Math.Min(left, r.Quantity); var days = Math.Max(0, (at.Date - r.Date.Date).Days);
                q[Bucket(days)] += take; left -= take; oldest = Math.Max(oldest, days);
            }
            if (left > 0) q[4] += left;
            var cost = lv.Item!.AvgCost;
            rows.Add(new StockAgingRow(lv.Item, lv.Warehouse!, lv.Quantity, Math.Round(lv.Quantity * cost, 0), q, q.Select(x => Math.Round(x * cost, 0)).ToArray(), oldest));
        }
        return rows.OrderByDescending(r => r.BucketValue[4] + r.BucketValue[3]).ThenBy(r => r.Item.Code).ToList();
    }

    /// <summary>Truy xuất nguồn gốc theo lô / serial: nhập từ nhà cung cấp nào, đơn nào; xuất cho ai, chứng từ nào</summary>
    public async Task<List<StockLotMove>> TraceAsync(string lotNo, int? itemId)
    {
        var q = _db.StockLotMoves.Include(m => m.Item).Include(m => m.Partner).Where(m => m.LotNo == lotNo);
        if (itemId != null) q = q.Where(m => m.ItemId == itemId);
        return await q.OrderBy(m => m.Date).ThenBy(m => m.Id).ToListAsync();
    }
}

public class StocktakeService
{
    private readonly ErpDbContext _db; private readonly InventoryService _inv; private readonly CurrentUser _user;
    public StocktakeService(ErpDbContext db, InventoryService inv, CurrentUser user) { _db = db; _inv = inv; _user = user; }

    /// <summary>Lập đợt kiểm kê: chụp tồn sổ sách của toàn bộ hàng có tồn trong kho (hoặc một nhóm hàng)</summary>
    public async Task<Stocktake> CreateAsync(int warehouseId, DateTime date, int? groupId, string? note)
    {
        if (await _db.Stocktakes.AnyAsync(s => s.WarehouseId == warehouseId && s.Status == StocktakeStatus.Counting)) throw new BusinessException("Kho này đang có đợt kiểm kê chưa xử lý.");
        var levels = await _db.StockLevels.Include(s => s.Item).Where(s => s.WarehouseId == warehouseId && s.Quantity != 0 && (groupId == null || s.Item!.GroupId == groupId)).ToListAsync();
        if (levels.Count == 0) throw new BusinessException("Kho không có hàng để kiểm kê.");
        var st = new Stocktake { WarehouseId = warehouseId, Date = date, Note = note, CreatedById = _user.Id, Number = await DocNumbers.NextAsync(_db.Stocktakes.Select(x => x.Number), "KK", date) };
        foreach (var l in levels.OrderBy(l => l.Item!.Code)) st.Lines.Add(new StocktakeLine { ItemId = l.ItemId, BookQty = l.Quantity });
        _db.Stocktakes.Add(st); await _db.SaveChangesAsync();
        return st;
    }

    /// <summary>Xử lý chênh lệch: lập phiếu kiểm kê điều chỉnh (thừa → Nợ 15x / Có 3381; thiếu → Nợ 1381 / Có 15x) cho các dòng đã đếm</summary>
    public async Task<InventoryDoc?> PostAsync(Stocktake st)
    {
        if (st.Status != StocktakeStatus.Counting) throw new BusinessException("Đợt kiểm kê đã xử lý hoặc đã hủy.");
        var counted = st.Lines.Where(l => l.CountedQty != null).ToList();
        if (counted.Count == 0) throw new BusinessException("Chưa nhập số liệu kiểm đếm.");
        var doc = new InventoryDoc { Type = InvDocType.Adjustment, Date = st.Date, WarehouseId = st.WarehouseId, Origin = DocOrigin.Manual, OriginRef = st.Number, Reason = $"Kiểm kê {st.Number}" + (string.IsNullOrWhiteSpace(st.Note) ? "" : " – " + st.Note),
            CreatedById = _user.Id, Number = await _inv.NextNumberAsync(InvDocType.Adjustment, st.Date) };
        foreach (var l in counted) doc.Lines.Add(new InventoryDocLine { ItemId = l.ItemId, CountedQty = l.CountedQty });
        await _inv.PostAsync(doc);
        st.Status = StocktakeStatus.Posted; st.InventoryDocId = doc.Id;
        await _db.SaveChangesAsync();
        return doc;
    }
}

/// <summary>Mã vạch Code 128 (bộ ký tự B) dạng SVG để in tem nhãn hàng hóa, lô, vị trí, pallet</summary>
public static class Barcode
{
    private static readonly string[] P =
    {
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213","221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
        "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211","212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
        "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331","231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214","112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
        "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141","214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
        "114131","311141","411131","211412","211214","211232","2331112"
    };

    public static string Svg(string text, int height = 46, double module = 1.4)
    {
        var codes = new List<int> { 104 };
        foreach (var ch in text) codes.Add(ch is >= ' ' and <= '~' ? ch - 32 : '?' - 32);
        var sum = 104; for (var i = 1; i < codes.Count; i++) sum += codes[i] * i;
        codes.Add(sum % 103); codes.Add(106);
        var sb = new StringBuilder(); double x = 10 * module; var bars = new StringBuilder();
        foreach (var c in codes)
        {
            var bar = true;
            foreach (var w in P[c]) { var width = (w - '0') * module; if (bar) bars.Append($"<rect x=\"{x:0.##}\" y=\"0\" width=\"{width:0.##}\" height=\"{height}\"/>"); x += width; bar = !bar; }
        }
        x += 10 * module;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {x:0.#} {height}\" width=\"{x:0.#}\" height=\"{height}\" shape-rendering=\"crispEdges\" fill=\"#000\">{bars}</svg>");
        return sb.ToString();
    }
}
