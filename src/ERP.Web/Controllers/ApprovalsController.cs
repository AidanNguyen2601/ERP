using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Hộp duyệt chứng từ và cấu hình quy tắc phê duyệt</summary>
public class ApprovalsController : ErpControllerBase
{
    private readonly WorkflowService _wf;
    private readonly PurchaseService _purchase;
    private readonly SalesService _sales;
    private readonly AccountingService _acc;
    private readonly PayrollService _payroll;
    public ApprovalsController(ErpDbContext db, WorkflowService wf, PurchaseService purchase, SalesService sales, AccountingService acc, PayrollService payroll) : base(db)
    { _wf = wf; _purchase = purchase; _sales = sales; _acc = acc; _payroll = payroll; }

    public static string? DocUrl(IUrlHelper url, ApprovalDocType t, int id) => t switch
    {
        ApprovalDocType.PurchaseOrder => url.Action("Details", "PurchaseOrders", new { id }),
        ApprovalDocType.SalesOrder => url.Action("Details", "SalesOrders", new { id }),
        ApprovalDocType.JournalEntry => url.Action("Details", "Journals", new { id }),
        ApprovalDocType.Payroll => url.Action("Details", "Payroll", new { id }),
        ApprovalDocType.PurchaseRequisition => url.Action("Details", "Requisitions", new { id }),
        _ => null
    };

