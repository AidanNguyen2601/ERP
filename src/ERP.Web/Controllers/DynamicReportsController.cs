using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

public class DesignerVm
{
    public ReportDef Def { get; set; } = new();
    public ReportResult? Result { get; set; }
    public SavedReport? Saved { get; set; }
    public ReportTemplate? Template { get; set; }
    public string Title { get; set; } = "Báo cáo động mới";
    public List<Dataset> Datasets { get; set; } = new();
    public bool CanEdit { get; set; } = true;
    public string? Error { get; set; }
    public bool OpenDesigner { get; set; }
    public string DatasetsJson { get; set; } = "[]";
}

/// <summary>Báo cáo động: người dùng tự chọn nguồn dữ liệu, trường, nhóm, chỉ số, bộ lọc; lưu / chia sẻ / xuất Excel</summary>
public class DynamicReportsController : ErpControllerBase
{
    public DynamicReportsController(ErpDbContext db) : base(db) { }

    private List<Dataset> Allowed() => DatasetRegistry.All.Where(d => DatasetRegistry.Allowed(d, User)).ToList();

    public async Task<IActionResult> Index()
    {
        var allowed = Allowed().Select(d => d.Key).ToHashSet();
        ViewBag.Templates = ReportTemplates.All.Where(t => allowed.Contains(t.Def.Dataset)).ToList();
        var me = Me;
        var all = await Db.SavedReports.Include(r => r.Owner).Where(r => r.OwnerId == me || r.IsShared).OrderBy(r => r.Name).ToListAsync();
        ViewBag.Mine = all.Where(r => r.OwnerId == me).ToList();
        ViewBag.Shared = all.Where(r => r.OwnerId != me && allowed.Contains(r.DatasetKey)).ToList();
        ViewBag.Datasets = Allowed();
        ViewBag.Me = me; ViewBag.IsAdmin = User.IsAdmin();
        return View();
    }

