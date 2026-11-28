using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Luồng phê duyệt nhiều cấp cấu hình được (không cần lập trình): quy tắc theo loại chứng từ, giá trị, chi nhánh → các cấp duyệt theo vai trò / người cụ thể.
/// </summary>
public class WorkflowService
{
    private readonly ErpDbContext _db;
    private readonly CurrentUser _user;
    private readonly NotificationService _notify;
    public WorkflowService(ErpDbContext db, CurrentUser user, NotificationService notify) { _db = db; _user = user; _notify = notify; }

    private Task NotifyApproversAsync(ApprovalRequest req) =>
        _notify.NotifyAsync(Current(req).Select(s => s.ApproverRole), Current(req).FirstOrDefault(s => s.ApproverUserId != null)?.ApproverUserId,
            $"Chờ bạn duyệt: {req.DocType.GetDisplayName()} {req.DocNumber}", $"{req.Summary} · {req.Amount:N0} đ", "/Approvals", "warning", "Phê duyệt", req.RequestedById);

    public Task<bool> HasRulesAsync(ApprovalDocType type) => _db.ApprovalRules.AnyAsync(r => r.DocType == type && r.IsActive);

    public async Task<List<ApprovalRule>> MatchAsync(ApprovalDocType type, decimal amount, int? branchId) =>
        (await _db.ApprovalRules.Where(r => r.DocType == type && r.IsActive && r.MinAmount <= amount && (r.BranchId == null || r.BranchId == branchId)).ToListAsync())
        .OrderBy(r => r.Level).ThenBy(r => r.Id).ToList();

    public Task<ApprovalRequest?> LatestAsync(ApprovalDocType type, int docId) =>
        _db.ApprovalRequests.Include(r => r.Steps).Where(r => r.DocType == type && r.DocId == docId).OrderByDescending(r => r.Id).FirstOrDefaultAsync();

    public async Task<bool> IsApprovedAsync(ApprovalDocType type, int docId) => (await LatestAsync(type, docId))?.Status == ApprovalStatus.Approved;
    public async Task<bool> IsPendingAsync(ApprovalDocType type, int docId) => (await LatestAsync(type, docId))?.Status == ApprovalStatus.Pending;

    /// <summary>Tạo yêu cầu duyệt; null nếu không có quy tắc nào áp dụng (không cần duyệt)</summary>
    public async Task<ApprovalRequest?> StartAsync(ApprovalDocType type, int docId, string number, decimal amount, string summary, int? branchId)
    {
        var rules = await MatchAsync(type, amount, branchId);
        if (rules.Count == 0) return null;
        foreach (var old in await _db.ApprovalRequests.Where(r => r.DocType == type && r.DocId == docId && r.Status == ApprovalStatus.Pending).ToListAsync())
            old.Status = ApprovalStatus.Cancelled;
        var req = new ApprovalRequest
        {
            DocType = type, DocId = docId, DocNumber = number, Amount = amount, Summary = summary.Length > 300 ? summary[..300] : summary, BranchId = branchId,
            RequestedById = _user.Id, CurrentLevel = rules.Min(r => r.Level)
        };
        foreach (var r in rules) req.Steps.Add(new ApprovalStep { Level = r.Level, Name = r.Name, ApproverRole = r.ApproverRole, ApproverUserId = r.ApproverUserId });
        _db.ApprovalRequests.Add(req);
        await _db.SaveChangesAsync();
        await _db.LogAsync("Gửi duyệt", type.ToString(), type.GetDisplayName(), docId, number, $"{{\"Giá trị\":[null,{amount}],\"Số cấp\":[null,{req.Steps.Select(s => s.Level).Distinct().Count()}]}}");
        await NotifyApproversAsync(req);
        return req;
    }

