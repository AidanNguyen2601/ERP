using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Phiếu bảo dưỡng / sửa chữa đột xuất: yêu cầu → duyệt → thực hiện (phụ tùng, danh mục công việc) → hoàn thành → nghiệm thu</summary>
public class MaintenanceOrdersController : ErpControllerBase
{
    private readonly MaintenanceService _svc;
    private readonly WorkflowService _wf;
    public MaintenanceOrdersController(ErpDbContext db, MaintenanceService svc, WorkflowService wf) : base(db) { _svc = svc; _wf = wf; }
    private bool CanEdit => User.InAny(AppRoles.MaintenanceTeam);
    private bool CanStock => User.InAny(AppRoles.MaintenanceTeam + "," + AppRoles.Warehouse);

    private Task<MaintenanceOrder?> LoadAsync(int id) => Db.MaintenanceOrders.Include(o => o.Equipment).Include(o => o.Plan).Include(o => o.Warehouse)
        .Include(o => o.Parts).ThenInclude(p => p.Item).Include(o => o.Tasks).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id);

    public async Task<IActionResult> Index(MaintenanceStatus? status, MaintenanceKind? kind, int? equipmentId, string? q, bool open = false)
    {
        var query = Db.MaintenanceOrders.Include(o => o.Equipment).AsQueryable();
        if (status.HasValue) query = query.Where(o => o.Status == status);
        if (kind.HasValue) query = query.Where(o => o.Kind == kind);
        if (equipmentId.HasValue) query = query.Where(o => o.EquipmentId == equipmentId);
        if (open) query = query.Where(o => o.Status != MaintenanceStatus.Accepted && o.Status != MaintenanceStatus.Cancelled);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(o => o.Number.Contains(q) || o.Title.Contains(q) || o.Equipment!.Name.Contains(q) || o.Equipment.Code.Contains(q));
        ViewBag.Status = status; ViewBag.Kind = kind; ViewBag.EquipmentId = equipmentId; ViewBag.Q = q; ViewBag.Open = open; ViewBag.CanEdit = CanEdit;
        ViewBag.Counts = (await Db.MaintenanceOrders.Select(o => o.Status).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        ViewBag.Equipments = new SelectList(await Db.Equipments.OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.Name }).ToListAsync(), "Id", "Name", equipmentId);
        return View(await query.OrderByDescending(o => o.DueDate).ThenByDescending(o => o.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var o = await LoadAsync(id);
        if (o == null) return NotFound();
        ViewBag.CanEdit = CanEdit; ViewBag.CanStock = CanStock;
        ViewBag.Approval = await _wf.LatestAsync(ApprovalDocType.MaintenanceOrder, id);
        ViewBag.Docs = await Db.InventoryDocs.Where(d => d.Origin == DocOrigin.Maintenance && d.OriginId == id).OrderBy(d => d.Date).ThenBy(d => d.Id).ToListAsync();
        ViewBag.Stock = o.WarehouseId == null ? new Dictionary<int, decimal>() : (await Db.StockLevels.Where(s => s.WarehouseId == o.WarehouseId && o.Parts.Select(p => p.ItemId).Contains(s.ItemId)).ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        ViewBag.Items = new SelectList(await Db.Items.Where(i => i.IsActive && i.Type != ItemType.Service && i.Type != ItemType.FinishedGood).OrderBy(i => i.Code).Select(i => new { i.Id, Name = i.Code + " – " + i.Name + " (" + i.Unit + ")" }).ToListAsync(), "Id", "Name");
        return View(o);
    }

    public async Task<IActionResult> Create(MaintenanceKind kind = MaintenanceKind.Breakdown, int? equipmentId = null)
    {
        if (!CanEdit && !User.Identity!.IsAuthenticated) return Denied();
        var m = new MaintenanceOrder { Kind = kind, EquipmentId = equipmentId, RequestedBy = User.GetFullName() };
        await LoadFormAsync(m);
        return View("Form", m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(MaintenanceOrder model, bool submit)
    {
        ModelState.Remove(nameof(model.Number));
        if (!ModelState.IsValid) { await LoadFormAsync(model); return View("Form", model); }
        var o = model.Id == 0 ? null : await Db.MaintenanceOrders.FindAsync(model.Id);
        if (o != null && o.Status != MaintenanceStatus.Draft) { Error("Phiếu đã gửi duyệt / thực hiện, không sửa được."); return RedirectToAction(nameof(Details), new { id = o.Id }); }
        if (o == null) o = await _svc.CreateAsync(new MaintenanceOrder { Kind = model.Kind, EquipmentId = model.EquipmentId, Title = model.Title, ReportedAt = model.ReportedAt, RequestedBy = model.RequestedBy });
        o.Kind = model.Kind; o.EquipmentId = model.EquipmentId; o.Title = model.Title.Trim(); o.Symptom = model.Symptom; o.Priority = model.Priority; o.DueDate = model.DueDate.Date;
        o.ReportedAt = model.ReportedAt; o.RequestedBy = model.RequestedBy; o.Technician = model.Technician; o.WarehouseId = model.WarehouseId; o.EstimatedCost = model.EstimatedCost;
        await Db.SaveChangesAsync();
        if (submit)
        {
            var msg = await _svc.SubmitAsync(o);
            if (msg != null) TempData["Warning"] = msg; else Success($"Phiếu {o.Number} không cần duyệt theo quy tắc, đã được duyệt tự động.");
        }
        else Success($"Đã lưu phiếu {o.Number}.");
        return RedirectToAction(nameof(Details), new { id = o.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var o = await Db.MaintenanceOrders.FindAsync(id);
        if (o == null) return NotFound();
        var msg = await _svc.SubmitAsync(o);
        if (msg != null) TempData["Warning"] = msg; else Success($"Phiếu {o.Number} không cần duyệt theo quy tắc, đã được duyệt tự động.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(int id)
    {
        if (!CanEdit) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        await _svc.StartAsync(o);
        Success($"Bắt đầu thực hiện {o.Number}." + (o.Kind == MaintenanceKind.Breakdown ? " Thiết bị chuyển trạng thái dừng (hỏng)." : ""));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPart(int id, int itemId, decimal qty, int? warehouseId)
    {
        if (!CanEdit) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        if (warehouseId.HasValue && o.WarehouseId != warehouseId) { o.WarehouseId = warehouseId; await Db.SaveChangesAsync(); }
        await _svc.AddPartAsync(o, itemId, qty);
        Success("Đã thêm phụ tùng vào phiếu.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> IssuePart(int id, int partId, decimal qty, DateTime? date)
    {
        if (!CanStock) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        var doc = await _svc.IssuePartAsync(o, partId, qty, date ?? DateTime.Today);
        Success($"Đã xuất phụ tùng phiếu {doc.Number}, trị giá {doc.TotalAmount.ToMoney()} đ (ghi chi phí bảo dưỡng).");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnPart(int id, int partId, decimal qty, DateTime? date)
    {
        if (!CanStock) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        var doc = await _svc.ReturnPartAsync(o, partId, qty, date ?? DateTime.Today);
        Success($"Đã hoàn trả phụ tùng phiếu {doc.Number}, trị giá {doc.TotalAmount.ToMoney()} đ.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RecoverPart(int id, int partId, decimal qty)
    {
        if (!CanEdit) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        await _svc.RecoverOldPartAsync(o, partId, qty);
        Success("Đã ghi nhận phụ tùng cũ thu hồi.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleTask(int id, int taskId, bool done, string? result)
    {
        if (!CanEdit) return Denied();
        var t = await Db.MaintenanceTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.MaintenanceOrderId == id);
        if (t == null) return NotFound();
        t.Done = done; t.Result = result;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTask(int id, string description)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(description)) { Error("Nhập nội dung công việc."); return RedirectToAction(nameof(Details), new { id }); }
        var next = (await Db.MaintenanceTasks.Where(x => x.MaintenanceOrderId == id).MaxAsync(x => (int?)x.Seq) ?? 0) + 1;
        Db.MaintenanceTasks.Add(new MaintenanceTask { MaintenanceOrderId = id, Seq = next, Description = description.Trim() });
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, string? cause, string? resolution, decimal laborHours, decimal laborRate, decimal outsideCost, decimal? downtimeHours, string? technician)
    {
        if (!CanEdit) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        await _svc.CompleteAsync(o, cause, resolution, laborHours, laborRate, outsideCost, downtimeHours, technician);
        Success($"Đã hoàn thành {o.Number}: dừng máy {o.DowntimeHours.ToQty()} giờ, chi phí {o.TotalCost.ToMoney()} đ. Chờ nghiệm thu.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(int id, string by, string? note)
    {
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        if (!User.InAny(AppRoles.MaintenanceTeam + "," + AppRoles.Approvers) && o.RequestedBy != User.GetFullName()) return Denied();
        await _svc.AcceptAsync(o, by, note);
        Success($"Đã nghiệm thu {o.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!CanEdit) return Denied();
        var o = await LoadAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        await _svc.CancelAsync(o);
        Success($"Đã hủy phiếu {o.Number}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LoadFormAsync(MaintenanceOrder m)
    {
        ViewBag.Equipments = new SelectList(await Db.Equipments.Where(e => e.Status != EquipmentStatus.Retired || e.Id == m.EquipmentId).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.Name }).ToListAsync(), "Id", "Name", m.EquipmentId);
        ViewBag.Whs = await WarehousesAsync(m.WarehouseId);
    }
}
