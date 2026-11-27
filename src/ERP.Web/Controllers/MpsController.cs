using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Kế hoạch sản xuất tổng thể (MPS): nhu cầu theo đơn hàng và dự báo, đề xuất số lượng sản xuất, kiểm tra công suất, tạo lệnh sản xuất</summary>
public class MpsController : ErpControllerBase
{
    private readonly MpsService _mps;
    public MpsController(ErpDbContext db, MpsService mps) : base(db) => _mps = mps;
    private bool CanEdit => User.InAny(AppRoles.ProductionTeam);

    public async Task<IActionResult> Index()
    {
        ViewBag.CanEdit = CanEdit;
        ViewBag.Items = await Db.Items.Where(i => i.IsActive && (i.Type == ItemType.FinishedGood || i.Type == ItemType.SemiFinished) && Db.Boms.Any(b => b.FinishedItemId == i.Id && b.IsActive)).OrderBy(i => i.Code).ToListAsync();
        return View(await Db.MpsPlans.Include(p => p.Lines).OrderByDescending(p => p.FromMonth).ThenByDescending(p => p.Id).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string fromMonth, int months, string? note, int[]? itemIds)
    {
        if (!CanEdit) return Denied();
        if (!DateTime.TryParse(fromMonth + "-01", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var from)) from = DateTime.Today;
        var plan = await _mps.CreateAsync(name, from, months, note, itemIds);
        Success($"Đã lập kế hoạch {plan.Code}. Nhập dự báo, bấm \"Đề xuất\" để tính số lượng sản xuất theo đơn hàng và tồn kho.");
        return RedirectToAction(nameof(Details), new { id = plan.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var plan = await Db.MpsPlans.FindAsync(id);
        if (plan == null) return NotFound();
        ViewBag.CanEdit = CanEdit;
        ViewBag.Orders = await Db.ProductionOrders.Where(p => Db.MpsLines.Any(l => l.MpsPlanId == id && l.ProductionOrderId == p.Id)).ToDictionaryAsync(p => p.Id);
        return View((plan, await _mps.ComputeAsync(plan)));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, Dictionary<int, decimal> forecast, Dictionary<int, decimal> planned)
    {
        if (!CanEdit) return Denied();
        var lines = await Db.MpsLines.Where(l => l.MpsPlanId == id).ToListAsync();
        foreach (var l in lines)
        {
            if (forecast.TryGetValue(l.Id, out var f)) l.ForecastQty = Math.Max(0, f);
            if (planned.TryGetValue(l.Id, out var p) && l.ProductionOrderId == null) l.PlannedQty = Math.Max(0, p);
        }
        await Db.SaveChangesAsync();
        Success("Đã lưu kế hoạch.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Suggest(int id, Dictionary<int, decimal> forecast)
    {
        if (!CanEdit) return Denied();
        var plan = await Db.MpsPlans.FindAsync(id) ?? throw new BusinessException("Không tìm thấy kế hoạch.");
        foreach (var l in await Db.MpsLines.Where(l => l.MpsPlanId == id).ToListAsync())
            if (forecast.TryGetValue(l.Id, out var f)) l.ForecastQty = Math.Max(0, f);
        await Db.SaveChangesAsync();
        await _mps.ApplySuggestionsAsync(plan);
        Success("Đã đề xuất số lượng sản xuất = nhu cầu (đơn hàng + dự báo) + tồn tối thiểu − tồn dự kiến − lệnh đang mở. Điều chỉnh rồi lưu.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOrders(int id, int[] lineIds)
    {
        if (!CanEdit) return Denied();
        var plan = await Db.MpsPlans.FindAsync(id) ?? throw new BusinessException("Không tìm thấy kế hoạch.");
        var orders = await _mps.CreateOrdersAsync(plan, lineIds);
        Success($"Đã tạo {orders.Count} lệnh sản xuất nháp: {string.Join(", ", orders.Select(o => o.Number))}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!CanEdit) return Denied();
        var plan = await Db.MpsPlans.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return NotFound();
        Db.MpsLines.RemoveRange(plan.Lines); Db.MpsPlans.Remove(plan);
        await Db.SaveChangesAsync();
        Success($"Đã xóa kế hoạch {plan.Code} (lệnh sản xuất đã tạo được giữ nguyên).");
        return RedirectToAction(nameof(Index));
    }
}
