using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>CRM mở rộng: chiến dịch marketing (chi phí, khách hàng tiềm năng, cơ hội thắng, ROI) và phiếu hỗ trợ / bảo hành có SLA</summary>
[Authorize(Roles = AppRoles.SalesTeam + "," + AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class ServiceController : ErpControllerBase
{
    private readonly ServiceDesk _desk;
    public ServiceController(ErpDbContext db, ServiceDesk desk) : base(db) => _desk = desk;
    private bool CanEdit => User.InAny(AppRoles.SalesTeam + "," + AppRoles.Admin);

    public record CampaignStat(Campaign Campaign, int Leads, int Converted, int Opps, int Won, decimal WonAmount, decimal Pipeline)
    {
        public decimal CostPerLead => Leads == 0 ? 0 : Math.Round(Campaign.ActualCost / Leads, 0);
        public decimal? Roi => Campaign.ActualCost == 0 ? null : Math.Round((WonAmount - Campaign.ActualCost) * 100 / Campaign.ActualCost, 1);
    }

    // ------------------------------------------------------------------ chiến dịch
    public async Task<IActionResult> Campaigns()
    {
        var camps = await Db.Campaigns.OrderByDescending(c => c.FromDate).ToListAsync();
        var leads = await Db.Leads.Where(l => l.CampaignId != null).Select(l => new { l.Id, l.CampaignId, l.Stage }).ToListAsync();
        var opps = await Db.Opportunities.Where(o => o.LeadId != null).Select(o => new { o.LeadId, o.Stage, o.Amount, o.Probability }).ToListAsync();
        var stats = camps.Select(c =>
        {
            var ls = leads.Where(l => l.CampaignId == c.Id).ToList(); var ids = ls.Select(l => l.Id).ToHashSet();
            var os = opps.Where(o => ids.Contains(o.LeadId!.Value)).ToList();
            return new CampaignStat(c, ls.Count, ls.Count(l => l.Stage == LeadStage.Converted), os.Count, os.Count(o => o.Stage == OppStage.Won), os.Where(o => o.Stage == OppStage.Won).Sum(o => o.Amount),
                os.Where(o => o.Stage != OppStage.Won && o.Stage != OppStage.Lost).Sum(o => o.Amount * o.Probability / 100m));
        }).ToList();
        ViewBag.CanEdit = CanEdit;
        return View(stats);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCampaign(Campaign model)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(model.Name)) throw new BusinessException("Nhập tên chiến dịch.");
        var c = model.Id == 0 ? new Campaign { Code = await DocNumbers.NextAsync(Db.Campaigns.Select(x => x.Code), "CD", model.FromDate) } : await Db.Campaigns.FindAsync(model.Id) ?? throw new BusinessException("Không tìm thấy.");
        if (c.Id == 0) Db.Campaigns.Add(c);
        c.Name = model.Name.Trim(); c.Channel = model.Channel; c.FromDate = model.FromDate; c.ToDate = model.ToDate; c.Budget = model.Budget; c.ActualCost = model.ActualCost; c.TargetLeads = model.TargetLeads; c.Note = model.Note;
        await Db.SaveChangesAsync();
        Success($"Đã lưu chiến dịch {c.Code}.");
        return RedirectToAction(nameof(Campaigns));
    }

    // ------------------------------------------------------------------ phiếu hỗ trợ / bảo hành
    public async Task<IActionResult> Tickets(TicketStatus? status, bool open = true, bool late = false, int? assigneeId = null)
    {
        var q = Db.ServiceTickets.Include(t => t.Partner).Include(t => t.Assignee).Include(t => t.Item).AsQueryable();
        if (status.HasValue) q = q.Where(t => t.Status == status); else if (open) q = q.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        if (assigneeId.HasValue) q = q.Where(t => t.AssigneeId == assigneeId);
        var list = await q.OrderBy(t => t.DueAt).Take(300).ToListAsync();
        if (late) list = list.Where(t => t.IsLate).ToList();
        var all = await Db.ServiceTickets.Where(t => t.CreatedAt >= DateTime.Today.AddDays(-90)).ToListAsync();
        ViewBag.Stats = (Open: all.Count(t => t.IsOpen), Late: all.Count(t => t.IsOpen && t.IsLate), Resolved: all.Count(t => !t.IsOpen),
            OnTimePct: all.Count(t => !t.IsOpen) == 0 ? 0m : Math.Round(all.Count(t => !t.IsOpen && !t.IsLate) * 100m / all.Count(t => !t.IsOpen), 1),
            AvgHours: all.Where(t => t.ResolvedAt != null).Select(t => (decimal)(t.ResolvedAt!.Value - t.CreatedAt).TotalHours).DefaultIfEmpty(0).Average());
        ViewBag.Status = status; ViewBag.Open = open; ViewBag.Late = late; ViewBag.CanEdit = CanEdit;
        await BagAsync();
        return View(list);
    }

    private async Task BagAsync()
    {
        ViewBag.Customers = await CustomersAsync();
        ViewBag.Items = new SelectList(await Db.Items.Where(i => i.IsActive && i.Type != ItemType.Service).OrderBy(i => i.Code).Select(i => new { i.Id, Name = i.Code + " – " + i.Name }).ToListAsync(), "Id", "Name");
        ViewBag.Users = new SelectList(await Db.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, Name = u.FullName }).ToListAsync(), "Id", "Name");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTicket(ServiceTicket model)
    {
        if (!CanEdit) return Denied();
        if (model.PartnerId == null) throw new BusinessException("Chọn khách hàng.");
        if (string.IsNullOrWhiteSpace(model.Subject)) throw new BusinessException("Nhập tiêu đề.");
        model.Id = 0; model.Status = TicketStatus.New; model.CreatedAt = DateTime.Now;
        model.Number = await DocNumbers.NextAsync(Db.ServiceTickets.Select(t => t.Number), "HT", DateTime.Today);
        model.DueAt = model.CreatedAt.AddHours(ServiceTicket.SlaHours(model.Priority));
        if (model.Category == TicketCategory.Warranty)
        {
            var w = await _desk.WarrantyAsync(model.PartnerId.Value, model.ItemId, model.SerialNo);
            model.InvoiceId = w.Invoice?.Id;
            model.Description = $"{model.Description}\n[Bảo hành] {w.Message}".Trim();
        }
        Db.ServiceTickets.Add(model);
        await Db.SaveChangesAsync();
        Success($"Đã tiếp nhận phiếu {model.Number} – hạn xử lý {model.DueAt:dd/MM HH:mm}.");
        return RedirectToAction(nameof(Ticket), new { id = model.Id });
    }

    public async Task<IActionResult> Ticket(int id)
    {
        var t = await Db.ServiceTickets.Include(x => x.Partner).Include(x => x.Assignee).Include(x => x.Item).Include(x => x.Invoice).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        ViewBag.Warranty = t.PartnerId != null && t.ItemId != null ? await _desk.WarrantyAsync(t.PartnerId.Value, t.ItemId, t.SerialNo) : null;
        ViewBag.History = await Db.ServiceTickets.Where(x => x.PartnerId == t.PartnerId && x.Id != id).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync();
        ViewBag.CanEdit = CanEdit;
        await BagAsync();
        return View(t);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTicket(int id, TicketStatus status, int? assigneeId, TicketPriority priority, string? resolution)
    {
        if (!CanEdit) return Denied();
        var t = await Db.ServiceTickets.FindAsync(id) ?? throw new BusinessException("Không tìm thấy phiếu.");
        if (status is TicketStatus.Resolved or TicketStatus.Closed && string.IsNullOrWhiteSpace(resolution ?? t.Resolution)) throw new BusinessException("Nhập kết quả xử lý trước khi đóng phiếu.");
        if (priority != t.Priority) t.DueAt = t.CreatedAt.AddHours(ServiceTicket.SlaHours(priority));
        t.Priority = priority; t.AssigneeId = assigneeId;
        if (t.FirstResponseAt == null && status != TicketStatus.New) t.FirstResponseAt = DateTime.Now;
        if (status is TicketStatus.Resolved or TicketStatus.Closed) t.ResolvedAt ??= DateTime.Now; else t.ResolvedAt = null;
        t.Status = status; if (!string.IsNullOrWhiteSpace(resolution)) t.Resolution = resolution;
        await Db.SaveChangesAsync();
        Success("Đã cập nhật phiếu " + t.Number + ".");
        return RedirectToAction(nameof(Ticket), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Warranty(int partnerId, int? itemId, string? serial) => Json(await _desk.WarrantyAsync(partnerId, itemId, serial) is var w ? new { w.InWarranty, w.Message, until = w.Until?.ToString("dd/MM/yyyy") } : null);
}
