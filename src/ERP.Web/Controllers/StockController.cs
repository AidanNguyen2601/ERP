using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

/// <summary>Kho nâng cao: cảnh báo tồn / hạn dùng, tuổi hàng tồn, lô – serial và truy xuất, vị trí lưu kho, kiểm kê theo đợt, in tem mã vạch</summary>
public class StockController : ErpControllerBase
{
    private readonly StockReportService _rpt;
    public StockController(ErpDbContext db, StockReportService rpt) : base(db) => _rpt = rpt;
    private bool CanWrite => User.InAny(AppRoles.WarehouseTeam + "," + AppRoles.Accountant);

    // ------------------------------------------------------------------ cảnh báo
    public async Task<IActionResult> Alerts()
    {
        ViewBag.CanBuy = User.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Warehouse);
        return View(await _rpt.AlertsAsync(DateTime.Today));
    }

    // ------------------------------------------------------------------ tuổi hàng tồn
    public async Task<IActionResult> Aging(DateTime? at, int? warehouseId, ItemType? type, bool export = false)
    {
        var d = (at ?? DateTime.Today).Date;
        var rows = await _rpt.AgingAsync(d, warehouseId, type);
        if (export)
        {
            var cols = new List<ReportExport.Col> { new("Mã hàng", FieldKind.Text), new("Tên hàng", FieldKind.Text), new("Kho", FieldKind.Text), new("Tồn", FieldKind.Money) };
            cols.AddRange(StockReportService.BucketNames.Select(b => new ReportExport.Col("SL " + b, FieldKind.Money)));
            cols.AddRange(new[] { new ReportExport.Col("Giá trị tồn", FieldKind.Money), new ReportExport.Col("Giá trị > 180 ngày", FieldKind.Money) });
            return File(ReportExport.Xlsx("TuoiHangTon", "BÁO CÁO TUỔI HÀNG TỒN KHO", new[] { $"Tại ngày {d:dd/MM/yyyy}" }, cols,
                rows.Select(r => new object?[] { r.Item.Code, r.Item.Name, r.Warehouse.Name, r.Qty }.Concat(r.BucketQty.Select(x => (object?)x)).Concat(new object?[] { r.Value, r.BucketValue[4] }).ToArray()), null),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Tuoi_hang_ton_{d:yyyyMMdd}.xlsx");
        }
        ViewBag.At = d; ViewBag.WarehouseId = warehouseId; ViewBag.Type = type; ViewBag.Whs = await WarehousesAsync(warehouseId);
        return View(rows);
    }

    // ------------------------------------------------------------------ lô, serial
    public async Task<IActionResult> Lots(string? q, int? warehouseId, bool all = false)
    {
        var lots = Db.StockLots.Include(l => l.Item).Include(l => l.Warehouse).AsQueryable();
        if (!all) lots = lots.Where(l => l.Quantity > 0);
        if (warehouseId != null) lots = lots.Where(l => l.WarehouseId == warehouseId);
        if (!string.IsNullOrWhiteSpace(q)) lots = lots.Where(l => l.LotNo.Contains(q) || l.Item!.Code.Contains(q) || l.Item.Name.Contains(q));
        ViewBag.Q = q; ViewBag.All = all; ViewBag.WarehouseId = warehouseId; ViewBag.Whs = await WarehousesAsync(warehouseId);
        return View(await lots.OrderBy(l => l.ExpiryDate == null).ThenBy(l => l.ExpiryDate).ThenBy(l => l.Item!.Code).Take(500).ToListAsync());
    }

    public async Task<IActionResult> Trace(string? lot, int? itemId)
    {
        ViewBag.Lot = lot; ViewBag.ItemId = itemId;
        ViewBag.Partners = await Db.Partners.ToDictionaryAsync(p => p.Id, p => p.Name);
        return View(string.IsNullOrWhiteSpace(lot) ? new List<StockLotMove>() : await _rpt.TraceAsync(lot.Trim(), itemId));
    }

    // ------------------------------------------------------------------ vị trí lưu kho
    public async Task<IActionResult> Bins(int? warehouseId)
    {
        var bins = await Db.BinLocations.Include(b => b.Warehouse).Where(b => warehouseId == null || b.WarehouseId == warehouseId).OrderBy(b => b.WarehouseId).ThenBy(b => b.Code).ToListAsync();
        ViewBag.Stock = await Db.StockBins.Include(s => s.Item).Where(s => s.Quantity > 0).OrderBy(s => s.Item!.Code).ToListAsync();
        ViewBag.Whs = await WarehousesAsync(warehouseId); ViewBag.WarehouseId = warehouseId; ViewBag.CanWrite = CanWrite;
        ViewBag.Items = await ItemsAsync(i => i.IsStocked);
        ViewBag.AllBins = await Db.BinLocations.Where(b => b.IsActive).OrderBy(b => b.WarehouseId).ThenBy(b => b.Code).ToListAsync();
        var whLevel = await Db.StockLevels.Include(s => s.Item).Include(s => s.Warehouse).Where(s => s.Quantity > 0 && (warehouseId == null || s.WarehouseId == warehouseId)).ToListAsync();
        var assigned = (await Db.StockBins.ToListAsync()).GroupBy(s => (s.ItemId, s.WarehouseId)).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        ViewBag.Unassigned = whLevel.Select(l => (l.Item!, l.Warehouse!, Qty: l.Quantity - assigned.GetValueOrDefault((l.ItemId, l.WarehouseId)))).Where(x => x.Qty > 0).ToList();
        return View(bins);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBin(int id, int warehouseId, string code, string? name, string kind, bool isActive = true)
    {
        if (!CanWrite) return Denied();
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code == "") { Error("Nhập mã vị trí."); return RedirectToAction(nameof(Bins)); }
        if (await Db.BinLocations.AnyAsync(b => b.WarehouseId == warehouseId && b.Code == code && b.Id != id)) { Error("Mã vị trí đã tồn tại trong kho."); return RedirectToAction(nameof(Bins)); }
        var b = id == 0 ? new BinLocation { WarehouseId = warehouseId } : await Db.BinLocations.FindAsync(id);
        if (b == null) return NotFound();
        b.Code = code; b.Name = name; b.Kind = string.IsNullOrWhiteSpace(kind) ? "Kệ" : kind; b.IsActive = isActive;
        if (b.Id == 0) Db.BinLocations.Add(b);
        await Db.SaveChangesAsync();
        Success($"Đã lưu vị trí {code}.");
        return RedirectToAction(nameof(Bins), new { warehouseId });
    }

    /// <summary>Chuyển hàng giữa các vị trí trong cùng kho (hoặc xếp hàng chưa gán vị trí vào vị trí) – không phát sinh bút toán</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveBin(int itemId, int warehouseId, int? fromBinId, int toBinId, decimal qty)
    {
        if (!CanWrite) return Denied();
        if (qty <= 0) throw new BusinessException("Số lượng phải lớn hơn 0.");
        var to = await Db.BinLocations.FindAsync(toBinId); if (to == null || to.WarehouseId != warehouseId) throw new BusinessException("Vị trí nhận không thuộc kho.");
        var item = await Db.Items.FindAsync(itemId) ?? throw new BusinessException("Không tìm thấy hàng.");
        var bins = await Db.StockBins.Where(s => s.ItemId == itemId && s.WarehouseId == warehouseId).ToListAsync();
        if (fromBinId != null)
        {
            var from = bins.FirstOrDefault(b => b.BinId == fromBinId);
            if (from == null || from.Quantity < qty) throw new BusinessException($"Vị trí nguồn không đủ {item.Code}.");
            from.Quantity -= qty;
        }
        else
        {
            var level = await Db.StockLevels.Where(s => s.ItemId == itemId && s.WarehouseId == warehouseId).SumAsync(s => (decimal?)s.Quantity) ?? 0;
            if (level - bins.Sum(b => b.Quantity) < qty) throw new BusinessException($"Phần {item.Code} chưa gán vị trí không đủ {qty.ToQty()}.");
        }
        var dst = bins.FirstOrDefault(b => b.BinId == toBinId);
        if (dst == null) Db.StockBins.Add(dst = new StockBin { ItemId = itemId, WarehouseId = warehouseId, BinId = toBinId });
        dst.Quantity += qty;
        await Db.SaveChangesAsync();
        Success($"Đã chuyển {qty.ToQty()} {item.Code} vào vị trí {to.Code}.");
        return RedirectToAction(nameof(Bins), new { warehouseId });
    }

    // ------------------------------------------------------------------ pallet
    public async Task<IActionResult> Pallets(int? warehouseId)
    {
        ViewBag.Whs = await WarehousesAsync(warehouseId); ViewBag.WarehouseId = warehouseId; ViewBag.CanWrite = CanWrite;
        ViewBag.Bins = await Db.BinLocations.Where(b => b.IsActive).OrderBy(b => b.WarehouseId).ThenBy(b => b.Code).ToListAsync();
        ViewBag.Items = await ItemsAsync(i => i.IsStocked);
        return View(await Db.Pallets.Include(p => p.Warehouse).Include(p => p.Bin).Include(p => p.Items).ThenInclude(i => i.Item).Where(p => p.IsActive && (warehouseId == null || p.WarehouseId == warehouseId)).OrderBy(p => p.Code).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePallet(int warehouseId, string? code, int? binId, string? note)
    {
        if (!CanWrite) return Denied();
        code = string.IsNullOrWhiteSpace(code) ? $"PLT{DateTime.Today:yyMM}-{(await Db.Pallets.CountAsync() + 1):0000}" : code.Trim().ToUpperInvariant();
        if (await Db.Pallets.AnyAsync(p => p.Code == code)) { Error("Mã pallet đã tồn tại."); return RedirectToAction(nameof(Pallets)); }
        if (binId != null && (await Db.BinLocations.FindAsync(binId))?.WarehouseId != warehouseId) { Error("Vị trí không thuộc kho."); return RedirectToAction(nameof(Pallets)); }
        Db.Pallets.Add(new Pallet { Code = code, WarehouseId = warehouseId, BinId = binId, Note = note });
        await Db.SaveChangesAsync();
        Success($"Đã tạo pallet {code}. In tem pallet tại In tem mã vạch.");
        return RedirectToAction(nameof(Pallets), new { warehouseId });
    }

    /// <summary>Xếp hàng lên pallet: không vượt tồn kho còn lại chưa xếp pallet khác; nếu pallet đang ở một vị trí thì tồn theo vị trí cũng được cập nhật</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPalletItem(int id, int itemId, decimal qty, string? lotNo)
    {
        if (!CanWrite) return Denied();
        var p = await Db.Pallets.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        var item = await Db.Items.FindAsync(itemId) ?? throw new BusinessException("Không tìm thấy hàng.");
        if (qty <= 0) throw new BusinessException("Số lượng phải lớn hơn 0.");
        var level = await Db.StockLevels.Where(s => s.ItemId == itemId && s.WarehouseId == p.WarehouseId).SumAsync(s => (decimal?)s.Quantity) ?? 0;
        var onPallets = await Db.PalletItems.Where(x => x.ItemId == itemId && x.Pallet!.WarehouseId == p.WarehouseId).SumAsync(x => (decimal?)x.Quantity) ?? 0;
        if (qty > level - onPallets) throw new BusinessException($"Tồn {item.Code} trong kho còn {(level - onPallets).ToQty()} chưa xếp pallet, không đủ {qty.ToQty()}.");
        lotNo = string.IsNullOrWhiteSpace(lotNo) ? null : lotNo.Trim();
        if (lotNo != null && !await Db.StockLots.AnyAsync(l => l.ItemId == itemId && l.WarehouseId == p.WarehouseId && l.LotNo == lotNo && l.Quantity > 0)) throw new BusinessException($"Không có lô {lotNo} của {item.Code} trong kho.");
        var pi = p.Items.FirstOrDefault(x => x.ItemId == itemId && x.LotNo == lotNo);
        if (pi == null) p.Items.Add(pi = new PalletItem { ItemId = itemId, LotNo = lotNo });
        pi.Quantity += qty;
        if (p.BinId != null) await AdjustBinAsync(itemId, p.WarehouseId, p.BinId.Value, qty);
        await Db.SaveChangesAsync();
        Success($"Đã xếp {qty.ToQty()} {item.Code} lên pallet {p.Code}.");
        return RedirectToAction(nameof(Pallets), new { warehouseId = p.WarehouseId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePalletItem(int id)
    {
        if (!CanWrite) return Denied();
        var pi = await Db.PalletItems.Include(x => x.Pallet).FirstOrDefaultAsync(x => x.Id == id); if (pi == null) return NotFound();
        if (pi.Pallet!.BinId != null) await AdjustBinAsync(pi.ItemId, pi.Pallet.WarehouseId, pi.Pallet.BinId.Value, -pi.Quantity);
        Db.PalletItems.Remove(pi); await Db.SaveChangesAsync();
        Success("Đã dỡ hàng khỏi pallet.");
        return RedirectToAction(nameof(Pallets), new { warehouseId = pi.Pallet.WarehouseId });
    }

    /// <summary>Di chuyển cả pallet sang vị trí khác trong kho (tồn theo vị trí của toàn bộ hàng trên pallet đi theo)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MovePallet(int id, int toBinId)
    {
        if (!CanWrite) return Denied();
        var p = await Db.Pallets.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        var to = await Db.BinLocations.FindAsync(toBinId); if (to == null || to.WarehouseId != p.WarehouseId) throw new BusinessException("Vị trí nhận không thuộc kho của pallet.");
        if (p.BinId == toBinId) throw new BusinessException("Pallet đang ở vị trí này.");
        foreach (var pi in p.Items.GroupBy(x => x.ItemId))
        {
            var q = pi.Sum(x => x.Quantity);
            if (p.BinId != null) await AdjustBinAsync(pi.Key, p.WarehouseId, p.BinId.Value, -q);
            await AdjustBinAsync(pi.Key, p.WarehouseId, toBinId, q);
        }
        p.BinId = toBinId; await Db.SaveChangesAsync();
        Success($"Đã chuyển pallet {p.Code} đến {to.Code}.");
        return RedirectToAction(nameof(Pallets), new { warehouseId = p.WarehouseId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePallet(int id)
    {
        if (!CanWrite) return Denied();
        var p = await Db.Pallets.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id); if (p == null) return NotFound();
        if (p.Items.Any()) throw new BusinessException("Pallet còn hàng, dỡ hàng trước khi đóng.");
        p.IsActive = false; await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Pallets), new { warehouseId = p.WarehouseId });
    }

    private async Task AdjustBinAsync(int itemId, int whId, int binId, decimal delta)
    {
        var b = await Db.StockBins.FirstOrDefaultAsync(x => x.ItemId == itemId && x.WarehouseId == whId && x.BinId == binId);
        if (delta > 0)
        {
            var level = await Db.StockLevels.Where(s => s.ItemId == itemId && s.WarehouseId == whId).SumAsync(s => (decimal?)s.Quantity) ?? 0;
            var assigned = await Db.StockBins.Where(s => s.ItemId == itemId && s.WarehouseId == whId).SumAsync(s => (decimal?)s.Quantity) ?? 0;
            if (level - assigned < delta) throw new BusinessException("Tồn chưa gán vị trí không đủ để đặt pallet vào vị trí này (hàng đã gán vị trí khác – chuyển bằng chức năng chuyển vị trí).");
            if (b == null) Db.StockBins.Add(b = new StockBin { ItemId = itemId, WarehouseId = whId, BinId = binId });
        }
        if (b != null) b.Quantity = Math.Max(0, b.Quantity + delta);
    }

    // ------------------------------------------------------------------ kiểm kê theo đợt
    public async Task<IActionResult> Stocktakes()
    {
        ViewBag.Whs = await WarehousesAsync();
        ViewBag.Groups = new SelectList(await Db.ItemGroups.OrderBy(g => g.Code).ToListAsync(), "Id", "Name");
        ViewBag.CanWrite = CanWrite;
        return View(await Db.Stocktakes.Include(s => s.Warehouse).Include(s => s.Lines).OrderByDescending(s => s.Date).ThenByDescending(s => s.Id).Take(100).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateStocktake(int warehouseId, DateTime date, int? groupId, string? note, [FromServices] StocktakeService svc)
    {
        if (!CanWrite) return Denied();
        var st = await svc.CreateAsync(warehouseId, date.Date, groupId, note);
        Success($"Đã lập đợt kiểm kê {st.Number} gồm {st.Lines.Count} mặt hàng. In phiếu kiểm đếm rồi nhập số thực tế.");
        return RedirectToAction(nameof(Stocktake), new { id = st.Id });
    }

    public async Task<IActionResult> Stocktake(int id, bool print = false)
    {
        var st = await Db.Stocktakes.Include(s => s.Warehouse).Include(s => s.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(s => s.Id == id);
        if (st == null) return NotFound();
        ViewBag.CanWrite = CanWrite; ViewBag.Print = print;
        ViewBag.Current = (await Db.StockLevels.Where(s => s.WarehouseId == st.WarehouseId).ToListAsync()).ToDictionary(s => s.ItemId, s => s.Quantity);
        ViewBag.Doc = st.InventoryDocId == null ? null : await Db.InventoryDocs.FindAsync(st.InventoryDocId);
        return View(st);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCounts(int id, int[] lineId, string?[] counted, string?[] note, bool post, [FromServices] StocktakeService svc)
    {
        if (!CanWrite) return Denied();
        var st = await Db.Stocktakes.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id); if (st == null) return NotFound();
        if (st.Status != StocktakeStatus.Counting) throw new BusinessException("Đợt kiểm kê đã xử lý.");
        for (var i = 0; i < lineId.Length; i++)
        {
            var l = st.Lines.FirstOrDefault(x => x.Id == lineId[i]); if (l == null) continue;
            var c = counted.ElementAtOrDefault(i);
            l.CountedQty = string.IsNullOrWhiteSpace(c) ? null : decimal.TryParse(c, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : throw new BusinessException("Số kiểm đếm không hợp lệ.");
            l.Note = note.ElementAtOrDefault(i);
        }
        await Db.SaveChangesAsync();
        if (post)
        {
            var doc = await svc.PostAsync(st);
            Success($"Đã xử lý chênh lệch kiểm kê bằng phiếu {doc!.Number}.");
        }
        else Success("Đã lưu số liệu kiểm đếm.");
        return RedirectToAction(nameof(Stocktake), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelStocktake(int id)
    {
        if (!CanWrite) return Denied();
        var st = await Db.Stocktakes.FindAsync(id); if (st == null) return NotFound();
        if (st.Status != StocktakeStatus.Counting) throw new BusinessException("Đợt kiểm kê đã xử lý.");
        st.Status = StocktakeStatus.Cancelled; await Db.SaveChangesAsync();
        Success("Đã hủy đợt kiểm kê.");
        return RedirectToAction(nameof(Stocktakes));
    }

    // ------------------------------------------------------------------ in tem nhãn
    public async Task<IActionResult> Labels(string kind = "item", int copies = 1)
    {
        ViewBag.Kind = kind; ViewBag.Copies = copies;
        ViewBag.Items = await Db.Items.Where(i => i.IsActive && i.Type != ItemType.Service).OrderBy(i => i.Code).ToListAsync();
        ViewBag.Lots = await Db.StockLots.Include(l => l.Item).Where(l => l.Quantity > 0).OrderBy(l => l.Item!.Code).ThenBy(l => l.LotNo).Take(500).ToListAsync();
        ViewBag.Bins = await Db.BinLocations.Include(b => b.Warehouse).Where(b => b.IsActive).OrderBy(b => b.Warehouse!.Code).ThenBy(b => b.Code).ToListAsync();
        ViewBag.Pallets = await Db.Pallets.Include(p => p.Warehouse).Include(p => p.Items).Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync();
        return View();
    }

    /// <summary>Trang tem nhãn để in: mã vạch Code 128. Mã hàng: MÃ_HÀNG · Lô / serial: MÃ_HÀNG|SỐ_LÔ · Vị trí / pallet: tên vị trí</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PrintLabels(string kind, int[] ids, int copies)
    {
        var labels = new List<(string Title, string Sub, string Code)>();
        if (kind == "item") foreach (var i in await Db.Items.Where(x => ids.Contains(x.Id)).ToListAsync()) labels.Add((i.Name, $"{i.Code} · {i.Unit}", string.IsNullOrWhiteSpace(i.Barcode) ? i.Code : i.Barcode));
        else if (kind == "lot") foreach (var l in await Db.StockLots.Include(x => x.Item).Where(x => ids.Contains(x.Id)).ToListAsync()) labels.Add((l.Item!.Name, $"Lô {l.LotNo}{(l.ExpiryDate != null ? " · HSD " + l.ExpiryDate.Value.ToString("dd/MM/yyyy") : "")}", $"{l.Item.Code}|{l.LotNo}"));
        else if (kind == "pallet") foreach (var p in await Db.Pallets.Include(x => x.Items).ThenInclude(i => i.Item).Where(x => ids.Contains(x.Id)).ToListAsync()) labels.Add(("Pallet " + p.Code, string.Join(", ", p.Items.Select(i => i.Item!.Code + " x " + i.Quantity.ToQty())), p.Code));
        else if (kind == "bin") foreach (var b in await Db.BinLocations.Include(x => x.Warehouse).Where(x => ids.Contains(x.Id)).ToListAsync()) labels.Add((b.Name ?? b.Code, $"{b.Kind} · {b.Warehouse?.Name}", b.Code));
        if (labels.Count == 0) { Error("Chọn ít nhất một mục để in tem."); return RedirectToAction(nameof(Labels), new { kind }); }
        ViewBag.Copies = Math.Clamp(copies, 1, 50);
        return View("LabelSheet", labels);
    }
}