    public static bool CanAct(ApprovalStep s, ClaimsPrincipal u) =>
        s.Status == ApprovalStatus.Pending && (u.IsAdmin() || (s.ApproverUserId.HasValue ? s.ApproverUserId == u.GetUserId() : !string.IsNullOrEmpty(s.ApproverRole) && u.IsInRole(s.ApproverRole)));

    public static IEnumerable<ApprovalStep> Current(ApprovalRequest r) => r.Steps.Where(s => s.Level == r.CurrentLevel && s.Status == ApprovalStatus.Pending);

    public static string Describe(ApprovalRequest r) =>
        r.Status != ApprovalStatus.Pending ? r.Status.GetDisplayName()
        : $"Chờ duyệt cấp {r.CurrentLevel}/{r.Steps.Max(s => s.Level)}: " + string.Join(", ", Current(r).Select(s => s.Name + (s.ApproverRole != null ? $" ({AppRoles.DisplayName(s.ApproverRole)})" : "")));

    /// <summary>Duyệt / từ chối bước hiện tại. Trả về trạng thái yêu cầu sau thao tác. Không có người dùng (tác vụ hệ thống) thì duyệt hết.</summary>
    public async Task<ApprovalStatus> ActAsync(ApprovalRequest req, bool approve, string? comment)
    {
        if (req.Status != ApprovalStatus.Pending) throw new BusinessException("Yêu cầu duyệt đã kết thúc.");
        var u = _user.Principal;
        var steps = Current(req).ToList();
        var mine = u == null ? steps : steps.Where(s => CanAct(s, u)).ToList();
        if (mine.Count == 0) throw new BusinessException("Bạn không phải người duyệt của cấp hiện tại (" + string.Join(", ", steps.Select(s => s.Name)) + ").");
        foreach (var s in u == null ? req.Steps.Where(x => x.Status == ApprovalStatus.Pending).ToList() : mine.Take(1).ToList())
        {
            s.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            s.ActedById = _user.Id; s.ActedByName = _user.Name; s.ActedAt = DateTime.Now; s.Comment = comment;
        }
        if (!approve)
        {
            req.Status = ApprovalStatus.Rejected; req.CompletedAt = DateTime.Now;
            foreach (var s in req.Steps.Where(x => x.Status == ApprovalStatus.Pending)) s.Status = ApprovalStatus.Cancelled;
        }
        else if (!Current(req).Any())
        {
            var next = req.Steps.Where(x => x.Status == ApprovalStatus.Pending).Select(x => x.Level).DefaultIfEmpty(0).Min();
            if (next == 0) { req.Status = ApprovalStatus.Approved; req.CompletedAt = DateTime.Now; }
            else req.CurrentLevel = next;
        }
        await _db.SaveChangesAsync();
        await _db.LogAsync(approve ? "Duyệt" : "Từ chối", req.DocType.ToString(), req.DocType.GetDisplayName(), req.DocId, req.DocNumber,
            comment == null ? null : System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?[]> { ["Ý kiến"] = new object?[] { null, comment } }));
        if (req.Status == ApprovalStatus.Pending) await NotifyApproversAsync(req);
        else if (req.RequestedById != null)
            await _notify.NotifyAsync(Array.Empty<string?>(), req.RequestedById, $"{req.DocType.GetDisplayName()} {req.DocNumber} {(req.Status == ApprovalStatus.Approved ? "đã được duyệt" : "bị từ chối")}", comment, null, req.Status == ApprovalStatus.Approved ? "success" : "danger", "Phê duyệt");
        return req.Status;
    }

    /// <summary>Các yêu cầu đang chờ người dùng duyệt</summary>
    public async Task<List<ApprovalRequest>> InboxAsync(ClaimsPrincipal u)
    {
        var pending = await _db.ApprovalRequests.Include(r => r.Steps).Include(r => r.RequestedBy).Where(r => r.Status == ApprovalStatus.Pending).OrderBy(r => r.RequestedAt).ToListAsync();
        return pending.Where(r => Current(r).Any(s => CanAct(s, u))).ToList();
    }
}
