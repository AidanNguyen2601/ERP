using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

public class HomeController : ErpControllerBase
{
    private readonly AccountingService _acc;
    public HomeController(ErpDbContext db, AccountingService acc) : base(db) => _acc = acc;

    public async Task<IActionResult> Index()
    {
        var today = DateTime.Today;
        var m0 = new DateTime(today.Year, today.Month, 1);
        var vm = new DashboardViewModel();
        if (User.SeesFinance())
        {
            vm.RevenueMonth = -await _acc.MovementAsync("511", m0, today);
            vm.GrossProfitMonth = vm.RevenueMonth - await _acc.MovementAsync("632", m0, today);
            vm.CashBalance = await _acc.BalanceAsync("111") + await _acc.BalanceAsync("112");
            vm.Receivable = await _acc.BalanceAsync("131");
            vm.Payable = -await _acc.BalanceAsync("331");
            vm.AssetNbv = await _acc.BalanceAsync("211") + await _acc.BalanceAsync("214");
            for (var i = 5; i >= 0; i--)
            {
                var f = m0.AddMonths(-i); var t = f.AddMonths(1).AddDays(-1);
                var rev = -await _acc.MovementAsync("511", f, t);
                var cost = await _acc.MovementAsync("632", f, t) + await _acc.MovementAsync("641", f, t) + await _acc.MovementAsync("642", f, t);
                vm.Monthly.Add(($"{f:MM/yy}", rev, cost));
            }
            var open = await Db.Invoices.Include(i => i.Partner)
                .Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total && i.DueDate < today).ToListAsync();
            vm.OverdueInvoices = open.OrderBy(i => i.DueDate).Take(6).ToList();
            vm.ReceivableOverdue = open.Sum(i => i.Outstanding);
            vm.DraftInvoices = await Db.Invoices.CountAsync(i => i.Status == InvoiceStatus.Draft);
        }
        vm.InventoryValue = await Db.StockLevels.Where(s => s.Item!.Type != ItemType.Service).SumAsync(s => (decimal?)(s.Quantity * s.Item!.AvgCost)) ?? 0;
        var low = await Db.Items.Where(i => i.IsActive && i.MinStock > 0)
            .Select(i => new { Item = i, OnHand = Db.StockLevels.Where(s => s.ItemId == i.Id).Sum(s => (decimal?)s.Quantity) ?? 0 })
            .Where(x => x.OnHand < x.Item.MinStock).ToListAsync();
        vm.LowStock = low.Select(x => (x.Item, x.OnHand)).OrderBy(x => x.OnHand / x.Item.MinStock).ToList();
        vm.PoPendingApproval = await Db.PurchaseOrders.CountAsync(p => p.Status == PoStatus.PendingApproval);
        vm.SoToDeliver = await Db.SalesOrders.CountAsync(s => s.Status == SoStatus.Confirmed || s.Status == SoStatus.PartiallyDelivered);
        vm.ProductionOpen = await Db.ProductionOrders.CountAsync(p => p.Status == ProductionStatus.Released || p.Status == ProductionStatus.InProgress || p.Status == ProductionStatus.Draft);
        return View(vm);
    }

    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