    /// <summary>Mở trình thiết kế: báo cáo đã lưu (id), mẫu hệ thống (slug) hoặc báo cáo trống theo nguồn dữ liệu (dataset)</summary>
    public async Task<IActionResult> Designer(int? id, string? slug, string? dataset)
    {
        var vm = NewVm();
        if (id.HasValue)
        {
            var s = await Db.SavedReports.FirstOrDefaultAsync(r => r.Id == id && (r.OwnerId == Me || r.IsShared));
            if (s == null) return NotFound();
            var ds = DatasetRegistry.Find(s.DatasetKey);
            if (ds == null || !DatasetRegistry.Allowed(ds, User)) return Denied();
            vm.Saved = s; vm.Title = s.Name; vm.CanEdit = s.OwnerId == Me || User.IsAdmin();
            vm.Def = ReportDef.FromJson(s.DefinitionJson) ?? NewDef(ds);
            return await RunAsync(vm, ds);
        }
        if (!string.IsNullOrEmpty(slug))
        {
            var t = ReportTemplates.Find(slug);
            if (t == null) return NotFound();
            var ds = DatasetRegistry.Find(t.Def.Dataset)!;
            if (!DatasetRegistry.Allowed(ds, User)) return Denied();
            vm.Template = t; vm.Title = t.Title; vm.Def = t.Def.Clone();
            return await RunAsync(vm, ds);
        }
        if (!string.IsNullOrEmpty(dataset) && DatasetRegistry.Find(dataset) is { } req && !DatasetRegistry.Allowed(req, User)) return Denied();
        var first = DatasetRegistry.Find(dataset) is { } d && DatasetRegistry.Allowed(d, User) ? d : vm.Datasets.FirstOrDefault();
        if (first == null) return Denied();
        vm.Def = NewDef(first); vm.OpenDesigner = true;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(string defJson, int? id, string? slug, string? title)
    {
        var vm = NewVm();
        var def = ReportDef.FromJson(defJson);
        var ds = def == null ? null : DatasetRegistry.Find(def.Dataset);
        if (def == null || ds == null || !DatasetRegistry.Allowed(ds, User)) { vm.Error = "Định nghĩa báo cáo không hợp lệ hoặc bạn không có quyền với nguồn dữ liệu này."; vm.Def = NewDef(vm.Datasets.First()); vm.OpenDesigner = true; return View("Designer", vm); }
        vm.Def = def; vm.Title = string.IsNullOrWhiteSpace(title) ? "Báo cáo động" : title!;
        if (id.HasValue) { vm.Saved = await Db.SavedReports.FirstOrDefaultAsync(r => r.Id == id && (r.OwnerId == Me || r.IsShared)); if (vm.Saved != null) { vm.Title = vm.Saved.Name; vm.CanEdit = vm.Saved.OwnerId == Me || User.IsAdmin(); } }
        else if (!string.IsNullOrEmpty(slug)) { vm.Template = ReportTemplates.Find(slug); if (vm.Template != null) vm.Title = vm.Template.Title; }
        return await RunAsync(vm, ds, "Designer");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Export(string defJson, string format, string? title)
    {
        var def = ReportDef.FromJson(defJson);
        var ds = def == null ? null : DatasetRegistry.Find(def.Dataset);
        if (def == null || ds == null || !DatasetRegistry.Allowed(ds, User)) return Denied();
        def = ReportEngine.Sanitize(def, ds);
        var result = await ReportEngine.RunAsync(ds, def, Db);
        var name = string.IsNullOrWhiteSpace(title) ? ds.Title : title!.Trim();
        var file = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim().Replace(' ', '_');
        var (cols, rows, totals) = ReportExport.From(result);
        if (format == "pdf") { ViewBag.Title2 = name; ViewBag.Sub = Subtitle(result, User.GetFullName()).ToList(); return View("Print", result); }
        if (format == "csv") return File(ReportExport.Csv(cols, rows, totals), "text/csv; charset=utf-8", $"{file}.csv");
        return File(ReportExport.Xlsx(name, name, Subtitle(result, User.GetFullName()), cols, rows, totals), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{file}.xlsx");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string defJson, string name, string? description, bool isShared, int? id, bool asCopy = false)
    {
        var def = ReportDef.FromJson(defJson);
        var ds = def == null ? null : DatasetRegistry.Find(def.Dataset);
        if (def == null || ds == null || !DatasetRegistry.Allowed(ds, User)) return Denied();
        name = (name ?? "").Trim();
        if (name.Length == 0) { Error("Nhập tên báo cáo."); return RedirectToAction(nameof(Designer), new { id, dataset = ds.Key }); }
        def = ReportEngine.Sanitize(def, ds);
        SavedReport? r = id.HasValue && !asCopy ? await Db.SavedReports.FindAsync(id) : null;
        if (r != null && r.OwnerId != Me && !User.IsAdmin()) { Error("Chỉ người tạo mới sửa được báo cáo này – hãy lưu thành bản sao."); return RedirectToAction(nameof(Designer), new { id }); }
        if (r == null) { r = new SavedReport { OwnerId = Me, CreatedAt = DateTime.Now }; Db.SavedReports.Add(r); }
        r.Name = name.Length > 150 ? name[..150] : name; r.Description = description; r.DatasetKey = ds.Key; r.DefinitionJson = def.ToJson(); r.IsShared = isShared; r.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        Success($"Đã lưu báo cáo \"{r.Name}\".");
        return RedirectToAction(nameof(Designer), new { id = r.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await Db.SavedReports.FindAsync(id);
        if (r == null) return NotFound();
        if (r.OwnerId != Me && !User.IsAdmin()) return Denied();
        Db.SavedReports.Remove(r); await Db.SaveChangesAsync();
        Success($"Đã xóa báo cáo \"{r.Name}\".");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleShare(int id)
    {
        var r = await Db.SavedReports.FindAsync(id);
        if (r == null) return NotFound();
        if (r.OwnerId != Me && !User.IsAdmin()) return Denied();
        r.IsShared = !r.IsShared; await Db.SaveChangesAsync();
        Success(r.IsShared ? $"Đã chia sẻ \"{r.Name}\" cho mọi người." : $"Đã ngừng chia sẻ \"{r.Name}\".");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ nội bộ
    private DesignerVm NewVm()
    {
        var vm = new DesignerVm { Datasets = Allowed() };
        vm.DatasetsJson = JsonSerializer.Serialize(vm.Datasets.Select(d => new
        {
            key = d.Key, title = d.Title, group = d.Group, description = d.Description, dateMode = d.DateMode.ToString(), dateLabel = d.DateLabel,
            fields = d.Fields.Select(f => new { key = f.Key, title = f.Title, kind = f.Kind.ToString(), numeric = f.IsNumeric, additive = f.Additive })
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return vm;
    }

    private static ReportDef NewDef(Dataset ds)
    {
        var measure = ds.Fields.FirstOrDefault(f => f.Additive);
        return new ReportDef
        {
            Dataset = ds.Key, DatePreset = ds.DateMode == DateMode.None ? "all" : "thisMonth", Mode = "grouped",
            Rows = ds.Fields.Where(f => f.Kind == FieldKind.Text).Take(1).Select(f => f.Key).ToList(),
            Measures = new List<MeasureDef> { measure != null ? new MeasureDef { Field = measure.Key, Agg = Agg.Sum } : new MeasureDef { Field = ds.Fields.First().Key, Agg = Agg.Count } },
            Columns = ds.Fields.Take(8).Select(f => f.Key).ToList()
        };
    }

    private async Task<IActionResult> RunAsync(DesignerVm vm, Dataset ds, string view = "Designer")
    {
        try
        {
            vm.Def = ReportEngine.Sanitize(vm.Def, ds);
            vm.Result = await ReportEngine.RunAsync(ds, vm.Def, Db);
        }
        catch (Exception ex)
        {
            vm.Error = "Không chạy được báo cáo: " + ex.Message; vm.OpenDesigner = true;
        }
        return View(view, vm);
    }

    public static IEnumerable<string> Subtitle(ReportResult r, string who)
    {
        var period = r.From.HasValue || r.To.HasValue
            ? $"Kỳ báo cáo: {(r.From.HasValue ? r.From.Value.ToString("dd/MM/yyyy") : "…")} – {(r.To.HasValue ? r.To.Value.ToString("dd/MM/yyyy") : "…")}"
            : "Kỳ báo cáo: toàn bộ thời gian";
        yield return $"Nguồn dữ liệu: {r.DatasetTitle} · {period}";
        yield return $"Số dòng dữ liệu nguồn: {r.SourceRows:N0} · Xuất lúc {DateTime.Now:dd/MM/yyyy HH:mm} bởi {who}";
    }
}
