using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Controllers;

/// <summary>Báo cáo chất lượng: tỷ lệ đạt, tỷ lệ lỗi theo công đoạn, nhà cung cấp, mặt hàng, tiêu chí; xu hướng theo tháng</summary>
public class QcReportsController : ErpControllerBase
{
    public QcReportsController(ErpDbContext db) : base(db) { }

    public record Group(string Key, string Name, int Lots, int Pass, int Conditional, int Fail, decimal LotQty, decimal Rejected)
    {
        public decimal PassRate => Lots == 0 ? 0 : Math.Round(Pass * 100m / Lots, 1);
        public decimal DefectRate => LotQty == 0 ? 0 : Math.Round(Rejected * 100 / LotQty, 2);
    }
    public class ReportVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public Group Total { get; set; } = null!;
        public List<Group> ByStage { get; set; } = new();
        public List<Group> ByVendor { get; set; } = new();
        public List<Group> ByItem { get; set; } = new();
        public List<Group> Monthly { get; set; } = new();
        public List<(string Name, int Fails, int Checks)> TopCriteria { get; set; } = new();
        public int OpenNcr { get; set; }
        public int OverdueNcr { get; set; }
        public decimal ScrapValue { get; set; }
        public decimal ReturnValue { get; set; }
    }

    private static Group Make(string key, string name, IEnumerable<QcInspection> list)
    {
        var l = list.ToList();
        return new Group(key, name, l.Count, l.Count(i => i.Result == QcResult.Pass), l.Count(i => i.Result == QcResult.Conditional),
            l.Count(i => i.Result == QcResult.Fail), l.Sum(i => i.LotQty), l.Sum(i => i.RejectedQty));
    }

    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? new DateTime(t.Year, t.Month, 1).AddMonths(-5)).Date;
        if (f > t) (f, t) = (t, f);
        var list = await Db.QcInspections.Include(i => i.Item).Include(i => i.Partner).Include(i => i.Lines)
            .Where(i => i.Status == QcStatus.Completed && i.Date >= f && i.Date <= t).ToListAsync();

        var vm = new ReportVm { From = f, To = t, Total = Make("all", "Tổng", list) };
        vm.ByStage = Enum.GetValues<QcStage>().Select(s => Make(s.ToString(), s.GetDisplayName(), list.Where(i => i.Stage == s))).Where(g => g.Lots > 0).ToList();
        vm.ByVendor = list.Where(i => i.Stage == QcStage.Incoming && i.Partner != null).GroupBy(i => i.Partner!)
            .Select(g => Make(g.Key.Code, g.Key.Name, g)).OrderBy(g => g.PassRate).ThenByDescending(g => g.Rejected).ToList();
        vm.ByItem = list.GroupBy(i => i.Item!).Select(g => Make(g.Key.Code, g.Key.Name, g))
            .OrderByDescending(g => g.Rejected).ThenBy(g => g.PassRate).Take(10).ToList();
        for (var m = new DateTime(f.Year, f.Month, 1); m <= t; m = m.AddMonths(1))
        {
            var mm = m;
            vm.Monthly.Add(Make(mm.ToString("yyyy-MM"), mm.ToString("MM/yy"), list.Where(i => i.Date.Year == mm.Year && i.Date.Month == mm.Month)));
        }
        vm.TopCriteria = list.SelectMany(i => i.Lines).GroupBy(l => l.Name)
            .Select(g => (g.Key, g.Count(l => l.Passed == false), g.Count())).Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2).Take(8).ToList();

        var today = DateTime.Today;
        vm.OpenNcr = await Db.QcNonconformances.CountAsync(n => n.Status == NcrStatus.Open);
        vm.OverdueNcr = await Db.QcNonconformances.CountAsync(n => n.Status == NcrStatus.Open && n.DueDate < today);
        var docs = await Db.QcNonconformances.Where(n => n.InventoryDoc != null && n.InventoryDoc.Date >= f && n.InventoryDoc.Date <= t)
            .Select(n => new { n.Disposition, Amount = n.InventoryDoc!.Lines.Sum(l => l.Amount) }).ToListAsync();
        vm.ScrapValue = docs.Where(d => d.Disposition == NcrDisposition.Scrap).Sum(d => d.Amount);
        vm.ReturnValue = docs.Where(d => d.Disposition == NcrDisposition.ReturnToVendor).Sum(d => d.Amount);
        return View(vm);
    }
}
