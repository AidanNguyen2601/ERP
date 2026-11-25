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

    public QualityService(ErpDbContext db, InventoryService inv, AccountingService acc, CurrentUser user)
    { _db = db; _inv = inv; _acc = acc; _user = user; }

    public Task<string> NextInspectionNumberAsync(DateTime date) => DocNumbers.NextAsync(_db.QcInspections.Select(x => x.Number), "KCS", date);
    public Task<string> NextNcrNumberAsync(DateTime date) => DocNumbers.NextAsync(_db.QcNonconformances.Select(x => x.Number), "NCR", date);

    /// <summary>Kế hoạch phù hợp nhất: đúng mặt hàng → đúng loại hàng → áp dụng chung</summary>
    public async Task<QcPlan?> FindPlanAsync(QcStage stage, Item item) =>
        (await _db.QcPlans.Include(p => p.Lines).Where(p => p.IsActive && p.Stage == stage
                && (p.ItemId == item.Id || (p.ItemId == null && (p.ItemType == item.Type || p.ItemType == null))))
            .ToListAsync())
        .OrderBy(p => p.ItemId != null ? 0 : p.ItemType != null ? 1 : 2).ThenBy(p => p.Code).FirstOrDefault();

    /// <summary>Phiếu nháp với tiêu chí chép từ kế hoạch (chưa lưu)</summary>
    public async Task<QcInspection> DraftAsync(QcStage stage, Item item, decimal lotQty, DocOrigin origin = DocOrigin.Manual,
        string? originRef = null, int? originId = null, int? partnerId = null, int? warehouseId = null, int? planId = null)
    {
        var plan = planId.HasValue
            ? await _db.QcPlans.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == planId && p.IsActive)
            : await FindPlanAsync(stage, item);
        var insp = new QcInspection
        {
            Date = DateTime.Today, Stage = stage, ItemId = item.Id, Item = item, LotQty = lotQty, Origin = origin, OriginRef = originRef, OriginId = originId,
            PartnerId = partnerId, WarehouseId = warehouseId, QcPlanId = plan?.Id, QcPlan = plan, InspectorId = _user.Id
        };
        if (plan != null)
        {
            insp.SampleQty = lotQty <= 0 ? 0 : Math.Min(lotQty, Math.Max(1, Math.Ceiling(lotQty * plan.SamplePct / 100)));
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
        if (i.Result == QcResult.Pending) i.Result = Suggest(i);
        if (i.Result == QcResult.Pending) throw new BusinessException("Chọn kết luận cho phiếu kiểm tra.");
        if (i.Result == QcResult.Pass && i.RejectedQty > 0) throw new BusinessException("Kết luận Đạt thì SL không đạt phải bằng 0 (hoặc chọn Đạt có điều kiện).");
        if (i.Result == QcResult.Fail && i.RejectedQty == 0) throw new BusinessException("Kết luận Không đạt: nhập số lượng không đạt.");
        if (i.Result == QcResult.Pass && i.Lines.Any(l => l.Passed == false && l.IsCritical))
            throw new BusinessException("Có tiêu chí trọng yếu không đạt, không thể kết luận Đạt.");

        i.Status = QcStatus.Completed;
        i.CompletedAt = DateTime.Now;
        i.InspectorId ??= _user.Id;
        if (string.IsNullOrEmpty(i.Number)) i.Number = await NextInspectionNumberAsync(i.Date);
        if (i.Id == 0) _db.QcInspections.Add(i);
        await _db.SaveChangesAsync();

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

        int counter; int? partnerId = null; string reason;
        if (n.Disposition == NcrDisposition.Scrap)
        {
            counter = await _acc.RuleAsync(PostingCatalog.QcScrap);
            reason = $"Xuất hủy hàng không đạt theo {n.Number}";
        }
        else
        {
            var vendor = n.PartnerId.HasValue ? await _db.Partners.FindAsync(n.PartnerId) : null;
            if (vendor == null || !vendor.IsVendor) throw new BusinessException("Trả lại nhà cung cấp: chọn nhà cung cấp trên NCR.");
            counter = await _acc.RuleAsync(PostingCatalog.QcReturnPayable);
            partnerId = vendor.Id;
            reason = $"Trả lại {vendor.Name} hàng không đạt theo {n.Number}";
        }
        var doc = new InventoryDoc
        {
            Type = InvDocType.Issue, Date = date.Date, WarehouseId = n.WarehouseId.Value, Origin = DocOrigin.Quality, OriginRef = n.Number, OriginId = n.Id,
            PartnerId = partnerId, CounterAccountId = counter, Reason = reason, CreatedById = _user.Id,
            Number = await _inv.NextNumberAsync(InvDocType.Issue, date.Date)
        };
        doc.Lines.Add(new InventoryDocLine { ItemId = item.Id, Quantity = n.Quantity });
        await _inv.PostAsync(doc);
        n.InventoryDocId = doc.Id;
        await _db.SaveChangesAsync();
        return doc;
    }

    public async Task CloseNcrAsync(QcNonconformance n)
    {
        if (n.Status != NcrStatus.Open) throw new BusinessException("NCR đã đóng.");
        if (n.Disposition == NcrDisposition.Pending) throw new BusinessException("Chọn hướng xử lý trước khi đóng NCR.");
        if (string.IsNullOrWhiteSpace(n.CorrectiveAction)) throw new BusinessException("Nhập hành động khắc phục / phòng ngừa trước khi đóng NCR.");
        if (n.Disposition is NcrDisposition.Scrap or NcrDisposition.ReturnToVendor && n.InventoryDocId == null)
            throw new BusinessException("Lập phiếu xuất kho (hủy / trả lại) trước khi đóng NCR.");
        n.Status = NcrStatus.Closed; n.ClosedAt = DateTime.Now; n.ClosedBy = _user.Name;
        await _db.SaveChangesAsync();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
