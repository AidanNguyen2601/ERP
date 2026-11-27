using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Đối chiếu số liệu các phân hệ (kho, mua, bán, công nợ, quỹ, TSCĐ, sản xuất, thuế) với sổ cái</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class ReconciliationController : ErpControllerBase
{
    private readonly ReconciliationService _svc;
    public ReconciliationController(ErpDbContext db, ReconciliationService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index() => View(await _svc.RunAsync());
}
