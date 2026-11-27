using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>
/// Quản lý chất lượng:
///  – Kế hoạch kiểm tra (bộ tiêu chí) theo công đoạn × mặt hàng / loại hàng
///  – Phiếu kiểm tra (KCS) lập từ đơn mua (đầu vào), lệnh sản xuất (thành phẩm), đơn bán (trước giao) hoặc thủ công
///  – Kết luận phiếu: số lượng không đạt tự sinh báo cáo không phù hợp (NCR)
///  – NCR: hủy bỏ / trả lại nhà cung cấp → phiếu xuất kho + bút toán theo cấu hình bút toán (QC.Scrap, QC.ReturnPayable)
/// </summary>
public class QualityService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly AccountingService _acc;
    private readonly CurrentUser _user;

    private readonly PurchaseReturnService _returns;

    public QualityService(ErpDbContext db, InventoryService inv, AccountingService acc, CurrentUser user, PurchaseReturnService returns)
    { _db = db; _inv = inv; _acc = acc; _user = user; _returns = returns; }

    public Task<string> NextInspectionNumberAsync(DateTime date) => DocNumbers.NextAsync(_db.QcInspections.Select(x => x.Number), "KCS", date);
    public Task<string> NextNcrNumberAsync(DateTime date) => DocNumbers.NextAsync(_db.QcNonconformances.Select(x => x.Number), "NCR", date);

    /// <summary>Kế hoạch phù hợp nhất: đúng mặt hàng → đúng loại hàng → áp dụng chung</summary>
    public async Task<QcPlan?> FindPlanAsync(QcStage stage, Item item, string? operationName = null) =>
        (await _db.QcPlans.Include(p => p.Lines).Where(p => p.IsActive && p.Stage == stage
                && (p.ItemId == item.Id || (p.ItemId == null && (p.ItemType == item.Type || p.ItemType == null))))
            .ToListAsync())
        .Where(p => p.OperationName == null || p.OperationName == operationName)
        .OrderBy(p => p.OperationName != null ? 0 : 1).ThenBy(p => p.ItemId != null ? 0 : p.ItemType != null ? 1 : 2).ThenBy(p => p.Code).FirstOrDefault();

    /// <summary>Phiếu nháp với tiêu chí chép từ kế hoạch (chưa lưu)</summary>
    public async Task<QcInspection> DraftAsync(QcStage stage, Item item, decimal lotQty, DocOrigin origin = DocOrigin.Manual,
        string? originRef = null, int? originId = null, int? partnerId = null, int? warehouseId = null, int? planId = null,
        string? lotNo = null, int? operationId = null, string? operationName = null)
    {
        var plan = planId.HasValue
            ? await _db.QcPlans.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == planId && p.IsActive)
            : await FindPlanAsync(stage, item, operationName);
        var insp = new QcInspection
        {
            Date = DateTime.Today, Stage = stage, ItemId = item.Id, Item = item, LotQty = lotQty, Origin = origin, OriginRef = originRef, OriginId = originId,
            PartnerId = partnerId, WarehouseId = warehouseId, QcPlanId = plan?.Id, QcPlan = plan, InspectorId = _user.Id,
            LotNo = lotNo, ProductionOperationId = operationId, OperationName = operationName
        };
        if (plan != null)
        {
            var (sample, ac, re) = SampleFor(plan, lotQty);
            insp.SampleQty = sample; insp.AcceptNumber = ac; insp.RejectNumber = re;
            foreach (var l in plan.Lines.OrderBy(x => x.Seq))
                insp.Lines.Add(new QcInspectionLine
                {
                    Seq = l.Seq, Name = l.Name, Method = l.Method, Standard = l.Standard, Type = l.Type,
                    MinValue = l.MinValue, MaxValue = l.MaxValue, Unit = l.Unit, IsCritical = l.IsCritical
                });
        }
        insp.AcceptedQty = lotQty;
        return insp;
    }

    // ------------------------------------------------------------------ Lấy mẫu
    // Bảng AQL rút gọn tham khảo ISO 2859-1 (một lần, kiểm tra bình thường, mức II): cỡ mẫu → số lỗi chấp nhận Ac theo AQL 0.65 / 1.0 / 1.5 / 2.5 / 4.0 (Re = Ac + 1)
    private static readonly decimal[] AqlLevels = { 0.65m, 1.0m, 1.5m, 2.5m, 4.0m };
    private static readonly (int N, int[] Ac)[] AqlTable =
    {
        (2, new[] { 0, 0, 0, 0, 0 }), (3, new[] { 0, 0, 0, 0, 0 }), (5, new[] { 0, 0, 0, 0, 0 }), (8, new[] { 0, 0, 0, 0, 1 }), (13, new[] { 0, 0, 0, 1, 1 }),
        (20, new[] { 0, 0, 1, 1, 2 }), (32, new[] { 0, 1, 1, 2, 3 }), (50, new[] { 1, 1, 2, 3, 5 }), (80, new[] { 1, 2, 3, 5, 7 }), (125, new[] { 2, 3, 5, 7, 10 }),
        (200, new[] { 3, 5, 7, 10, 14 }), (315, new[] { 5, 7, 10, 14, 21 }), (500, new[] { 7, 10, 14, 21, 21 }), (800, new[] { 10, 14, 21, 21, 21 }), (1250, new[] { 14, 21, 21, 21, 21 })
    };
    private static readonly (decimal MaxLot, int N)[] LotToSample =
    {
        (8, 2), (15, 3), (25, 5), (50, 8), (90, 13), (150, 20), (280, 32), (500, 50), (1200, 80), (3200, 125), (10000, 200), (35000, 315), (150000, 500), (500000, 800), (decimal.MaxValue, 1250)
    };
    public static readonly decimal[] AqlOptions = AqlLevels;

    /// <summary>Cỡ mẫu và số lỗi chấp nhận / loại theo cách lấy mẫu của kế hoạch</summary>
    public static (decimal Sample, int? Ac, int? Re) SampleFor(QcPlan plan, decimal lotQty)
    {
        if (lotQty <= 0) return (0, null, null);
        switch (plan.SamplingMethod)
        {
            case QcSamplingMethod.Fixed:
                return (Math.Min(lotQty, Math.Max(1, plan.FixedSampleQty)), null, null);
            case QcSamplingMethod.Aql:
                var n = (decimal)LotToSample.First(x => lotQty <= x.MaxLot).N;
                if (n >= lotQty) return (lotQty, 0, 1);   // lô nhỏ hơn cỡ mẫu: kiểm 100%, không chấp nhận lỗi
                var idx = Array.IndexOf(AqlLevels, AqlLevels.OrderBy(a => Math.Abs(a - (plan.AqlValue ?? 1.0m))).First());
                var ac = AqlTable.First(t => t.N == (int)n).Ac[idx];
                return (n, ac, ac + 1);
            default:
                return (Math.Min(lotQty, Math.Max(1, Math.Ceiling(lotQty * plan.SamplePct / 100))), null, null);
        }
    }

    /// <summary>Lô vượt ngưỡng đạt (theo AQL hoặc tỷ lệ lỗi tối đa của kế hoạch)? Trả về lý do hoặc null</summary>
    public static string? ThresholdBreach(QcInspection i)
    {
        if (i.SampleQty > 0 && i.RejectNumber != null && i.DefectQty >= i.RejectNumber) return $"mẫu có {i.DefectQty.ToQty()} lỗi, từ {i.RejectNumber} lỗi trở lên phải loại lô (AQL, chấp nhận tối đa {i.AcceptNumber})";
        var max = i.QcPlan?.MaxDefectPct ?? 0;
        if (max > 0 && i.SampleQty > 0 && i.DefectQty * 100 / i.SampleQty > max) return $"tỷ lệ lỗi trong mẫu {(i.DefectQty * 100 / i.SampleQty):0.##}% vượt ngưỡng đạt {max:0.##}%";
        return null;
    }

    /// <summary>Tiêu chí đo lường: tự đánh giá theo khoảng min – max</summary>
    public static void Evaluate(QcInspectionLine l)
    {
        if (l.Type != QcCriterionType.Measure) return;
        if (l.MeasuredValue == null) { l.Passed = null; return; }
        l.Passed = (l.MinValue == null || l.MeasuredValue >= l.MinValue) && (l.MaxValue == null || l.MeasuredValue <= l.MaxValue);
    }

    /// <summary>Gợi ý kết luận: lỗi trọng yếu → Không đạt; lỗi thường → Đạt có điều kiện; đạt hết → Đạt</summary>
    public static QcResult Suggest(QcInspection i)
    {
        if (ThresholdBreach(i) != null) return QcResult.Fail;
        if (i.Lines.Any(l => l.Passed == false && l.IsCritical)) return QcResult.Fail;
        if (i.Lines.Any(l => l.Passed == false)) return QcResult.Conditional;
        if (i.Lines.Count > 0 && i.Lines.All(l => l.Passed == true)) return QcResult.Pass;
        return i.Lines.Count == 0 ? (i.RejectedQty > 0 ? QcResult.Conditional : QcResult.Pass) : QcResult.Pending;
    }

    /// <summary>Kết luận phiếu kiểm tra; số lượng không đạt sinh NCR</summary>
    public async Task<QcNonconformance?> CompleteAsync(QcInspection i)
    {
        if (i.Status != QcStatus.Draft) throw new BusinessException("Phiếu đã kết luận hoặc đã hủy.");
        foreach (var l in i.Lines) Evaluate(l);
        var missing = i.Lines.Where(l => l.Passed == null).Select(l => l.Name).ToList();
        if (missing.Any()) throw new BusinessException("Chưa đánh giá tiêu chí: " + string.Join("; ", missing));
        if (i.LotQty <= 0) throw new BusinessException("Số lượng lô phải lớn hơn 0.");
        if (i.AcceptedQty < 0 || i.RejectedQty < 0 || i.AcceptedQty + i.RejectedQty != i.LotQty)
            throw new BusinessException($"SL đạt + SL không đạt phải bằng số lượng lô ({i.LotQty.ToQty()}).");
        if (i.SampleQty > i.LotQty) throw new BusinessException("Số lượng mẫu không lớn hơn số lượng lô.");
        if (i.DefectQty < 0 || i.DefectQty > i.SampleQty) throw new BusinessException("SL mẫu lỗi phải từ 0 đến số lượng mẫu.");
        if (i.Result == QcResult.Pending) i.Result = Suggest(i);
        if (i.Result == QcResult.Pending) throw new BusinessException("Chọn kết luận cho phiếu kiểm tra.");
        if (i.Result == QcResult.Pass && i.RejectedQty > 0) throw new BusinessException("Kết luận Đạt thì SL không đạt phải bằng 0 (hoặc chọn Đạt có điều kiện).");
        if (i.Result == QcResult.Fail && i.RejectedQty == 0) throw new BusinessException("Kết luận Không đạt: nhập số lượng không đạt.");
        if (i.Result == QcResult.Pass && i.Lines.Any(l => l.Passed == false && l.IsCritical))
            throw new BusinessException("Có tiêu chí trọng yếu không đạt, không thể kết luận Đạt.");
        if (i.Result is QcResult.Pass or QcResult.Conditional && ThresholdBreach(i) is { } breach) throw new BusinessException("Lô vượt ngưỡng đạt: " + breach + ". Kết luận Không đạt.");

        i.Status = QcStatus.Completed;
        i.CompletedAt = DateTime.Now;
        i.InspectorId ??= _user.Id;
        if (string.IsNullOrEmpty(i.Number)) i.Number = await NextInspectionNumberAsync(i.Date);
        if (i.Id == 0) _db.QcInspections.Add(i);
        await _db.SaveChangesAsync();
        await AfterReinspectionAsync(i);

        if (i.RejectedQty <= 0) return null;
        var failed = i.Lines.Where(l => l.Passed == false)
            .Select(l => l.Name + (l.MeasuredValue.HasValue ? $" (đo {l.MeasuredValue.Value.ToQty()} {l.Unit}, chuẩn {l.Standard})" : "") + (string.IsNullOrWhiteSpace(l.Note) ? "" : ": " + l.Note));
        var ncr = new QcNonconformance
        {
            Number = await NextNcrNumberAsync(i.Date), Date = i.Date, InspectionId = i.Id, ItemId = i.ItemId, WarehouseId = i.WarehouseId, PartnerId = i.PartnerId,
            Quantity = i.RejectedQty, DueDate = i.Date.AddDays(7), CreatedById = _user.Id,
            Description = Trim($"Phiếu {i.Number} ({i.Stage.GetDisplayName()}): {i.RejectedQty.ToQty()} không đạt. " +
                (failed.Any() ? "Tiêu chí lỗi: " + string.Join("; ", failed) + "." : "") + (string.IsNullOrWhiteSpace(i.Note) ? "" : " " + i.Note), 1000)
        };
        _db.QcNonconformances.Add(ncr);
        await _db.SaveChangesAsync();
        return ncr;
    }

    /// <summary>Thực hiện hướng xử lý có ảnh hưởng tồn kho: hủy bỏ / trả lại nhà cung cấp → phiếu xuất kho + bút toán</summary>
    public async Task<InventoryDoc> ExecuteDispositionAsync(QcNonconformance n, DateTime date)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (n.InventoryDocId != null) throw new BusinessException("NCR đã có phiếu xuất kho.");
        if (n.Disposition is not (NcrDisposition.Scrap or NcrDisposition.ReturnToVendor))
            throw new BusinessException("Chỉ hướng xử lý Hủy bỏ hoặc Trả lại nhà cung cấp mới lập phiếu xuất kho.");
        if (n.WarehouseId == null) throw new BusinessException("Chọn kho đang giữ hàng không phù hợp.");
        var item = await _db.Items.FindAsync(n.ItemId) ?? throw new BusinessException("Không tìm thấy mặt hàng.");
        if (!item.IsStocked) throw new BusinessException("Hàng dịch vụ không quản lý tồn kho.");

        if (n.Disposition == NcrDisposition.ReturnToVendor)
            return await _returns.ReturnAsync(n, date);   // phiếu kho + công nợ + thuế + đơn mua + cấn trừ hóa đơn

        var doc = new InventoryDoc
        {
            Type = InvDocType.Issue, Date = date.Date, WarehouseId = n.WarehouseId.Value, Origin = DocOrigin.Quality, OriginRef = n.Number, OriginId = n.Id,
            CounterAccountId = await _acc.RuleAsync(PostingCatalog.QcScrap), Reason = $"Xuất hủy hàng không đạt theo {n.Number}", CreatedById = _user.Id,
            Number = await _inv.NextNumberAsync(InvDocType.Issue, date.Date)
        };
        doc.Lines.Add(new InventoryDocLine { ItemId = item.Id, Quantity = n.Quantity });
        await _inv.PostAsync(doc);
        n.InventoryDocId = doc.Id;
        await _db.SaveChangesAsync();
        return doc;
    }

    // ------------------------------------------------------------------ Giấy chứng nhận chất lượng
    public async Task<string> IssueCertificateAsync(QcInspection i)
    {
        if (i.Status != QcStatus.Completed) throw new BusinessException("Chỉ cấp giấy chứng nhận cho phiếu đã kết luận.");
        if (i.Result is not (QcResult.Pass or QcResult.Conditional)) throw new BusinessException("Chỉ cấp giấy chứng nhận khi kết luận Đạt / Đạt có điều kiện.");
        if (i.Stage is not (QcStage.Final or QcStage.Outgoing)) throw new BusinessException("Giấy chứng nhận chất lượng cấp cho kiểm tra thành phẩm hoặc trước khi giao hàng.");
        if (i.AcceptedQty <= 0) throw new BusinessException("Không có số lượng đạt để cấp giấy chứng nhận.");
        if (!string.IsNullOrEmpty(i.CertificateNo)) throw new BusinessException($"Đã cấp giấy chứng nhận {i.CertificateNo}.");
        i.CertificateNo = await DocNumbers.NextAsync(_db.QcInspections.Where(x => x.CertificateNo != null).Select(x => x.CertificateNo!), "CQ", DateTime.Today);
        i.CertificateDate = DateTime.Today; i.CertificateById = _user.Id;
        await _db.SaveChangesAsync();
        return i.CertificateNo;
    }

    // ------------------------------------------------------------------ Cách ly / thả hàng
    private async Task<Warehouse> QuarantineWarehouseAsync()
    {
        var w = await _db.Warehouses.FirstOrDefaultAsync(x => x.Code == "KCL");
        if (w != null) return w;
        w = new Warehouse { Code = "KCL", Name = "Kho cách ly hàng không đạt", Address = "Khu cách ly chất lượng" };
        _db.Warehouses.Add(w); await _db.SaveChangesAsync();
        return w;
    }

    private async Task<InventoryDoc> TransferAsync(QcNonconformance n, int fromId, int toId, DateTime date, string reason)
    {
        var doc = new InventoryDoc
        {
            Type = InvDocType.Transfer, Date = date.Date, WarehouseId = fromId, ToWarehouseId = toId, Origin = DocOrigin.Quality, OriginRef = n.Number, OriginId = n.Id,
            Reason = reason, CreatedById = _user.Id, Number = await _inv.NextNumberAsync(InvDocType.Transfer, date.Date)
        };
        doc.Lines.Add(new InventoryDocLine { ItemId = n.ItemId!.Value, Quantity = n.Quantity });
        await _inv.PostAsync(doc);
        return doc;
    }

    /// <summary>Cách ly: chuyển số lượng không phù hợp sang kho cách ly để không bị xuất dùng / giao nhầm trong khi chờ quyết định</summary>
    public async Task<InventoryDoc> QuarantineAsync(QcNonconformance n, DateTime date)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (n.QuarantinedAt != null) throw new BusinessException(n.ReleasedAt == null ? "Hàng đang được cách ly." : "Hàng đã được thả khỏi cách ly.");
        if (n.InventoryDocId != null) throw new BusinessException("NCR đã xuất kho theo hướng xử lý.");
        if (n.WarehouseId == null) throw new BusinessException("Chọn kho đang giữ hàng không phù hợp.");
        var item = await _db.Items.FindAsync(n.ItemId) ?? throw new BusinessException("Không tìm thấy mặt hàng.");
        if (!item.IsStocked) throw new BusinessException("Hàng dịch vụ không quản lý tồn kho.");
        var q = await QuarantineWarehouseAsync();
        if (n.WarehouseId == q.Id) throw new BusinessException("Hàng đang ở kho cách ly.");
        var doc = await TransferAsync(n, n.WarehouseId.Value, q.Id, date, $"Cách ly hàng không phù hợp theo {n.Number}");
        n.OriginalWarehouseId = n.WarehouseId; n.WarehouseId = q.Id; n.QuarantineDocId = doc.Id; n.QuarantinedAt = date.Date;
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Thả khỏi cách ly (chấp nhận nhân nhượng hoặc đã tái chế đạt): chuyển về kho ban đầu</summary>
    public async Task<InventoryDoc> ReleaseAsync(QcNonconformance n, DateTime date)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (!n.IsQuarantined) throw new BusinessException("Hàng không ở trạng thái cách ly.");
        if (n.Disposition is not (NcrDisposition.AcceptAsIs or NcrDisposition.Rework)) throw new BusinessException("Chỉ thả cách ly khi hướng xử lý là Chấp nhận nhân nhượng hoặc Sửa chữa / làm lại.");
        if (n.Disposition == NcrDisposition.Rework && !await ReinspectionPassedAsync(n)) throw new BusinessException("Tái chế: phải có phiếu kiểm tra lại kết luận Đạt trước khi thả cách ly.");
        var doc = await TransferAsync(n, n.WarehouseId!.Value, n.OriginalWarehouseId ?? n.WarehouseId.Value, date, $"Thả cách ly theo {n.Number}");
        n.WarehouseId = n.OriginalWarehouseId; n.ReleaseDocId = doc.Id; n.ReleasedAt = date.Date;
        await _db.SaveChangesAsync();
        return doc;
    }

    // ------------------------------------------------------------------ Tái chế / sửa chữa: kiểm tra lại
    public async Task<bool> ReinspectionPassedAsync(QcNonconformance n)
    {
        if (n.ReinspectionId == null) return false;
        var r = await _db.QcInspections.FindAsync(n.ReinspectionId);
        return r is { Status: QcStatus.Completed, RejectedQty: 0, Result: QcResult.Pass or QcResult.Conditional };
    }

    public async Task<QcInspection> CreateReinspectionAsync(QcNonconformance n)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (n.Disposition != NcrDisposition.Rework) throw new BusinessException("Chỉ lập phiếu kiểm tra lại cho hướng xử lý Sửa chữa / làm lại.");
        if (n.ReinspectionId != null && await _db.QcInspections.AnyAsync(x => x.Id == n.ReinspectionId && x.Status != QcStatus.Cancelled)) throw new BusinessException("NCR đã có phiếu kiểm tra lại.");
        var src = n.InspectionId == null ? null : await _db.QcInspections.FindAsync(n.InspectionId);
        var item = await _db.Items.FindAsync(n.ItemId) ?? throw new BusinessException("Không tìm thấy mặt hàng.");
        var insp = await DraftAsync(src?.Stage ?? QcStage.Final, item, n.Quantity, DocOrigin.Quality, n.Number, n.Id, n.PartnerId, n.WarehouseId, src?.QcPlanId);
        insp.Number = await NextInspectionNumberAsync(DateTime.Today);
        insp.Note = $"Kiểm tra lại sau sửa chữa / tái chế theo {n.Number}";
        _db.QcInspections.Add(insp);
        await _db.SaveChangesAsync();
        n.ReinspectionId = insp.Id;
        await _db.SaveChangesAsync();
        return insp;
    }

    /// <summary>Ghi kết quả kiểm tra lại vào NCR gốc</summary>
    private async Task AfterReinspectionAsync(QcInspection i)
    {
        if (i.Origin != DocOrigin.Quality || i.OriginId == null || i.Status != QcStatus.Completed) return;
        var n = await _db.QcNonconformances.FirstOrDefaultAsync(x => x.Id == i.OriginId && x.ReinspectionId == i.Id);
        if (n == null) return;
        n.RootCause = (n.RootCause + $"\nKiểm tra lại {i.Number}: {i.Result.GetDisplayName()} ({i.RejectedQty.ToQty()} không đạt)").Trim();
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ Hành động khắc phục / phòng ngừa (CAPA)
    public async Task<QcNcrAction> AddActionAsync(QcNonconformance n, QcActionKind kind, string description, string? responsible, DateTime? due)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (string.IsNullOrWhiteSpace(description)) throw new BusinessException("Nhập nội dung hành động.");
        var a = new QcNcrAction { NcrId = n.Id, Kind = kind, Description = description.Trim(), Responsible = responsible, DueDate = due?.Date };
        _db.QcNcrActions.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    public async Task CompleteActionAsync(QcNcrAction a, string? note)
    {
        if (a.Status == QcActionStatus.Done) throw new BusinessException("Hành động đã hoàn thành.");
        a.Status = QcActionStatus.Done; a.DoneAt = DateTime.Now; a.Note = note;
        await _db.SaveChangesAsync();
    }

    public async Task CloseNcrAsync(QcNonconformance n)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (n.Disposition == NcrDisposition.Pending) throw new BusinessException("Chọn hướng xử lý trước khi đóng NCR.");
        var actions = await _db.QcNcrActions.Where(a => a.NcrId == n.Id).ToListAsync();
        if (string.IsNullOrWhiteSpace(n.CorrectiveAction) && actions.Count == 0) throw new BusinessException("Nhập hành động khắc phục / phòng ngừa trước khi đóng NCR.");
        if (actions.Any(a => a.Status == QcActionStatus.Open)) throw new BusinessException("Còn hành động khắc phục / phòng ngừa chưa hoàn thành.");
        if (n.Disposition == NcrDisposition.Rework && !await ReinspectionPassedAsync(n)) throw new BusinessException("Sửa chữa / làm lại: phải có phiếu kiểm tra lại kết luận Đạt trước khi đóng NCR.");
        if (n.IsQuarantined && n.Disposition is NcrDisposition.AcceptAsIs or NcrDisposition.Rework) throw new BusinessException("Hàng còn ở kho cách ly: thả cách ly (chuyển về kho) trước khi đóng NCR.");
        if (n.Disposition is NcrDisposition.Scrap or NcrDisposition.ReturnToVendor && n.InventoryDocId == null)
            throw new BusinessException("Lập phiếu xuất kho (hủy / trả lại) trước khi đóng NCR.");
        n.Status = NcrStatus.Closed; n.ClosedAt = DateTime.Now; n.ClosedBy = _user.Name;
        await _db.SaveChangesAsync();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
