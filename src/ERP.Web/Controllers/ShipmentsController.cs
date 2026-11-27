using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Giao hàng: phiếu giao hàng (từ phiếu xuất kho theo đơn bán), giao một phần, theo dõi vận chuyển tới khi khách nhận</summary>
[Authorize(Roles = AppRoles.SalesTeam + "," + AppRoles.WarehouseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class ShipmentsController : ErpControllerBase
{
    public ShipmentsController(ErpDbContext db) : base(db) { }
    private bool CanEdit => User.InAny(AppRoles.SalesTeam + "," + AppRoles.WarehouseTeam);

    public async Task<IActionResult> Index(ShipmentStatus? status)
    {
        var shipped = await Db.Shipments.Select(s => s.InventoryDocId).ToListAsync();
        ViewBag.Pending = await Db.InventoryDocs.Include(d => d.Partner).Include(d => d.Warehouse)
            .Where(d => d.Origin == DocOrigin.Sales && d.Type == InvDocType.Issue && d.Status == DocStatus.Posted && !shipped.Contains(d.Id)).OrderByDescending(d => d.Date).Take(100).ToListAsync();
        var q = Db.Shipments.Include(s => s.InventoryDoc).ThenInclude(d => d!.Partner).Include(s => s.SalesOrder).AsQueryable();
        if (status != null) q = q.Where(s => s.Status == status);
        ViewBag.Status = status; ViewBag.CanEdit = CanEdit;
        return View(await q.OrderByDescending(s => s.Id).Take(300).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int docId)
    {
        if (!CanEdit) return Denied();
        var d = await Db.InventoryDocs.Include(x => x.Partner).FirstOrDefaultAsync(x => x.Id == docId); if (d == null) return NotFound();
        if (await Db.Shipments.AnyAsync(s => s.InventoryDocId == docId)) throw new BusinessException("Phiếu xuất đã có vận đơn.");
        var s = new Shipment { InventoryDocId = docId, SalesOrderId = d.Origin == DocOrigin.Sales ? d.OriginId : null, Address = d.Partner?.Address, Receiver = d.Partner?.ContactPerson,
            Number = await DocNumbers.NextAsync(Db.Shipments.Select(x => x.Number), "VD", DateTime.Today) };
        Db.Shipments.Add(s); await Db.SaveChangesAsync();
        Success($"Đã lập vận đơn {s.Number} cho phiếu xuất {d.Number}.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, ShipmentStatus status, string? carrier, string? vehicleNo, string? driver, string? trackingNo, string? address, string? receiver, decimal freight, string? note)
    {
        if (!CanEdit) return Denied();
        var s = await Db.Shipments.FindAsync(id); if (s == null) return NotFound();
        s.Carrier = carrier; s.VehicleNo = vehicleNo; s.Driver = driver; s.TrackingNo = trackingNo; s.Address = address; s.Receiver = receiver; s.Freight = Math.Max(0, freight); s.Note = note;
        if (status != s.Status)
        {
            if (status == ShipmentStatus.InTransit && s.ShippedAt == null) s.ShippedAt = DateTime.Now;
            if (status == ShipmentStatus.Delivered) { s.ShippedAt ??= DateTime.Now; s.DeliveredAt = DateTime.Now; }
            s.Status = status;
        }
        await Db.SaveChangesAsync();
        Success($"Đã cập nhật vận đơn {s.Number}: {s.Status.GetDisplayName()}.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>In phiếu giao hàng từ phiếu xuất kho theo đơn bán (kèm số lượng đã giao, còn phải giao trên đơn)</summary>
    public async Task<IActionResult> Note(int docId, [FromServices] IOptions<ErpSettings> cfg)
    {
        var d = await Db.InventoryDocs.Include(x => x.Partner).Include(x => x.Warehouse).Include(x => x.Lines).ThenInclude(l => l.Item).FirstOrDefaultAsync(x => x.Id == docId);
        if (d == null) return NotFound();
        ViewBag.Company = cfg.Value;
        ViewBag.Shipment = await Db.Shipments.FirstOrDefaultAsync(s => s.InventoryDocId == docId);
        ViewBag.Order = d.OriginId == null ? null : await Db.SalesOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == d.OriginId);
        return View(d);
    }
}
