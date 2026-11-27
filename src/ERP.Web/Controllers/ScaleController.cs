using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

/// <summary>Phiếu cân nhận từ thiết bị cân điện tử (qua API) để đối chiếu khi nhập / xuất kho</summary>
public class ScaleController : ErpControllerBase
{
    public ScaleController(ErpDbContext db) : base(db) { }

    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        var (f, t) = Range(from, to);
        ViewBag.From = f; ViewBag.To = t;
        var end = t.AddDays(1);
        return View(await Db.ScaleTickets.Include(s => s.Item).Where(s => s.WeighedAt >= f && s.WeighedAt < end).OrderByDescending(s => s.WeighedAt).ToListAsync());
    }
}
