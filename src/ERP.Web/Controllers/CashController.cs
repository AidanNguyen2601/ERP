using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Controllers;

/// <summary>Quản lý dòng tiền: quỹ tiền mặt, tài khoản ngân hàng, phiếu thu / chi, chuyển tiền, sổ quỹ, dự báo</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class CashController : ErpControllerBase
{
    private readonly CashService _cash;
    private readonly AccountingService _acc;
    private readonly ReportService _rpt;
    public CashController(ErpDbContext db, CashService cash, AccountingService acc, ReportService rpt) : base(db) { _cash = cash; _acc = acc; _rpt = rpt; }

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, CashVoucherType? type, int? cashAccountId, string? q)
    {
        var (f, t) = Range(from, to);
        var accounts = await Db.CashAccounts.Include(c => c.Account).OrderBy(c => c.Code).ToListAsync();
        var balances = new Dictionary<int, decimal>();
        foreach (var a in accounts) balances[a.Id] = await _cash.BalanceAsync(a);
        var vq = Db.CashVouchers.Include(v => v.CashAccount).Include(v => v.ToCashAccount).Include(v => v.Partner).Where(v => v.Date >= f && v.Date <= t);
        if (type.HasValue) vq = vq.Where(v => v.Type == type);
        if (cashAccountId.HasValue) vq = vq.Where(v => v.CashAccountId == cashAccountId || v.ToCashAccountId == cashAccountId);
        if (!string.IsNullOrWhiteSpace(q)) vq = vq.Where(v => v.Number.Contains(q) || v.Description.Contains(q));
        ViewBag.Accounts = accounts; ViewBag.Balances = balances; ViewBag.From = f; ViewBag.To = t; ViewBag.Type = type; ViewBag.CashAccountId = cashAccountId; ViewBag.Q = q;
        ViewBag.CashSelect = await CashAccountsAsync(cashAccountId);
        return View(await vq.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id).Take(200).ToListAsync());
    }

    [Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Create(CashVoucherType type = CashVoucherType.Receipt, int? partnerId = null, int? invoiceId = null)
    {
        var f = new CashVoucherForm
        {
            Type = type, PartnerId = partnerId, InvoiceId = invoiceId,
            Purpose = type == CashVoucherType.Receipt ? CashPurpose.CustomerReceipt : type == CashVoucherType.Payment ? CashPurpose.VendorPayment : CashPurpose.Internal,
            CashAccountId = (await Db.CashAccounts.Where(c => c.Type == CashAccountType.Bank).OrderBy(c => c.Id).FirstOrDefaultAsync())?.Id
        };
        if (invoiceId.HasValue && await Db.Invoices.Include(i => i.Partner).FirstOrDefaultAsync(i => i.Id == invoiceId) is { } inv)
        {
            f.PartnerId = inv.PartnerId; f.Amount = inv.Outstanding;
            f.Description = (type == CashVoucherType.Receipt ? "Thu tiền" : "Thanh toán") + $" HĐ {inv.Series} {inv.InvoiceNo} – {inv.Partner?.Name}";
        }
        await LoadAsync(f);
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Save(CashVoucherForm form)
    {
        if (!ModelState.IsValid) { await LoadAsync(form); return View("Form", form); }
        try
        {
            var v = await _cash.CreateAsync(form);
            var alloc = v.Allocations.Any() ? $" Đã cấn trừ {v.Allocations.Count} hóa đơn." : "";
            Success($"Đã lập {v.Type.GetDisplayName().ToLower()} {v.Number} – {v.Amount.ToMoney()} đ.{alloc}");
            return RedirectToAction(nameof(Details), new { id = v.Id });
        }
        catch (BusinessException ex)
        {
            ModelState.AddModelError("", ex.Message);
            await LoadAsync(form);
            return View("Form", form);
        }
    }

    public async Task<IActionResult> Details(int id)
    {
        var v = await Db.CashVouchers.Include(x => x.CashAccount).ThenInclude(c => c!.Account).Include(x => x.ToCashAccount).Include(x => x.Partner)
            .Include(x => x.CounterAccount).Include(x => x.CreatedBy).Include(x => x.Allocations).ThenInclude(a => a.Invoice).FirstOrDefaultAsync(x => x.Id == id);
        return v == null ? NotFound() : View(v);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var v = await Db.CashVouchers.Include(x => x.Allocations).FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do hủy phiếu.");
        await _cash.CancelAsync(v, reason);
        Success($"Đã hủy phiếu {v.Number} và lập bút toán đảo.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Sổ quỹ tiền mặt / sổ tiền gửi ngân hàng</summary>
    public async Task<IActionResult> Book(int id, DateTime? from, DateTime? to)
    {
        var ca = await Db.CashAccounts.Include(c => c.Account).FirstOrDefaultAsync(c => c.Id == id);
        if (ca == null) return NotFound();
        var (f, t) = Range(from, to);
        var (opening, rows) = await _rpt.LedgerAsync(ca.Account!.Code, f, t);
        ViewBag.CashAccount = ca; ViewBag.From = f; ViewBag.To = t; ViewBag.Opening = opening;
        return View(rows);
    }

    /// <summary>Báo cáo dòng tiền thực tế theo tháng + dự báo 8 tuần tới từ công nợ đến hạn</summary>
    public async Task<IActionResult> Flow(int months = 4)
    {
        months = Math.Clamp(months, 1, 12);
        var m0 = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-(months - 1));
        var vouchers = await Db.CashVouchers.Include(v => v.CounterAccount)
            .Where(v => v.Status == DocStatus.Posted && v.Date >= m0 && v.Type != CashVoucherType.Transfer).ToListAsync();
        string Cat(CashVoucher v) => v.Purpose switch
        {
            CashPurpose.CustomerReceipt => "Thu tiền khách hàng",
            CashPurpose.VendorPayment => "Trả nhà cung cấp",
            _ => v.CounterAccount?.Code switch
            {
                var c when c != null && c.StartsWith("334") => "Trả lương",
                var c when c != null && (c.StartsWith("515") || c.StartsWith("635") || c.StartsWith("341")) => "Hoạt động tài chính",
                var c when c != null && c.StartsWith("627") => "Chi phí sản xuất chung",
                _ => v.Type == CashVoucherType.Receipt ? "Thu khác" : "Chi phí hoạt động khác"
            }
        };
        var monthsList = Enumerable.Range(0, months).Select(i => m0.AddMonths(i)).ToList();
        ViewBag.Months = monthsList;
        ViewBag.Rows = vouchers.GroupBy(v => (v.Type, Cat: Cat(v))).OrderBy(g => g.Key.Type).ThenBy(g => g.Key.Cat)
            .Select(g => (g.Key.Type, g.Key.Cat, Values: monthsList.Select(m => g.Where(v => v.Date.Year == m.Year && v.Date.Month == m.Month).Sum(v => v.Amount)).ToList())).ToList();

        // Dự báo: số dư tiền hiện tại + hóa đơn bán đến hạn − hóa đơn mua đến hạn (theo tuần)
        var cashNow = await _acc.BalanceAsync("111") + await _acc.BalanceAsync("112");
        var open = await Db.Invoices.Include(i => i.Partner).Where(i => i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).ToListAsync();
        var start = DateTime.Today;
        var weeks = new List<(DateTime From, DateTime To, decimal In, decimal Out, decimal Balance)>();
        var bal = cashNow;
        for (var w = 0; w < 8; w++)
        {
            var wf = start.AddDays(7 * w); var wt = wf.AddDays(6);
            bool InWeek(Invoice i) => w == 0 ? i.DueDate <= wt : i.DueDate >= wf && i.DueDate <= wt;    // tuần đầu gồm cả nợ quá hạn
            var inn = open.Where(i => i.Direction == InvoiceDirection.Sales && InWeek(i)).Sum(i => i.Outstanding);
            var outt = open.Where(i => i.Direction == InvoiceDirection.Purchase && InWeek(i)).Sum(i => i.Outstanding);
            bal += inn - outt;
            weeks.Add((wf, wt, inn, outt, bal));
        }
        ViewBag.CashNow = cashNow; ViewBag.Weeks = weeks;
        ViewBag.OverdueIn = open.Where(i => i.Direction == InvoiceDirection.Sales && i.DueDate < start).Sum(i => i.Outstanding);
        ViewBag.OverdueOut = open.Where(i => i.Direction == InvoiceDirection.Purchase && i.DueDate < start).Sum(i => i.Outstanding);
        ViewBag.MonthsCount = months;
        return View();
    }

    private async Task LoadAsync(CashVoucherForm f)
    {
        ViewBag.CashAccounts = await CashAccountsAsync(f.CashAccountId);
        ViewBag.ToCashAccounts = await CashAccountsAsync(f.ToCashAccountId);
        ViewBag.Partners = await PartnersAsync(f.PartnerId);
        ViewBag.Counter = await AccountSelectAsync(f.CounterAccountId, a => !a.Code.StartsWith("111") && !a.Code.StartsWith("112"));
        var dir = f.Type == CashVoucherType.Receipt ? InvoiceDirection.Sales : InvoiceDirection.Purchase;
        ViewBag.OpenInvoices = await Db.Invoices.Include(i => i.Partner)
            .Where(i => i.Direction == dir && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).OrderBy(i => i.DueDate).ToListAsync();
        var bal = new Dictionary<int, decimal>();
        foreach (var a in await Db.CashAccounts.Include(c => c.Account).ToListAsync()) bal[a.Id] = await _cash.BalanceAsync(a);
        ViewBag.Balances = bal;
        ViewBag.CashCurrencies = await Db.CashAccounts.ToDictionaryAsync(c => c.Id, c => c.CurrencyCode);
    }
}
