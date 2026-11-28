using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Hoạch định: tồn kho an toàn / điểm đặt hàng, kiểm kê bằng điện thoại, lập lịch sản xuất theo năng lực</summary>
public class PlanningController : ErpControllerBase
{
    private readonly StockPlanningService _stock; private readonly CapacityScheduler _sched;
    public PlanningController(ErpDbContext db, StockPlanningService stock, CapacityScheduler sched) : base(db) { _stock = stock; _sched = sched; }

    // ------------------------------------------------------------------ tồn kho an toàn
    [Authorize(Roles = AppRoles.FinanceView + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing + "," + AppRoles.Production)]
    public async Task<IActionResult> Reorder(int days = 90, decimal z = 1.65m, int review = 14, bool onlyBelow = false)
    {
        days = Math.Clamp(days, 14, 730); review = Math.Clamp(review, 1, 180);
        var rows = await _stock.SuggestAsync(days, z, review);
        if (onlyBelow) rows = rows.Where(r => r.SuggestQty > 0).ToList();
        ViewBag.Days = days; ViewBag.Z = z; ViewBag.Review = review; ViewBag.OnlyBelow = onlyBelow;
        ViewBag.CanEdit = User.InAny(AppRoles.Admin + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing);
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing)]
    public async Task<IActionResult> ApplyLevels(int days, decimal z, int review, List<int>? itemIds)
    {
        var ids = (itemIds ?? new()).ToHashSet();
        var rows = (await _stock.SuggestAsync(days, z, review)).Where(r => ids.Contains(r.Item.Id)).ToList();
        var n = await _stock.ApplyAsync(rows);
        Success($"Đã cập nhật tồn tối thiểu / tối đa cho {n} mặt hàng.");
        return RedirectToAction(nameof(Reorder), new { days, z, review });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing)]
    public async Task<IActionResult> CreateRequisition(int days, decimal z, int review, List<int>? itemIds)
    {
        var ids = (itemIds ?? new()).ToHashSet();
        var rows = (await _stock.SuggestAsync(days, z, review)).Where(r => ids.Count == 0 || ids.Contains(r.Item.Id)).ToList();
        var pr = await _stock.CreateRequisitionAsync(rows);
        if (pr == null) { Error("Không có mặt hàng nào dưới điểm đặt hàng."); return RedirectToAction(nameof(Reorder), new { days, z, review }); }
        Success($"Đã lập đề nghị mua hàng {pr.Number} ({pr.Lines.Count} mặt hàng).");
        return RedirectToAction("Details", "Requisitions", new { id = pr.Id });
    }

    // ------------------------------------------------------------------ kiểm kê bằng điện thoại
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Warehouse + "," + AppRoles.Accountant)]
    public async Task<IActionResult> Scan(int id)
    {
        var st = await Db.Stocktakes.Include(s => s.Warehouse).Include(s => s.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(s => s.Id == id);
        if (st == null) return NotFound();
        return View(st);
    }

    public record ScanResult(bool Ok, string Message, int? LineId = null, string? Item = null, decimal? Counted = null, decimal? Book = null, int Done = 0, int Total = 0);

    /// <summary>Quét mã vạch / mã hàng: cộng dồn (hoặc ghi đè) số lượng đếm được của dòng kiểm kê</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Warehouse + "," + AppRoles.Accountant)]
    public async Task<IActionResult> ScanCount(int id, string code, decimal qty = 1, bool replace = false)
    {
        var st = await Db.Stocktakes.Include(s => s.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(s => s.Id == id);
        if (st == null) return NotFound();
        if (st.Status != StocktakeStatus.Counting) return Json(new ScanResult(false, "Đợt kiểm kê không còn ở trạng thái đang đếm."));
        code = (code ?? "").Trim();
        if (code.Length == 0) return Json(new ScanResult(false, "Chưa có mã."));
        var line = st.Lines.FirstOrDefault(l => string.Equals(l.Item?.Barcode, code, StringComparison.OrdinalIgnoreCase))
                   ?? st.Lines.FirstOrDefault(l => string.Equals(l.Item?.Code, code, StringComparison.OrdinalIgnoreCase));
        if (line == null)
        {
            var item = await Db.Items.FirstOrDefaultAsync(i => i.Barcode == code || i.Code == code);
            if (item == null) return Json(new ScanResult(false, $"Không tìm thấy hàng có mã \"{code}\"."));
            line = new StocktakeLine { ItemId = item.Id, Item = item, BookQty = 0 };
            st.Lines.Add(line);
        }
        if (qty < 0) return Json(new ScanResult(false, "Số lượng không âm."));
        line.CountedQty = replace ? qty : (line.CountedQty ?? 0) + qty;
        await Db.SaveChangesAsync();
        return Json(new ScanResult(true, $"{line.Item!.Code} {line.Item.Name}: đếm {line.CountedQty:0.###} / sổ {line.BookQty:0.###}", line.Id, line.Item.Name, line.CountedQty, line.BookQty,
            st.Lines.Count(l => l.CountedQty != null), st.Lines.Count));
    }

    // ------------------------------------------------------------------ lập lịch sản xuất
    [Authorize(Roles = AppRoles.FinanceView + "," + AppRoles.Production)]
    public async Task<IActionResult> Schedule(DateTime? start, int days = 14)
    {
        var s = (start ?? DateTime.Today).Date.AddHours(8);
        var slots = await _sched.ScheduleAsync(s, save: false);
        ViewBag.Start = s; ViewBag.Days = Math.Clamp(days, 7, 60);
        ViewBag.CanEdit = User.InAny(AppRoles.Admin + "," + AppRoles.Production);
        return View(slots);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Production)]
    public async Task<IActionResult> SaveSchedule(DateTime? start)
    {
        var slots = await _sched.ScheduleAsync((start ?? DateTime.Today).Date.AddHours(8), save: true);
        Success($"Đã ghi lịch dự kiến cho {slots.Count} công đoạn" + (slots.Any(x => x.Late) ? $"; {slots.Where(x => x.Late).Select(x => x.Order.Id).Distinct().Count()} lệnh dự kiến trễ hạn." : "."));
        return RedirectToAction(nameof(Schedule), new { start = start?.ToString("yyyy-MM-dd") });
    }
}
