using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Nhân sự chuyên sâu: tuyển dụng, đánh giá KPI, đào tạo, cấp phát tài sản cho nhân viên</summary>
[Authorize(Roles = AppRoles.FinanceView)]
public class TalentController : ErpControllerBase
{
    private readonly TalentService _svc;
    public TalentController(ErpDbContext db, TalentService svc) : base(db) => _svc = svc;
    private bool CanEdit => User.InAny(AppRoles.Accounting);
    private async Task<SelectList> EmpsAsync() => new(await Db.Employees.Where(e => e.IsActive).OrderBy(e => e.Code).Select(e => new { e.Id, Name = e.Code + " – " + e.FullName }).ToListAsync(), "Id", "Name");

    // ------------------------------------------------------------------ tuyển dụng
    public async Task<IActionResult> Jobs(int? id)
    {
        ViewBag.CanEdit = CanEdit;
        var jobs = await Db.JobOpenings.Include(j => j.Candidates).OrderBy(j => j.Status).ThenByDescending(j => j.CreatedAt).ToListAsync();
        ViewBag.Job = id == null ? jobs.FirstOrDefault(j => j.Status == JobStatus.Open) ?? jobs.FirstOrDefault() : jobs.FirstOrDefault(j => j.Id == id);
        return View(jobs);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveJob(JobOpening model)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(model.Title)) throw new BusinessException("Nhập vị trí tuyển dụng.");
        var j = model.Id == 0 ? new JobOpening { Code = await DocNumbers.NextAsync(Db.JobOpenings.Select(x => x.Code), "TD", DateTime.Today) } : await Db.JobOpenings.FindAsync(model.Id) ?? throw new BusinessException("Không tìm thấy.");
        if (j.Id == 0) Db.JobOpenings.Add(j);
        j.Title = model.Title.Trim(); j.Department = model.Department; j.Quantity = Math.Max(1, model.Quantity); j.SalaryFrom = model.SalaryFrom; j.SalaryTo = model.SalaryTo;
        j.Deadline = model.Deadline; j.Description = model.Description; j.Status = model.Status == 0 ? JobStatus.Open : model.Status;
        await Db.SaveChangesAsync();
        Success($"Đã lưu vị trí {j.Code}.");
        return RedirectToAction(nameof(Jobs), new { id = j.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCandidate(Candidate model)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(model.FullName)) throw new BusinessException("Nhập họ tên ứng viên.");
        model.Id = 0; model.Stage = CandidateStage.Applied; model.CreatedAt = DateTime.Now;
        Db.Candidates.Add(model);
        await Db.SaveChangesAsync();
        Success("Đã thêm ứng viên.");
        return RedirectToAction(nameof(Jobs), new { id = model.JobOpeningId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveCandidate(int id, CandidateStage stage, int? score, DateTime? interviewAt, string? note)
    {
        if (!CanEdit) return Denied();
        var c = await Db.Candidates.FindAsync(id) ?? throw new BusinessException("Không tìm thấy ứng viên.");
        if (c.Stage == CandidateStage.Hired) throw new BusinessException("Ứng viên đã được tuyển.");
        if (stage == CandidateStage.Hired) throw new BusinessException("Dùng nút \"Nhận việc\" để tạo hồ sơ nhân viên.");
        c.Stage = stage; if (score != null) c.Score = Math.Clamp(score.Value, 0, 100); if (interviewAt != null) c.InterviewAt = interviewAt; if (!string.IsNullOrWhiteSpace(note)) c.Note = note;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Jobs), new { id = c.JobOpeningId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Hire(int id, decimal baseSalary, DateTime startDate)
    {
        if (!CanEdit) return Denied();
        var c = await Db.Candidates.Include(x => x.JobOpening).FirstOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Không tìm thấy ứng viên.");
        if (baseSalary <= 0) throw new BusinessException("Nhập mức lương cơ bản.");
        var e = await _svc.HireAsync(c, baseSalary, startDate);
        Success($"Đã tạo hồ sơ nhân viên {e.Code} – {e.FullName}. Lập hợp đồng lao động và bổ sung thông tin cá nhân.");
        return RedirectToAction("Profile", "Hr", new { id = e.Id });
    }

    // ------------------------------------------------------------------ đánh giá KPI
    public async Task<IActionResult> Appraisals(int? cycleId)
    {
        var cycles = await Db.AppraisalCycles.Include(c => c.Criteria).OrderByDescending(c => c.FromDate).ToListAsync();
        var cycle = cycleId == null ? cycles.FirstOrDefault() : cycles.FirstOrDefault(c => c.Id == cycleId);
        ViewBag.Cycles = cycles; ViewBag.Cycle = cycle; ViewBag.CanEdit = CanEdit;
        var list = cycle == null ? new List<Appraisal>() : await Db.Appraisals.Include(a => a.Employee).Where(a => a.AppraisalCycleId == cycle.Id).OrderBy(a => a.Employee!.Code).ToListAsync();
        return View(list);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCycle(string name, DateTime fromDate, DateTime toDate, string? bonusPct, string? criteria)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("Nhập tên kỳ đánh giá.");
        var c = new AppraisalCycle { Name = name.Trim(), FromDate = fromDate, ToDate = toDate, BonusPct = string.IsNullOrWhiteSpace(bonusPct) ? "A:100;B:50;C:0;D:0" : bonusPct.Trim() };
        // mỗi dòng: Tên tiêu chí | trọng số | chỉ tiêu
        foreach (var line in (criteria ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|', StringSplitOptions.TrimEntries);
            c.Criteria.Add(new AppraisalCriterion { Name = p[0], Weight = p.Length > 1 && int.TryParse(p[1], out var w) ? Math.Clamp(w, 1, 100) : 20, Target = p.Length > 2 ? p[2] : null });
        }
        if (c.Criteria.Count == 0) throw new BusinessException("Nhập ít nhất một tiêu chí (mỗi dòng: Tên | trọng số | chỉ tiêu).");
        Db.AppraisalCycles.Add(c);
        await Db.SaveChangesAsync();
        foreach (var e in await Db.Employees.Where(e => e.IsActive).ToListAsync()) Db.Appraisals.Add(new Appraisal { AppraisalCycleId = c.Id, EmployeeId = e.Id });
        await Db.SaveChangesAsync();
        Success($"Đã tạo kỳ đánh giá \"{c.Name}\" cho {await Db.Appraisals.CountAsync(a => a.AppraisalCycleId == c.Id)} nhân viên.");
        return RedirectToAction(nameof(Appraisals), new { cycleId = c.Id });
    }

    public async Task<IActionResult> Appraisal(int id)
    {
        var a = await Db.Appraisals.Include(x => x.Employee).Include(x => x.Scores).Include(x => x.Cycle).ThenInclude(c => c!.Criteria).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        ViewBag.CanEdit = CanEdit && a.Status != AppraisalStatus.Approved && a.Cycle?.IsClosed != true;
        return View(a);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ScoreAppraisal(int id, [FromForm(Name = "score")] Dictionary<string, string>? scoreForm, [FromForm(Name = "comment")] Dictionary<string, string>? commentForm, string? managerComment, string? selfReview, string? op)
    {
        if (!CanEdit) return Denied();
        var a = await Db.Appraisals.Include(x => x.Scores).Include(x => x.Cycle).ThenInclude(c => c!.Criteria).FirstOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Không tìm thấy.");
        if (a.Status == AppraisalStatus.Approved) throw new BusinessException("Phiếu đánh giá đã chốt.");
        var score = (scoreForm ?? new()).Where(x => int.TryParse(x.Key, out _)).ToDictionary(x => int.Parse(x.Key), x => int.TryParse(x.Value, out var v) ? v : 0);
        var comment = (commentForm ?? new()).Where(x => int.TryParse(x.Key, out _)).ToDictionary(x => int.Parse(x.Key), x => (string?)x.Value);
        foreach (var cr in a.Cycle!.Criteria)
        {
            var s = a.Scores.FirstOrDefault(x => x.AppraisalCriterionId == cr.Id);
            if (s == null) a.Scores.Add(s = new AppraisalScore { AppraisalCriterionId = cr.Id });
            s.Score = Math.Clamp(score.GetValueOrDefault(cr.Id), 0, 100); s.Comment = comment.GetValueOrDefault(cr.Id);
        }
        a.ManagerComment = managerComment; a.SelfReview = selfReview; a.Reviewer = User.GetFullName();
        TalentService.Recalc(a, a.Cycle.Criteria);
        if (op == "approve") a.Status = AppraisalStatus.Approved; else if (op == "submit") a.Status = AppraisalStatus.Submitted;
        await Db.SaveChangesAsync();
        Success($"Đã lưu: {a.Total:0.#} điểm – xếp loại {a.Rating}.");
        return RedirectToAction(nameof(Appraisals), new { cycleId = a.AppraisalCycleId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PushBonus(int cycleId, int year, int month)
    {
        if (!CanEdit) return Denied();
        var c = await Db.AppraisalCycles.FindAsync(cycleId) ?? throw new BusinessException("Không tìm thấy kỳ.");
        var n = await _svc.PushBonusesAsync(c, year, month);
        c.IsClosed = true; await Db.SaveChangesAsync();
        Success($"Đã chuyển {n} khoản thưởng hiệu suất vào lương tháng {month:00}/{year} và khóa kỳ đánh giá.");
        return RedirectToAction(nameof(Appraisals), new { cycleId });
    }

    // ------------------------------------------------------------------ đào tạo
    public async Task<IActionResult> Training(int? id)
    {
        var courses = await Db.TrainingCourses.Include(c => c.Attendees).ThenInclude(a => a.Employee).OrderByDescending(c => c.FromDate).ToListAsync();
        ViewBag.Course = id == null ? courses.FirstOrDefault() : courses.FirstOrDefault(c => c.Id == id);
        ViewBag.Emps = await EmpsAsync(); ViewBag.CanEdit = CanEdit;
        // chứng chỉ bắt buộc sắp / đã hết hạn
        ViewBag.Expiring = courses.Where(c => c.Mandatory && c.ValidMonths > 0).SelectMany(c => c.Attendees).Where(a => a.ExpiresOn != null && a.ExpiresOn <= DateTime.Today.AddDays(60)).OrderBy(a => a.ExpiresOn).ToList();
        return View(courses);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCourse(TrainingCourse model, List<int>? employeeIds)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(model.Title)) throw new BusinessException("Nhập tên khóa đào tạo.");
        if (model.ToDate < model.FromDate) throw new BusinessException("Ngày kết thúc trước ngày bắt đầu.");
        model.Id = 0; model.Code = await DocNumbers.NextAsync(Db.TrainingCourses.Select(x => x.Code), "DT", model.FromDate);
        foreach (var e in (employeeIds ?? new()).Distinct()) model.Attendees.Add(new TrainingAttendee { EmployeeId = e });
        Db.TrainingCourses.Add(model);
        await Db.SaveChangesAsync();
        Success($"Đã tạo khóa {model.Code} với {model.Attendees.Count} học viên.");
        return RedirectToAction(nameof(Training), new { id = model.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAttendee(int id, int employeeId)
    {
        if (!CanEdit) return Denied();
        if (!await Db.TrainingAttendees.AnyAsync(a => a.TrainingCourseId == id && a.EmployeeId == employeeId)) { Db.TrainingAttendees.Add(new TrainingAttendee { TrainingCourseId = id, EmployeeId = employeeId }); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Training), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Result(int id, TrainingStatus status, int? score, string? cert)
    {
        if (!CanEdit) return Denied();
        var a = await Db.TrainingAttendees.Include(x => x.Course).FirstOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Không tìm thấy.");
        await _svc.CompleteTrainingAsync(a, a.Course!, status, score, string.IsNullOrWhiteSpace(cert) ? null : cert.Trim());
        return RedirectToAction(nameof(Training), new { id = a.TrainingCourseId });
    }

    // ------------------------------------------------------------------ cấp phát tài sản
    public async Task<IActionResult> Assets(bool open = true)
    {
        ViewBag.Emps = await EmpsAsync(); ViewBag.CanEdit = CanEdit; ViewBag.Open = open;
        ViewBag.Tools = new SelectList(await Db.ToolItems.OrderBy(t => t.Code).Select(t => new { t.Id, Name = t.Code + " – " + t.Name }).ToListAsync(), "Id", "Name");
        ViewBag.Fixed = new SelectList(await Db.FixedAssets.OrderBy(t => t.Code).Select(t => new { t.Id, Name = t.Code + " – " + t.Name }).ToListAsync(), "Id", "Name");
        var q = Db.EmployeeAssetIssues.Include(a => a.Employee).Include(a => a.ToolItem).Include(a => a.FixedAsset).AsQueryable();
        if (open) q = q.Where(a => a.ReturnedDate == null);
        return View(await q.OrderByDescending(a => a.IssuedDate).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(EmployeeAssetIssue model)
    {
        if (!CanEdit) return Denied();
        if (string.IsNullOrWhiteSpace(model.Description))
            model.Description = model.ToolItemId != null ? (await Db.ToolItems.FindAsync(model.ToolItemId))?.Name ?? "" : model.FixedAssetId != null ? (await Db.FixedAssets.FindAsync(model.FixedAssetId))?.Name ?? "" : "";
        if (string.IsNullOrWhiteSpace(model.Description)) throw new BusinessException("Chọn công cụ / tài sản hoặc nhập tên.");
        if (model.FixedAssetId != null && await Db.EmployeeAssetIssues.AnyAsync(a => a.FixedAssetId == model.FixedAssetId && a.ReturnedDate == null)) throw new BusinessException("Tài sản đang giao cho người khác – thu hồi trước.");
        model.Id = 0; model.ReturnedDate = null;
        Db.EmployeeAssetIssues.Add(model);
        await Db.SaveChangesAsync();
        Success("Đã ghi nhận bàn giao.");
        return RedirectToAction(nameof(Assets));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Return(int id, string? condition)
    {
        if (!CanEdit) return Denied();
        var a = await Db.EmployeeAssetIssues.FindAsync(id) ?? throw new BusinessException("Không tìm thấy.");
        a.ReturnedDate = DateTime.Today; a.ReturnCondition = condition;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Assets));
    }

    /// <summary>Biên bản bàn giao tài sản cho nhân viên (bản in)</summary>
    public async Task<IActionResult> Handover(int employeeId)
    {
        var e = await Db.Employees.FindAsync(employeeId);
        if (e == null) return NotFound();
        ViewBag.Employee = e;
        return View(await Db.EmployeeAssetIssues.Include(a => a.ToolItem).Include(a => a.FixedAsset).Where(a => a.EmployeeId == employeeId && a.ReturnedDate == null).OrderBy(a => a.IssuedDate).ToListAsync());
    }
}
