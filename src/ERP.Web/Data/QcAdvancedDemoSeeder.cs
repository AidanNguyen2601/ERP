using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu cho quản lý chất lượng nâng cao: lấy mẫu AQL / cố định và ngưỡng đạt, kiểm tra trong quá trình theo công đoạn và lô,
/// giấy chứng nhận chất lượng, cách ly → tái chế → kiểm tra lại → thả hàng và hành động khắc phục. Chạy một lần bằng service nghiệp vụ.
/// </summary>
public static class QcAdvancedDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedQcDemo", true)) return;
        if (!await db.QcPlans.AnyAsync()) return;
        var qc = sp.GetRequiredService<QualityService>();
        async Task Run(string name, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Dữ liệu mẫu chất lượng nâng cao \"{Name}\" chưa nạp được: {Msg}", name, ex.Message); }
        }
        await Run("Lấy mẫu", () => Sampling(db));
        await Run("Kiểm tra theo công đoạn", () => InProcess(db, qc));
        await Run("Giấy chứng nhận", () => Certificate(db, qc));
        await Run("Cách ly & tái chế", () => QuarantineFlow(db, qc));
        log.LogInformation("Đã nạp dữ liệu mẫu chất lượng nâng cao.");
    }

    private static async Task Sampling(ErpDbContext db)
    {
        if (await db.QcPlans.AnyAsync(p => p.SamplingMethod != QcSamplingMethod.Percent)) return;
        var plans = await db.QcPlans.OrderBy(p => p.Id).ToListAsync();
        var incoming = plans.FirstOrDefault(p => p.Stage == QcStage.Incoming && p.ItemId != null) ?? plans.FirstOrDefault(p => p.Stage == QcStage.Incoming);
        if (incoming != null) { incoming.SamplingMethod = QcSamplingMethod.Aql; incoming.AqlValue = 1.0m; incoming.MaxDefectPct = 5; }
        var final = plans.FirstOrDefault(p => p.Stage == QcStage.Final);
        if (final != null) { final.SamplingMethod = QcSamplingMethod.Aql; final.AqlValue = 2.5m; final.MaxDefectPct = 10; }
        var outgoing = plans.FirstOrDefault(p => p.Stage == QcStage.Outgoing);
        if (outgoing != null) { outgoing.SamplingMethod = QcSamplingMethod.Fixed; outgoing.FixedSampleQty = 5; }
        var inproc = plans.FirstOrDefault(p => p.Stage == QcStage.InProcess);
        if (inproc != null) { inproc.OperationName = "Sơn PU"; inproc.MaxDefectPct = 8; }
        await db.SaveChangesAsync();
    }

    /// <summary>Điền kết quả các tiêu chí: tất cả đạt (đo lường lấy giá trị giữa khoảng); failFirst: tiêu chí đầu không đạt</summary>
    private static void Fill(QcInspection i, bool failFirst = false)
    {
        var first = true;
        foreach (var l in i.Lines.OrderBy(x => x.Seq))
        {
            if (l.Type == QcCriterionType.Measure)
            {
                var mid = ((l.MinValue ?? l.MaxValue ?? 0) + (l.MaxValue ?? l.MinValue ?? 0)) / 2;
                l.MeasuredValue = failFirst && first ? (l.MaxValue ?? mid) + 1 : mid;
            }
            else l.Passed = !(failFirst && first);
            QualityService.Evaluate(l);
            if (l.Type == QcCriterionType.Measure) { }
            first = false;
        }
    }

    private static async Task InProcess(ErpDbContext db, QualityService qc)
    {
        if (await db.QcInspections.AnyAsync(i => i.LotNo == "LOT-THS-0001")) return;
        var mo = await db.ProductionOrders.Include(p => p.Item).Include(p => p.Operations).FirstOrDefaultAsync(p => p.Note != null && p.Note.StartsWith("[Mẫu SX] Tủ hồ sơ cho đơn dự án"));
        if (mo == null) return;
        var op = mo.Operations.OrderBy(o => o.Seq).FirstOrDefault(o => o.Name.Contains("Sơn")) ?? mo.Operations.OrderBy(o => o.Seq).Skip(1).FirstOrDefault();
        if (op == null) return;
        var lot = Math.Max(op.GoodQty, 8);
        var insp = await qc.DraftAsync(QcStage.InProcess, mo.Item!, lot, DocOrigin.Production, mo.Number, mo.Id, null, mo.MaterialWarehouseId, null, "LOT-THS-0001", op.Id, op.Name);
        Fill(insp);
        insp.Date = DateTime.Today; insp.DefectQty = 0; insp.AcceptedQty = lot; insp.RejectedQty = 0; insp.Result = QcResult.Pass; insp.Note = $"Kiểm tra giữa công đoạn {op.Name}, lô LOT-THS-0001";
        await qc.CompleteAsync(insp);
    }

    private static async Task Certificate(ErpDbContext db, QualityService qc)
    {
        if (await db.QcInspections.AnyAsync(i => i.CertificateNo != null)) return;
        var mo = await db.ProductionOrders.Include(p => p.Item).FirstOrDefaultAsync(p => p.Note != null && p.Note.StartsWith("[Mẫu SX] Bàn làm việc phiên bản 2") && (p.Status == ProductionStatus.Completed || p.Status == ProductionStatus.Closed));
        if (mo == null) return;
        var lot = mo.CompletedQty > 0 ? mo.CompletedQty : mo.Quantity;
        var insp = await qc.DraftAsync(QcStage.Final, mo.Item!, lot, DocOrigin.Production, mo.Number, mo.Id, null, mo.FinishedWarehouseId, null, "LOT-BLV-V2-01");
        Fill(insp);
        insp.DefectQty = 0; insp.AcceptedQty = lot; insp.RejectedQty = 0; insp.Result = QcResult.Pass; insp.Note = "Lô đạt, đủ điều kiện nhập kho và xuất hàng";
        await qc.CompleteAsync(insp);
        await qc.IssueCertificateAsync(insp);
    }

    private static async Task QuarantineFlow(ErpDbContext db, QualityService qc)
    {
        if (await db.QcNcrActions.AnyAsync()) return;
        var item = await db.Items.FirstAsync(i => i.Code == "TP-KS06");
        var wh = await db.Warehouses.FirstAsync(w => w.Code == "KTP");
        var insp = await qc.DraftAsync(QcStage.Final, item, 20, DocOrigin.Manual, null, null, null, wh.Id);
        Fill(insp, failFirst: true);
        insp.DefectQty = 2; insp.AcceptedQty = 18; insp.RejectedQty = 2; insp.Result = QcResult.Fail; insp.Note = "Lô kệ sách: 2 chiếc lệch kích thước";
        var ncr = await qc.CompleteAsync(insp) ?? throw new InvalidOperationException("Không sinh NCR");
        await qc.QuarantineAsync(ncr, DateTime.Today);
        ncr.Disposition = NcrDisposition.Rework; ncr.Responsible = "Quản đốc xưởng"; ncr.RootCause = "Dao cắt mòn, sai số kích thước";
        await db.SaveChangesAsync();
        var re = await qc.CreateReinspectionAsync(ncr);
        re = await db.QcInspections.Include(x => x.Lines).Include(x => x.Item).Include(x => x.QcPlan).FirstAsync(x => x.Id == re.Id);
        Fill(re);
        re.DefectQty = 0; re.AcceptedQty = re.LotQty; re.RejectedQty = 0; re.Result = QcResult.Pass;
        await qc.CompleteAsync(re);
        await qc.ReleaseAsync(ncr, DateTime.Today);
        var done = await qc.AddActionAsync(ncr, QcActionKind.Corrective, "Thay dao cắt, hiệu chuẩn lại máy CNC", "Tổ trưởng cắt ván", DateTime.Today);
        await qc.CompleteActionAsync(done, "Đã thay dao và chạy thử đạt");
        await qc.AddActionAsync(ncr, QcActionKind.Preventive, "Lập lịch kiểm tra dao cắt hằng tuần", "Phòng Quản lý chất lượng", DateTime.Today.AddDays(7));
    }
}