    public async Task<IActionResult> Index()
    {
        ViewBag.Inbox = await _wf.InboxAsync(User);
        var me = Me;
        ViewBag.Mine = await Db.ApprovalRequests.Include(r => r.Steps).Where(r => r.RequestedById == me).OrderByDescending(r => r.Id).Take(30).ToListAsync();
        ViewBag.Recent = await Db.ApprovalRequests.Include(r => r.Steps).Include(r => r.RequestedBy).Where(r => r.Status != ApprovalStatus.Pending).OrderByDescending(r => r.CompletedAt).Take(30).ToListAsync();
        ViewBag.AllPending = User.InAny(AppRoles.Approvers + "," + AppRoles.Accountant)
            ? await Db.ApprovalRequests.Include(r => r.Steps).Include(r => r.RequestedBy).Where(r => r.Status == ApprovalStatus.Pending).OrderBy(r => r.RequestedAt).ToListAsync()
            : new List<ApprovalRequest>();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Act(int id, bool approve, string? comment, string? returnUrl)
    {
        var req = await Db.ApprovalRequests.Include(r => r.Steps).FirstOrDefaultAsync(r => r.Id == id);
        if (req == null) return NotFound();
        string msg;
        switch (req.DocType)
        {
            case ApprovalDocType.PurchaseOrder:
                var po = await Db.PurchaseOrders.Include(p => p.Lines).ThenInclude(l => l.Item).FirstAsync(p => p.Id == req.DocId);
                msg = await _purchase.ApproveAsync(po, approve, comment);
                break;
            case ApprovalDocType.PurchaseRequisition:
                var pr = await Db.PurchaseRequisitions.FirstAsync(p => p.Id == req.DocId);
                var prSt = await _wf.ActAsync(req, approve, comment);
                if (prSt == ApprovalStatus.Approved) pr.Status = RequestStatus.Approved; else if (prSt == ApprovalStatus.Rejected) pr.Status = RequestStatus.Draft;
                await Db.SaveChangesAsync();
                msg = prSt == ApprovalStatus.Pending ? $"Đã duyệt cấp của bạn – {WorkflowService.Describe(req)}." : prSt == ApprovalStatus.Rejected ? $"Đã từ chối đề nghị {req.DocNumber}, trở về nháp." : $"Đề nghị {req.DocNumber} đã được duyệt hoàn tất.";
                break;
            default:
                var st = await _wf.ActAsync(req, approve, comment);
                msg = st == ApprovalStatus.Pending ? $"Đã duyệt cấp của bạn – {WorkflowService.Describe(req)}." : st == ApprovalStatus.Rejected ? $"Đã từ chối {req.DocNumber}." : $"{req.DocNumber} đã được duyệt hoàn tất.";
                if (st == ApprovalStatus.Approved) msg += " " + await CompleteAsync(req);
                break;
        }
        Success(msg);
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    /// <summary>Thực hiện thao tác đang chờ sau khi được duyệt hoàn tất</summary>
    private async Task<string> CompleteAsync(ApprovalRequest req)
    {
        switch (req.DocType)
        {
            case ApprovalDocType.SalesOrder:
                var so = await Db.SalesOrders.Include(s => s.Lines).ThenInclude(l => l.Item).FirstAsync(s => s.Id == req.DocId);
                if (so.Status != SoStatus.Draft) return "";
                try { var warn = await _sales.ConfirmAsync(so, User.InAny(AppRoles.Approvers)); return $"Đã xác nhận đơn {so.Number}." + (warn != null ? " " + warn : ""); }
                catch (BusinessException ex) { return "Chưa xác nhận được đơn: " + ex.Message; }
            case ApprovalDocType.JournalEntry:
                var j = await Db.JournalEntries.Include(x => x.Lines).FirstAsync(x => x.Id == req.DocId);
                if (j.Status != DocStatus.Draft) return "";
                await _acc.PostEntryAsync(j);
                return $"Đã ghi sổ chứng từ {j.Number}.";
            case ApprovalDocType.Payroll:
                var run = await Db.PayrollRuns.Include(r => r.Lines).FirstAsync(r => r.Id == req.DocId);
                if (run.Status != DocStatus.Draft) return "";
                await _payroll.PostAsync(run);
                return $"Đã hạch toán bảng lương tháng {run.Month:00}/{run.Year}.";
        }
        return "";
    }

    // ------------------------------------------------------------------ Cấu hình quy tắc
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> Rules()
    {
        ViewBag.Users = new SelectList(await Db.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, Name = u.FullName + " (" + u.Username + ")" }).ToListAsync(), "Id", "Name");
        ViewBag.Branches = new SelectList(await Db.Branches.OrderBy(b => b.Code).Select(b => new { b.Id, Name = b.Code + " – " + b.Name }).ToListAsync(), "Id", "Name");
        return View(await Db.ApprovalRules.Include(r => r.ApproverUser).Include(r => r.Branch).OrderBy(r => r.DocType).ThenBy(r => r.Level).ThenBy(r => r.MinAmount).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> SaveRule(ApprovalRule model)
    {
        if (string.IsNullOrWhiteSpace(model.ApproverRole) && model.ApproverUserId == null) { Error("Chọn vai trò hoặc người duyệt."); return RedirectToAction(nameof(Rules)); }
        if (model.ApproverUserId != null) model.ApproverRole = null;
        var r = model.Id == 0 ? new ApprovalRule() : await Db.ApprovalRules.FindAsync(model.Id);
        if (r == null) return NotFound();
        r.DocType = model.DocType; r.Name = (model.Name ?? "").Trim(); r.Level = Math.Clamp(model.Level, 1, 9); r.MinAmount = Math.Max(0, model.MinAmount);
        r.BranchId = model.BranchId; r.ApproverRole = model.ApproverRole; r.ApproverUserId = model.ApproverUserId; r.IsActive = model.IsActive;
        if (r.Id == 0) Db.ApprovalRules.Add(r);
        await Db.SaveChangesAsync();
        Success($"Đã lưu quy tắc \"{r.Name}\". Áp dụng cho chứng từ gửi duyệt từ bây giờ.");
        return RedirectToAction(nameof(Rules));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> DeleteRule(int id)
    {
        var r = await Db.ApprovalRules.FindAsync(id);
        if (r != null) { Db.ApprovalRules.Remove(r); await Db.SaveChangesAsync(); Success($"Đã xóa quy tắc \"{r.Name}\"."); }
        return RedirectToAction(nameof(Rules));
    }
}
