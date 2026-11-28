using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>CRM: khách hàng tiềm năng, cơ hội bán hàng (pipeline), lịch sử tương tác, hồ sơ 360° của khách hàng</summary>
[Authorize(Roles = AppRoles.SalesTeam + "," + AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class CrmController : ErpControllerBase
{
    private readonly CodeService _codes;
    public CrmController(ErpDbContext db, CodeService codes) : base(db) => _codes = codes;
    private bool CanEdit => User.InAny(AppRoles.SalesTeam + "," + AppRoles.Admin);
    /// <summary>Nhân viên kinh doanh chỉ thấy khách hàng tiềm năng / cơ hội của mình; quản lý xem tất cả</summary>
    private bool SeesAll => User.InAny(AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Accountant);

    private async Task<SelectList> OwnersAsync(int? sel = null) =>
        new(await Db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Admin || u.Role == AppRoles.Director)).OrderBy(u => u.FullName).Select(u => new { u.Id, Name = u.FullName }).ToListAsync(), "Id", "Name", sel);

    // ------------------------------------------------------------------ tổng quan
    public async Task<IActionResult> Index()
    {
        var me = Me;
        var leads = await Db.Leads.Where(l => SeesAll || l.OwnerId == me || l.OwnerId == null).ToListAsync();
        var opps = await Db.Opportunities.Include(o => o.Partner).Include(o => o.Lead).Where(o => SeesAll || o.OwnerId == me || o.OwnerId == null).ToListAsync();
        var today = DateTime.Today;
        ViewBag.Leads = leads; ViewBag.Opps = opps;
        ViewBag.Tasks = await Db.CrmActivities.Include(a => a.Lead).Include(a => a.Partner).Include(a => a.Opportunity).Where(a => a.Kind == ActivityKind.Task && !a.Done && (SeesAll || a.OwnerId == me)).OrderBy(a => a.Date).Take(12).ToListAsync();
        ViewBag.Recent = await Db.CrmActivities.Include(a => a.Lead).Include(a => a.Partner).Include(a => a.Owner).Where(a => a.Done).OrderByDescending(a => a.Date).Take(10).ToListAsync();
        ViewBag.WonMonth = opps.Where(o => o.Stage == OppStage.Won && o.ClosedAt >= new DateTime(today.Year, today.Month, 1)).Sum(o => o.Amount);
        return View();
    }

    // ------------------------------------------------------------------ khách hàng tiềm năng
    public async Task<IActionResult> Leads(LeadStage? stage, LeadSource? source, int? ownerId, string? q)
    {
        var me = Me;
        var query = Db.Leads.Include(l => l.Owner).Include(l => l.Partner).Where(l => SeesAll || l.OwnerId == me || l.OwnerId == null).AsQueryable();
        if (stage.HasValue) query = query.Where(l => l.Stage == stage);
        if (source.HasValue) query = query.Where(l => l.Source == source);
        if (ownerId.HasValue) query = query.Where(l => l.OwnerId == ownerId);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(l => l.Name.Contains(q) || (l.ContactName ?? "").Contains(q) || (l.Phone ?? "").Contains(q) || (l.Email ?? "").Contains(q) || l.Code.Contains(q));
        ViewBag.Stage = stage; ViewBag.Source = source; ViewBag.OwnerId = ownerId; ViewBag.Q = q; ViewBag.CanEdit = CanEdit;
        ViewBag.Owners = await OwnersAsync(ownerId);
        ViewBag.Counts = (await Db.Leads.Select(l => l.Stage).ToListAsync()).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return View(await query.OrderByDescending(l => l.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> LeadDetails(int id)
    {
        var l = await Db.Leads.Include(x => x.Owner).Include(x => x.Partner).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        ViewBag.Acts = await Db.CrmActivities.Include(a => a.Owner).Where(a => a.LeadId == id).OrderByDescending(a => a.Date).ToListAsync();
        ViewBag.Opps = await Db.Opportunities.Where(o => o.LeadId == id).OrderByDescending(o => o.CreatedAt).ToListAsync();
        ViewBag.CanEdit = CanEdit;
        return View(l);
    }

    public async Task<IActionResult> LeadForm(int? id)
    {
        if (!CanEdit) return Denied();
        var l = id == null ? new Lead { OwnerId = Me } : await Db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        ViewBag.Owners = await OwnersAsync(l.OwnerId);
        return View(l);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveLead(Lead model)
    {
        if (!CanEdit) return Denied();
        ModelState.Remove(nameof(model.Code));
        if (!ModelState.IsValid) { ViewBag.Owners = await OwnersAsync(model.OwnerId); return View("LeadForm", model); }
        var l = model.Id == 0 ? new Lead() : await Db.Leads.FindAsync(model.Id) ?? new Lead();
        if (l.Id == 0) { Db.Leads.Add(l); l.Code = await DocNumbers.NextAsync(Db.Leads.Select(x => x.Code), "KHTN", DateTime.Today); }
        l.Name = model.Name.Trim(); l.ContactName = model.ContactName; l.ContactTitle = model.ContactTitle; l.Phone = model.Phone; l.Email = model.Email; l.Address = model.Address;
        l.Source = model.Source; l.OwnerId = model.OwnerId; l.EstimatedValue = model.EstimatedValue; l.Note = model.Note;
        if (l.Id != 0 && l.Stage != LeadStage.Converted) l.Stage = model.Stage;
        await Db.SaveChangesAsync();
        Success($"Đã lưu khách hàng tiềm năng {l.Code}.");
        return RedirectToAction(nameof(LeadDetails), new { id = l.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLeadStage(int id, LeadStage stage, string? reason)
    {
        if (!CanEdit) return Denied();
        var l = await Db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (l.Stage == LeadStage.Converted) throw new BusinessException("Khách hàng tiềm năng đã chuyển đổi.");
        if (stage == LeadStage.Converted) throw new BusinessException("Dùng chức năng \"Chuyển thành khách hàng\".");
        if (stage == LeadStage.Lost && string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do không phù hợp.");
        l.Stage = stage; l.LostReason = stage == LeadStage.Lost ? reason : null;
        Db.CrmActivities.Add(new CrmActivity { Kind = ActivityKind.Note, Subject = $"Chuyển giai đoạn: {stage.GetDisplayName()}", Outcome = reason, LeadId = id, OwnerId = Me, Date = DateTime.Now });
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(LeadDetails), new { id });
    }

    /// <summary>Chuyển khách hàng tiềm năng thành khách hàng (đối tượng công nợ) và tạo cơ hội bán hàng</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConvertLead(int id, string? oppTitle, decimal? amount, int? existingPartnerId)
    {
        if (!CanEdit) return Denied();
        var l = await Db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (l.Stage == LeadStage.Converted) throw new BusinessException("Đã chuyển đổi trước đó.");
        Partner p;
        if (existingPartnerId != null) p = await Db.Partners.FindAsync(existingPartnerId) ?? throw new BusinessException("Không tìm thấy khách hàng.");
        else
        {
            if (await Db.Partners.AnyAsync(x => x.Name == l.Name && x.IsCustomer)) throw new BusinessException("Đã có khách hàng trùng tên – chọn khách hàng hiện có để gắn.");
            p = new Partner { Code = await _codes.NextAsync("customer") ?? $"KH{DateTime.Today:yyMM}{l.Id:000}", Name = l.Name, Address = l.Address, Phone = l.Phone, Email = l.Email, ContactPerson = l.ContactName, IsCustomer = true, PaymentTermDays = 30 };
            Db.Partners.Add(p);
            await Db.SaveChangesAsync();
        }
        l.PartnerId = p.Id; l.Stage = LeadStage.Converted;
        var o = new Opportunity { Title = string.IsNullOrWhiteSpace(oppTitle) ? $"Cơ hội bán hàng – {l.Name}" : oppTitle.Trim(), LeadId = l.Id, PartnerId = p.Id, Amount = amount ?? l.EstimatedValue, OwnerId = l.OwnerId ?? Me, Probability = 30, Stage = OppStage.Qualified,
            Code = await DocNumbers.NextAsync(Db.Opportunities.Select(x => x.Code), "CH", DateTime.Today), Note = l.Note };
        Db.Opportunities.Add(o);
        Db.CrmActivities.Add(new CrmActivity { Kind = ActivityKind.Note, Subject = $"Chuyển thành khách hàng {p.Code} và tạo cơ hội {o.Code}", LeadId = l.Id, PartnerId = p.Id, OwnerId = Me, Date = DateTime.Now });
        await Db.SaveChangesAsync();
        Success($"Đã chuyển thành khách hàng {p.Code} – {p.Name} và tạo cơ hội {o.Code}.");
        return RedirectToAction(nameof(OppDetails), new { id = o.Id });
    }

    // ------------------------------------------------------------------ cơ hội bán hàng
    public async Task<IActionResult> Pipeline(int? ownerId)
    {
        var me = Me;
        var q = Db.Opportunities.Include(o => o.Partner).Include(o => o.Lead).Include(o => o.Owner).Where(o => SeesAll || o.OwnerId == me || o.OwnerId == null).AsQueryable();
        if (ownerId.HasValue) q = q.Where(o => o.OwnerId == ownerId);
        ViewBag.OwnerId = ownerId; ViewBag.Owners = await OwnersAsync(ownerId); ViewBag.CanEdit = CanEdit;
        var from = DateTime.Today.AddMonths(-3);
        return View(await q.Where(o => o.Stage != OppStage.Won && o.Stage != OppStage.Lost || o.ClosedAt >= from).OrderBy(o => o.ExpectedClose).ToListAsync());
    }

    public async Task<IActionResult> OppDetails(int id)
    {
        var o = await Db.Opportunities.Include(x => x.Partner).Include(x => x.Lead).Include(x => x.Owner).Include(x => x.Quote).Include(x => x.SalesOrder).FirstOrDefaultAsync(x => x.Id == id);
        if (o == null) return NotFound();
        ViewBag.Acts = await Db.CrmActivities.Include(a => a.Owner).Where(a => a.OpportunityId == id).OrderByDescending(a => a.Date).ToListAsync();
        ViewBag.Quotes = o.PartnerId == null ? new List<SalesQuote>() : await Db.SalesQuotes.Where(q => q.CustomerId == o.PartnerId).OrderByDescending(q => q.Date).Take(10).ToListAsync();
        ViewBag.CanEdit = CanEdit;
        return View(o);
    }

    public async Task<IActionResult> OppForm(int? id, int? partnerId, int? leadId)
    {
        if (!CanEdit) return Denied();
        var o = id == null ? new Opportunity { OwnerId = Me, PartnerId = partnerId, LeadId = leadId } : await Db.Opportunities.FindAsync(id);
        if (o == null) return NotFound();
        await OppBagAsync(o);
        return View(o);
    }

    private async Task OppBagAsync(Opportunity o)
    {
        ViewBag.Owners = await OwnersAsync(o.OwnerId);
        ViewBag.Customers = await CustomersAsync(o.PartnerId);
        ViewBag.LeadList = new SelectList(await Db.Leads.Where(l => l.Stage != LeadStage.Lost || l.Id == o.LeadId).OrderBy(l => l.Name).Select(l => new { l.Id, Name = l.Code + " – " + l.Name }).ToListAsync(), "Id", "Name", o.LeadId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOpp(Opportunity model)
    {
        if (!CanEdit) return Denied();
        ModelState.Remove(nameof(model.Code));
        if (model.PartnerId == null && model.LeadId == null) ModelState.AddModelError("", "Chọn khách hàng hoặc khách hàng tiềm năng.");
        if (!ModelState.IsValid) { await OppBagAsync(model); return View("OppForm", model); }
        var o = model.Id == 0 ? new Opportunity { Code = await DocNumbers.NextAsync(Db.Opportunities.Select(x => x.Code), "CH", DateTime.Today) } : await Db.Opportunities.FindAsync(model.Id) ?? new Opportunity();
        if (o.Id == 0) Db.Opportunities.Add(o);
        var stageChanged = o.Id != 0 && o.Stage != model.Stage;
        o.Title = model.Title.Trim(); o.LeadId = model.LeadId; o.PartnerId = model.PartnerId; o.Amount = model.Amount; o.ExpectedClose = model.ExpectedClose.Date; o.OwnerId = model.OwnerId; o.Note = model.Note;
        if (o.Id == 0 || stageChanged) { o.Stage = model.Stage; o.Probability = model.Probability == o.Probability || model.Probability == 0 ? Opportunity.DefaultProbability(model.Stage) : model.Probability; } else o.Probability = model.Probability;
        if (o.Stage is OppStage.Won or OppStage.Lost) o.ClosedAt ??= DateTime.Now;
        await Db.SaveChangesAsync();
        Success($"Đã lưu cơ hội {o.Code}.");
        return RedirectToAction(nameof(OppDetails), new { id = o.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveOpp(int id, OppStage stage, string? reason, bool fromBoard = false)
    {
        if (!CanEdit) return Denied();
        var o = await Db.Opportunities.FindAsync(id);
        if (o == null) return NotFound();
        if (!o.IsOpen) throw new BusinessException("Cơ hội đã đóng.");
        if (stage == OppStage.Lost && string.IsNullOrWhiteSpace(reason)) throw new BusinessException("Nhập lý do thua.");
        o.Stage = stage; o.Probability = Opportunity.DefaultProbability(stage);
        if (stage is OppStage.Won or OppStage.Lost) { o.ClosedAt = DateTime.Now; o.LostReason = stage == OppStage.Lost ? reason : null; }
        Db.CrmActivities.Add(new CrmActivity { Kind = ActivityKind.Note, Subject = $"Chuyển giai đoạn: {stage.GetDisplayName()}", Outcome = reason, OpportunityId = id, PartnerId = o.PartnerId, LeadId = o.LeadId, OwnerId = Me, Date = DateTime.Now });
        await Db.SaveChangesAsync();
        if (stage == OppStage.Won) Success($"Cơ hội {o.Code} đã thắng. Lập đơn bán hàng cho khách để thực hiện.");
        return fromBoard ? RedirectToAction(nameof(Pipeline)) : RedirectToAction(nameof(OppDetails), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkQuote(int id, int quoteId)
    {
        if (!CanEdit) return Denied();
        var o = await Db.Opportunities.FindAsync(id);
        var q = await Db.SalesQuotes.FindAsync(quoteId);
        if (o == null || q == null) return NotFound();
        o.QuoteId = quoteId; o.Amount = q.Total > 0 ? q.Subtotal : o.Amount;
        if (o.Stage is OppStage.Prospecting or OppStage.Qualified) { o.Stage = OppStage.Proposal; o.Probability = Opportunity.DefaultProbability(OppStage.Proposal); }
        Db.CrmActivities.Add(new CrmActivity { Kind = ActivityKind.Offer, Subject = $"Gửi báo giá {q.Number}", Outcome = $"Giá trị {q.Total.ToMoney()} đ", OpportunityId = id, PartnerId = o.PartnerId, OwnerId = Me, Date = DateTime.Now });
        await Db.SaveChangesAsync();
        Success($"Đã gắn báo giá {q.Number} vào cơ hội.");
        return RedirectToAction(nameof(OppDetails), new { id });
    }

    // ------------------------------------------------------------------ tương tác
    public async Task<IActionResult> Activities(ActivityKind? kind, bool openTasks = false, int? ownerId = null)
    {
        var q = Db.CrmActivities.Include(a => a.Lead).Include(a => a.Partner).Include(a => a.Opportunity).Include(a => a.Owner).AsQueryable();
        if (!SeesAll) { var me = Me; q = q.Where(a => a.OwnerId == me); }
        if (kind.HasValue) q = q.Where(a => a.Kind == kind);
        if (openTasks) q = q.Where(a => a.Kind == ActivityKind.Task && !a.Done);
        if (ownerId.HasValue) q = q.Where(a => a.OwnerId == ownerId);
        ViewBag.Kind = kind; ViewBag.OpenTasks = openTasks; ViewBag.CanEdit = CanEdit;
        ViewBag.Owners = await OwnersAsync(ownerId);
        ViewBag.Customers = await CustomersAsync();
        ViewBag.LeadList = new SelectList(await Db.Leads.Where(l => l.Stage != LeadStage.Lost).OrderBy(l => l.Name).Select(l => new { l.Id, Name = l.Code + " – " + l.Name }).ToListAsync(), "Id", "Name");
        return View(await q.OrderByDescending(a => a.Done ? a.Date : DateTime.MaxValue).ThenByDescending(a => a.Date).Take(300).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddActivity(CrmActivity model, string? returnUrl)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(model.Subject)) throw new BusinessException("Nhập nội dung tương tác.");
        if (model.LeadId == null && model.PartnerId == null && model.OpportunityId == null) throw new BusinessException("Chọn khách hàng, khách hàng tiềm năng hoặc cơ hội.");
        model.Id = 0; model.OwnerId ??= Me; model.CreatedAt = DateTime.Now;
        if (model.OpportunityId != null) { var o = await Db.Opportunities.FindAsync(model.OpportunityId); if (o != null) { model.PartnerId ??= o.PartnerId; model.LeadId ??= o.LeadId; } }
        if (model.Kind != ActivityKind.Task) model.Done = true;
        Db.CrmActivities.Add(model);
        if (model.LeadId != null && model.Done) { var l = await Db.Leads.FindAsync(model.LeadId); if (l != null) { l.LastContactAt = model.Date; if (l.Stage == LeadStage.New) l.Stage = LeadStage.Contacted; } }
        await Db.SaveChangesAsync();
        Success("Đã ghi nhận tương tác.");
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Activities));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteTask(int id, string? outcome, string? returnUrl)
    {
        if (!CanEdit) return Denied();
        var a = await Db.CrmActivities.FindAsync(id);
        if (a == null) return NotFound();
        a.Done = true; a.Outcome = outcome;
        await Db.SaveChangesAsync();
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Activities));
    }

    // ------------------------------------------------------------------ khách hàng 360°
    public async Task<IActionResult> Customer(int id)
    {
        var p = await Db.Partners.FirstOrDefaultAsync(x => x.Id == id && x.IsCustomer);
        if (p == null) return NotFound();
        ViewBag.Acts = await Db.CrmActivities.Include(a => a.Owner).Where(a => a.PartnerId == id).OrderByDescending(a => a.Date).Take(60).ToListAsync();
        ViewBag.Opps = await Db.Opportunities.Where(o => o.PartnerId == id).OrderByDescending(o => o.CreatedAt).ToListAsync();
        ViewBag.Quotes = await Db.SalesQuotes.Where(q => q.CustomerId == id).OrderByDescending(q => q.Date).Take(10).ToListAsync();
        ViewBag.Orders = await Db.SalesOrders.Where(s => s.CustomerId == id).OrderByDescending(s => s.Date).Take(10).ToListAsync();
        var invs = await Db.Invoices.Where(i => i.PartnerId == id && i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued).ToListAsync();
        ViewBag.Revenue12 = invs.Where(i => i.Date >= DateTime.Today.AddMonths(-12)).Sum(i => i.Subtotal);
        ViewBag.Outstanding = invs.Sum(i => i.Outstanding); ViewBag.Overdue = invs.Where(i => i.IsOverdue).Sum(i => i.Outstanding);
        ViewBag.LastInvoice = invs.OrderByDescending(i => i.Date).FirstOrDefault()?.Date;
        ViewBag.CanEdit = CanEdit;
        return View(p);
    }
}
