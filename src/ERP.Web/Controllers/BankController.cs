using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Đối chiếu sao kê ngân hàng (nhập file / API), khớp tự động – thủ công, lập phiếu từ sao kê; in ủy nhiệm chi</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class BankController : ErpControllerBase
{
    private readonly BankService _bank;
    public BankController(ErpDbContext db, BankService bank) : base(db) => _bank = bank;

    public async Task<IActionResult> Index()
    {
        ViewBag.Banks = new SelectList(await Db.CashAccounts.Where(c => c.Type == CashAccountType.Bank && c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Id, Name = c.Code + " – " + c.Name + " (" + c.CurrencyCode + ")" }).ToListAsync(), "Id", "Name");
        return View(await Db.BankStatements.Include(s => s.CashAccount).Include(s => s.Lines).OrderByDescending(s => s.ToDate).ThenByDescending(s => s.Id).ToListAsync());
    }

    /// <summary>Nhập sao kê từ Excel / CSV: cột Ngày, Nội dung / Diễn giải, Số tham chiếu, Ghi có (tiền vào), Ghi nợ (tiền ra) – hoặc một cột Số tiền có dấu</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting), RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Import(int cashAccountId, IFormFile? file, decimal? opening, decimal? closing, string? name)
    {
        if (file == null || file.Length == 0) { Error("Chọn file sao kê."); return RedirectToAction(nameof(Index)); }
        List<Dictionary<string, string>> raw;
        await using (var s = file.OpenReadStream()) raw = TabularReader.Read(s, file.FileName);
        // Sao kê ngân hàng thường có vài dòng thông tin phía trên: tìm dòng tiêu đề có cột "Ngày"
        var rows = new List<BankService.StatementRow>(); var skipped = 0;
        foreach (var r in raw)
        {
            var d = TabularReader.Date(TabularReader.Get(r, "Ngày", "Ngày giao dịch", "Ngày GD", "Date", "Transaction date", "Ngày hiệu lực"));
            if (d == null) { skipped++; continue; }
            var amount = TabularReader.Get(r, "Số tiền", "Amount") is { } a ? TabularReader.Dec(a)
                : TabularReader.Dec(TabularReader.Get(r, "Ghi có", "Có", "Tiền vào", "Credit", "Số tiền ghi có", "PS Có")) - TabularReader.Dec(TabularReader.Get(r, "Ghi nợ", "Nợ", "Tiền ra", "Debit", "Số tiền ghi nợ", "PS Nợ"));
            if (amount == 0) { skipped++; continue; }
            rows.Add(new(d.Value, TabularReader.Get(r, "Nội dung", "Diễn giải", "Mô tả", "Description", "Nội dung giao dịch") ?? "", TabularReader.Get(r, "Số tham chiếu", "Số bút toán", "Reference", "Mã giao dịch", "Số CT"), amount));
        }
        if (rows.Count == 0) { Error("Không đọc được dòng giao dịch nào (cần cột Ngày và Ghi có / Ghi nợ hoặc Số tiền)."); return RedirectToAction(nameof(Index)); }
        var st = await _bank.ImportAsync(cashAccountId, name ?? "", opening ?? 0, closing, rows, "Tệp " + file.FileName);
        Success($"Đã nhập {rows.Count} giao dịch{(skipped > 0 ? $" (bỏ qua {skipped} dòng không phải giao dịch)" : "")}; tự khớp {st.Lines.Count(l => l.CashVoucherId != null)} dòng.");
        return RedirectToAction(nameof(Details), new { id = st.Id });
    }

    public async Task<IActionResult> Details(int id, [FromServices] CashService cash)
    {
        var st = await Db.BankStatements.Include(s => s.CashAccount).ThenInclude(c => c!.Account).Include(s => s.Lines).ThenInclude(l => l.CashVoucher).FirstOrDefaultAsync(s => s.Id == id);
        if (st == null) return NotFound();
        var matched = st.Lines.Where(l => l.CashVoucherId != null).Select(l => l.CashVoucherId!.Value).ToHashSet();
        var used = (await Db.BankStatementLines.Where(l => l.CashVoucherId != null).Select(l => l.CashVoucherId!.Value).ToListAsync()).ToHashSet();
        var from = st.FromDate.AddDays(-10); var to = st.ToDate.AddDays(10);
        var vouchers = await Db.CashVouchers.Include(v => v.Partner).Where(v => v.Status == DocStatus.Posted && (v.CashAccountId == st.CashAccountId || v.ToCashAccountId == st.CashAccountId) && v.Date >= from && v.Date <= to).OrderBy(v => v.Date).ToListAsync();
        ViewBag.Unmatched = vouchers.Where(v => !used.Contains(v.Id)).ToList();
        ViewBag.BookBalance = await _bank.BookBalanceAsync(st.CashAccount!, st.ToDate);
        ViewBag.Partners = await PartnersAsync();
        ViewBag.Counter = await AccountSelectAsync(null, a => a.Code[0] is '5' or '6' or '7' or '8' or '3' or '1');
        return View(st);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> AutoMatch(int id)
    {
        var n = await _bank.AutoMatchAsync(id);
        Success($"Đã tự khớp thêm {n} dòng.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Match(int id, int lineId, int voucherId)
    {
        await _bank.MatchAsync(lineId, voucherId);
        Success("Đã khớp dòng sao kê với phiếu.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Unmatch(int id, int lineId)
    {
        var l = await Db.BankStatementLines.FirstOrDefaultAsync(x => x.Id == lineId && x.BankStatementId == id);
        if (l != null) { l.CashVoucherId = null; l.MatchType = null; await Db.SaveChangesAsync(); Success("Đã bỏ khớp."); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> CreateVoucher(int id, int lineId, CashPurpose purpose, int? partnerId, int? counterAccountId)
    {
        var v = await _bank.CreateVoucherAsync(lineId, purpose, partnerId, counterAccountId);
        Success($"Đã lập và ghi sổ {v.Number} theo sao kê.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> Delete(int id)
    {
        var st = await Db.BankStatements.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id);
        if (st == null) return NotFound();
        Db.BankStatementLines.RemoveRange(st.Lines); Db.BankStatements.Remove(st);
        await Db.SaveChangesAsync();
        Success("Đã xóa sao kê (phiếu thu chi đã lập vẫn giữ nguyên).");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>In ủy nhiệm chi cho phiếu chi qua ngân hàng</summary>
    public async Task<IActionResult> Unc(int id, [FromServices] IOptions<ErpSettings> cfg)
    {
        var v = await Db.CashVouchers.Include(x => x.CashAccount).Include(x => x.Partner).Include(x => x.ToCashAccount).FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();
        if (v.Type == CashVoucherType.Receipt || v.CashAccount?.Type != CashAccountType.Bank) { Error("Ủy nhiệm chi chỉ lập cho phiếu chi / chuyển tiền từ tài khoản ngân hàng."); return RedirectToAction("Details", "Cash", new { id }); }
        ViewBag.Company = cfg.Value;
        return View(v);
    }
}
