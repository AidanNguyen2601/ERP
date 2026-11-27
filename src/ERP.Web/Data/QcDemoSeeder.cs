using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu cho phân hệ Quản lý chất lượng, dựng trên chứng từ đang có (đơn mua đã nhận, lệnh sản xuất, đơn bán đã giao)
/// và vài nghiệp vụ mới trong kỳ đang mở (đơn mua → nhận hàng, hoàn thành lệnh sản xuất).
/// Mọi nghiệp vụ chạy qua service thật nên kho, sổ cái, công nợ luôn khớp.
/// Chỉ chạy khi chưa có phiếu kiểm tra nào; toàn bộ trong một transaction — lỗi thì rollback và bỏ qua (không chặn khởi động).
/// </summary>
public static class QcDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger logger)
    {
        if (await db.QcInspections.AnyAsync() || !await db.QcPlans.AnyAsync() || !await db.PurchaseOrders.AnyAsync()) return;
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var log = await new Runner(sp, db).RunAsync();
            await tx.CommitAsync();
            logger.LogInformation("Đã nạp dữ liệu mẫu quản lý chất lượng: {Log}", string.Join("; ", log));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Không nạp được dữ liệu mẫu quản lý chất lượng (đã hoàn tác, ứng dụng vẫn chạy bình thường)");
        }
    }

    private class Runner
    {
        private readonly ErpDbContext _db;
        private readonly QualityService _qc;
        private readonly PurchaseService _purchase;
        private readonly ProductionService _production;
        private readonly IPasswordHasher<AppUser> _hasher;
        private readonly Random _rnd = new(2026);
        private readonly List<string> _log = new();
        private AppUser _qcUser = null!;
        private DateTime _today, _openFrom;

        public Runner(IServiceProvider sp, ErpDbContext db)
        {
            _db = db;
            _qc = sp.GetRequiredService<QualityService>();
            _purchase = sp.GetRequiredService<PurchaseService>();
            _production = sp.GetRequiredService<ProductionService>();
            _hasher = sp.GetRequiredService<IPasswordHasher<AppUser>>();
        }

        public async Task<List<string>> RunAsync()
        {
            _today = DateTime.Today;
            var lastClosed = (await _db.FiscalPeriods.Where(p => p.IsClosed).ToListAsync()).Select(p => new DateTime(p.Year, p.Month, 1)).DefaultIfEmpty(DateTime.MinValue).Max();
            _openFrom = lastClosed == DateTime.MinValue ? _today.AddMonths(-1) : lastClosed.AddMonths(1);
            if (_openFrom > _today) _openFrom = _today;

            await EnsureUserAndPlansAsync();
            await NewTransactionsAsync();
            await IncomingAsync();
            await ProductionAsync();
            await OutgoingAsync();
            return _log;
        }

        /// <summary>Ngày ghi sổ hợp lệ: không trước kỳ đang mở, không sau hôm nay</summary>
        private DateTime OpenDate(DateTime wanted) => wanted < _openFrom ? _openFrom : wanted > _today ? _today : wanted.Date;

        // ------------------------------------------------------------ Người dùng QC, kế hoạch kiểm tra trong quá trình SX
        private async Task EnsureUserAndPlansAsync()
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Role == AppRoles.Quality) ?? await _db.Users.FirstOrDefaultAsync(x => x.Username == "qc");
            if (u == null)
            {
                u = new AppUser { Username = "qc", FullName = "Hồ Thị Lan", Role = AppRoles.Quality, Department = "Phòng Quản lý chất lượng", Email = "qc@noithatdemo.vn" };
                u.PasswordHash = _hasher.HashPassword(u, "Abc@123");
                _db.Users.Add(u);
                if (!await _db.CatalogItems.AnyAsync(c => c.Type == CatalogType.Department && c.Name == u.Department))
                    _db.CatalogItems.Add(new CatalogItem { Type = CatalogType.Department, Code = "PBQC", Name = u.Department, SortOrder = 900 });
                await _db.SaveChangesAsync();
                _log.Add("tạo tài khoản qc");
            }
            _qcUser = u;

            if (!await _db.QcPlans.AnyAsync(p => p.Stage == QcStage.InProcess))
            {
                _db.QcPlans.Add(new QcPlan
                {
                    Code = "QC-IP-SON", Name = "Kiểm tra sau công đoạn sơn PU", Stage = QcStage.InProcess, ItemType = ItemType.FinishedGood, SamplePct = 20,
                    Lines =
                    {
                        new QcPlanLine { Seq = 1, Name = "Độ dày màng sơn", Type = QcCriterionType.Measure, MinValue = 80, MaxValue = 120, Unit = "µm", Method = "Máy đo độ dày màng sơn", Standard = "80 – 120 µm", IsCritical = true },
                        new QcPlanLine { Seq = 2, Name = "Độ bám dính (cross-cut)", Type = QcCriterionType.PassFail, Method = "Dao cắt ô lưới + băng keo", Standard = "Cấp 0 – 1", IsCritical = true },
                        new QcPlanLine { Seq = 3, Name = "Độ bóng bề mặt", Type = QcCriterionType.Measure, MinValue = 70, MaxValue = 90, Unit = "GU", Method = "Máy đo độ bóng 60°", Standard = "70 – 90 GU" },
                        new QcPlanLine { Seq = 4, Name = "Không bụi, vết chảy sơn", Type = QcCriterionType.PassFail, Method = "Quan sát dưới đèn" },
                    }
                });
                await _db.SaveChangesAsync();
            }
        }

        // ------------------------------------------------------------ Nghiệp vụ mới trong kỳ đang mở
        private async Task NewTransactionsAsync()
        {
            async Task<PurchaseOrder?> NewPo(string itemCode, decimal qty, DateTime date)
            {
                var item = await _db.Items.FirstOrDefaultAsync(i => i.Code == itemCode);
                var last = await _db.PurchaseOrderLines.Include(l => l.PurchaseOrder).Where(l => l.Item!.Code == itemCode)
                    .OrderByDescending(l => l.PurchaseOrder!.Date).FirstOrDefaultAsync();
                if (item == null || last == null) return null;
                var po = new PurchaseOrder
                {
                    Date = date.AddDays(-4), VendorId = last.PurchaseOrder!.VendorId, WarehouseId = last.PurchaseOrder.WarehouseId, ExpectedDate = date,
                    Number = await DocNumbers.NextAsync(_db.PurchaseOrders.Select(x => x.Number), "DMH", date.AddDays(-4)), Note = "Đơn bổ sung tồn kho tháng " + date.ToString("MM/yyyy")
                };
                po.Lines.Add(new PurchaseOrderLine { ItemId = item.Id, Quantity = qty, UnitPrice = last.UnitPrice, VatRate = last.VatRate });
                PurchaseService.Recalc(po);
                _db.PurchaseOrders.Add(po);
                await _db.SaveChangesAsync();
                await _purchase.SubmitAsync(po);
                if (po.Status == PoStatus.PendingApproval) await _purchase.ApproveAsync(po, true);
                await _db.Entry(po).Collection(p => p.Lines).Query().Include(l => l.Item).LoadAsync();
                await _purchase.ReceiveAsync(po, date, po.Lines.ToDictionary(l => l.Id, l => l.Quantity));
                _log.Add($"đơn mua {po.Number} ({itemCode} × {qty}) đã nhận");
                return po;
            }
            await Try("đơn mua MDF", () => NewPo("NVL-MDF18", 150, OpenDate(_today.AddDays(-7))));
            await Try("đơn mua bản lề", () => NewPo("NVL-BANLE", 300, OpenDate(_today.AddDays(-6))));

            // Lệnh sản xuất đã phát nhưng chưa xuất NVL: xuất NVL, kiểm tra sau sơn, hoàn thành
            var mo = await _db.ProductionOrders.Include(p => p.Lines).Include(p => p.Bom).Include(p => p.Item)
                .Where(p => p.Status == ProductionStatus.Released).OrderBy(p => p.Id).FirstOrDefaultAsync();
            if (mo != null)
            {
                await Try($"xuất NVL lệnh {mo.Number}", async () => { await _production.IssueMaterialsAsync(mo, OpenDate(_today.AddDays(-5))); return mo; });
                if (mo.Status == ProductionStatus.InProgress)
                {
                    await Inspect(QcStage.InProcess, mo.Item!, mo.Quantity, DocOrigin.Production, mo.Number, mo.Id, null, mo.MaterialWarehouseId,
                        OpenDate(_today.AddDays(-3)), Outcome.Pass, 0, "Màng sơn đều, độ bóng đạt.");
                    await Try($"hoàn thành lệnh {mo.Number}", async () =>
                    {
                        await _production.CompleteAsync(mo, OpenDate(_today.AddDays(-2)), mo.Quantity, mo.Bom!.LaborCostPerUnit * mo.Quantity, mo.Bom.OverheadPerUnit * mo.Quantity);
                        return mo;
                    });
                    if (mo.Status == ProductionStatus.Completed) _log.Add($"hoàn thành lệnh {mo.Number}");
                }
            }
        }

        // ------------------------------------------------------------ Kiểm tra đầu vào theo đơn mua đã nhận
        private async Task IncomingAsync()
        {
            var lines = await _db.PurchaseOrderLines.Include(l => l.PurchaseOrder).Include(l => l.Item)
                .Where(l => l.ReceivedQty > 0).OrderBy(l => l.PurchaseOrder!.Date).ThenBy(l => l.Id).ToListAsync();
            var n = 0;
            foreach (var l in lines)
            {
                var po = l.PurchaseOrder!;
                var received = await _db.InventoryDocs.Where(d => d.Origin == DocOrigin.Purchase && d.OriginId == po.Id).MinAsync(d => (DateTime?)d.Date) ?? po.Date;
                var date = received.AddDays(1) > _today ? received : received.AddDays(1);
                var code = l.Item!.Code;
                n++;
                // Kịch bản: MDF tháng 9 dày vượt chuẩn → trả NCC; bản lề lô mới lỗi giảm chấn → NCR chưa xử lý (quá hạn);
                // bản lề tháng 8 bao bì rách → chấp nhận; ghế xoay trầy nhẹ → sửa lại; đèn LED không sáng → trả NCC
                if (code == "NVL-MDF18" && po.Date.Month == 9)
                {
                    var ncr = await Inspect(QcStage.Incoming, l.Item, l.ReceivedQty, DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, date, Outcome.FailCritical,
                        Math.Round(l.ReceivedQty * 0.1m, 0), "Lô ván dày 18,6 mm, vượt dung sai; nhà cung cấp xác nhận lỗi máy cắt.");
                    await Resolve(ncr, NcrDisposition.ReturnToVendor, "Dao cắt của NCC bị mòn, không hiệu chỉnh trước ca.", "Yêu cầu NCC gửi biên bản hiệu chỉnh máy; tăng mẫu kiểm 2 lô kế tiếp lên 10%.", true, true);
                }
                else if (code == "NVL-BANLE" && po.Date >= _openFrom.AddDays(-10))
                {
                    var ncr = await Inspect(QcStage.Incoming, l.Item, l.ReceivedQty, DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, date, Outcome.FailCritical,
                        12, "12 bản lề giảm chấn đóng không êm, bật ngược.");
                    if (ncr != null) { ncr.Disposition = NcrDisposition.Pending; ncr.DueDate = _today.AddDays(-1); ncr.Responsible = "Phòng Mua hàng"; await _db.SaveChangesAsync(); }
                }
                else if (code == "NVL-BANLE")
                {
                    var ncr = await Inspect(QcStage.Incoming, l.Item, l.ReceivedQty, DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, date, Outcome.FailMinor,
                        8, "8 hộp rách bao bì, sản phẩm bên trong còn tốt.");
                    await Resolve(ncr, NcrDisposition.AcceptAsIs, "Vận chuyển xếp chồng quá cao.", "Đề nghị NCC tăng độ dày thùng carton.", false, true);
                }
                else if (code == "HH-GXVP")
                {
                    var ncr = await Inspect(QcStage.Incoming, l.Item, l.ReceivedQty, DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, date, Outcome.FailMinor,
                        3, "3 ghế trầy nhẹ tay vịn.");
                    await Resolve(ncr, NcrDisposition.Rework, "Va chạm trong container.", "Đánh bóng lại tay vịn tại xưởng; NCC bù chi phí 150.000đ/ghế.", false, true);
                }
                else if (code == "HH-DENLED")
                {
                    var ncr = await Inspect(QcStage.Incoming, l.Item, l.ReceivedQty, DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, date, Outcome.FailCritical,
                        6, "6 đèn không sáng khi cấp nguồn.");
                    await Resolve(ncr, NcrDisposition.ReturnToVendor, "Lỗi driver nguồn của lô sản xuất.", null, true, false);
                }
                else
                {
                    await Inspect(QcStage.Incoming, l.Item, l.ReceivedQty, DocOrigin.Purchase, po.Number, po.Id, po.VendorId, po.WarehouseId, date, Outcome.Pass, 0, null);
                }
            }
            _log.Add($"{n} phiếu kiểm tra đầu vào");
        }

        // ------------------------------------------------------------ Kiểm tra thành phẩm theo lệnh sản xuất đã hoàn thành
        private async Task ProductionAsync()
        {
            var mos = await _db.ProductionOrders.Include(p => p.Item).Where(p => p.Status == ProductionStatus.Completed && p.CompletedQty > 0).OrderBy(p => p.CompletedDate).ToListAsync();
            var k = 0;
            foreach (var mo in mos)
            {
                k++;
                var date = mo.CompletedDate ?? mo.DueDate;
                if (k == 2)
                {
                    var ncr = await Inspect(QcStage.Final, mo.Item!, mo.CompletedQty, DocOrigin.Production, mo.Number, mo.Id, null, mo.FinishedWarehouseId, date, Outcome.FailMinor,
                        1, "1 tủ bản lề cánh trái đóng không êm.");
                    await Resolve(ncr, NcrDisposition.Rework, "Lắp bản lề lệch lỗ khoan.", "Bổ sung dưỡng khoan bản lề; kiểm 100% cánh tủ lô sau.", false, true);
                }
                else if (k == 4)
                {
                    var ncr = await Inspect(QcStage.Final, mo.Item!, mo.CompletedQty, DocOrigin.Production, mo.Number, mo.Id, null, mo.FinishedWarehouseId, date, Outcome.FailCritical,
                        2, "2 mặt bàn màng sơn bị bọt khí, không sửa được.");
                    await Resolve(ncr, NcrDisposition.Scrap, "Độ ẩm phòng sơn cao (82%) khi phun lớp phủ.", "Lắp máy hút ẩm phòng sơn; ghi nhật ký độ ẩm mỗi ca.", true, true);
                }
                else if (k == mos.Count && mo.CompletedDate >= _openFrom)
                {
                    var ncr = await Inspect(QcStage.Final, mo.Item!, mo.CompletedQty, DocOrigin.Production, mo.Number, mo.Id, null, mo.FinishedWarehouseId, date, Outcome.FailMinor,
                        1, "1 bàn chênh kích thước chân 3 mm.");
                    if (ncr != null) { ncr.Disposition = NcrDisposition.Rework; ncr.Responsible = "Tổ trưởng xưởng"; ncr.DueDate = _today.AddDays(5); await _db.SaveChangesAsync(); }
                }
                else
                {
                    await Inspect(QcStage.Final, mo.Item!, mo.CompletedQty, DocOrigin.Production, mo.Number, mo.Id, null, mo.FinishedWarehouseId, date, Outcome.Pass, 0, null);
                }
            }
            _log.Add($"{mos.Count} phiếu kiểm tra thành phẩm");
        }

        // ------------------------------------------------------------ Kiểm tra trước giao hàng theo đơn bán
        private async Task OutgoingAsync()
        {
            var lines = await _db.SalesOrderLines.Include(l => l.SalesOrder).Include(l => l.Item)
                .Where(l => l.DeliveredQty > 0 && l.Item!.Type != ItemType.Service).OrderBy(l => l.SalesOrder!.Date).ThenBy(l => l.Id).ToListAsync();
            var c = 0;
            foreach (var l in lines)
            {
                var so = l.SalesOrder!;
                var delivered = await _db.InventoryDocs.Where(d => d.Origin == DocOrigin.Sales && d.OriginId == so.Id).MinAsync(d => (DateTime?)d.Date) ?? so.DeliveryDate;
                c++;
                if (c == 5)
                {
                    var ncr = await Inspect(QcStage.Outgoing, l.Item!, l.DeliveredQty, DocOrigin.Sales, so.Number, so.Id, so.CustomerId, so.WarehouseId, delivered, Outcome.FailMinor,
                        1, "1 kệ trầy góc khi xếp xe.");
                    await Resolve(ncr, NcrDisposition.AcceptAsIs, "Không lót xốp góc khi xếp hàng.", "Khách đồng ý nhận, giảm giá 5% dòng hàng; bổ sung quy định lót xốp góc.", false, true);
                }
                else await Inspect(QcStage.Outgoing, l.Item!, l.DeliveredQty, DocOrigin.Sales, so.Number, so.Id, so.CustomerId, so.WarehouseId, delivered, Outcome.Pass, 0, null);
            }
            // Một phiếu nháp đang kiểm dở: đơn bán đã xác nhận, chưa giao
            var pending = await _db.SalesOrderLines.Include(l => l.SalesOrder).Include(l => l.Item)
                .Where(l => l.SalesOrder!.Status == SoStatus.Confirmed && l.DeliveredQty < l.Quantity && l.Item!.Type != ItemType.Service).FirstOrDefaultAsync();
            if (pending != null)
            {
                var so = pending.SalesOrder!;
                var d = await _qc.DraftAsync(QcStage.Outgoing, pending.Item!, pending.Quantity, DocOrigin.Sales, so.Number, so.Id, so.CustomerId, so.WarehouseId);
                d.Date = _today; d.InspectorId = _qcUser.Id; d.Number = await _qc.NextInspectionNumberAsync(_today); d.Item = null; d.QcPlan = null;
                var first = d.Lines.OrderBy(x => x.Seq).FirstOrDefault();
                if (first != null) first.Passed = true;
                _db.QcInspections.Add(d);
                await _db.SaveChangesAsync();
                c++;
            }
            _log.Add($"{c} phiếu kiểm tra trước giao hàng");
        }

        // ------------------------------------------------------------ Tiện ích
        private enum Outcome { Pass, FailMinor, FailCritical }

        private async Task<QcNonconformance?> Inspect(QcStage stage, Item item, decimal lot, DocOrigin origin, string originRef, int originId,
            int? partnerId, int? warehouseId, DateTime date, Outcome outcome, decimal rejected, string? note)
        {
            if (lot <= 0) return null;
            var i = await _qc.DraftAsync(stage, item, lot, origin, originRef, originId, partnerId, warehouseId);
            i.Date = date.Date; i.InspectorId = _qcUser.Id; i.Note = note; i.Item = null; i.QcPlan = null;
            var lines = i.Lines.OrderBy(l => l.Seq).ToList();
            var failLine = outcome switch
            {
                Outcome.FailCritical => lines.FirstOrDefault(l => l.IsCritical),
                Outcome.FailMinor => lines.FirstOrDefault(l => !l.IsCritical),
                _ => null
            };
            foreach (var l in lines)
            {
                var fail = l == failLine;
                if (l.Type == QcCriterionType.Measure)
                {
                    var min = l.MinValue ?? (l.MaxValue ?? 0) - 10; var max = l.MaxValue ?? (l.MinValue ?? 0) + 10;
                    var span = max - min;
                    l.MeasuredValue = fail ? Math.Round(max + Math.Max(span * 0.25m, 0.1m), 2)
                        : Math.Round(min + span * (0.3m + (decimal)_rnd.NextDouble() * 0.4m), 2);
                }
                else l.Passed = !fail;
                if (fail) l.Note = note;
                QualityService.Evaluate(l);
            }
            rejected = Math.Min(rejected, lot);
            i.RejectedQty = outcome == Outcome.Pass ? 0 : Math.Max(rejected, 1);
            i.AcceptedQty = lot - i.RejectedQty;
            i.Result = QcResult.Pending; // để hệ thống gợi ý kết luận theo tiêu chí
            if (outcome != Outcome.Pass && failLine == null) i.Result = QcResult.Conditional; // kế hoạch không có tiêu chí phù hợp để đánh lỗi
            var ncr = await _qc.CompleteAsync(i);
            if (ncr != null) { ncr.CreatedById = _qcUser.Id; ncr.Responsible = "Phòng Quản lý chất lượng"; await _db.SaveChangesAsync(); }
            return ncr;
        }

        /// <summary>Quyết định hướng xử lý; lập phiếu xuất kho nếu cần; đóng NCR nếu có hành động khắc phục</summary>
        private async Task Resolve(QcNonconformance? n, NcrDisposition d, string rootCause, string? corrective, bool executeStock, bool close)
        {
            if (n == null) return;
            n.Disposition = d; n.RootCause = rootCause; n.CorrectiveAction = corrective;
            await _db.SaveChangesAsync();
            if (executeStock)
            {
                var doc = await Try($"xuất kho theo {n.Number}", () => _qc.ExecuteDispositionAsync(n, OpenDate(n.Date.AddDays(2))));
                if (doc == null) return; // không đủ tồn kho hiện tại → để NCR mở chờ xử lý
            }
            if (close && !string.IsNullOrWhiteSpace(corrective))
            {
                await _qc.CloseNcrAsync(n);
                n.ClosedBy = _qcUser.FullName;
                n.ClosedAt = OpenDate(n.Date.AddDays(5)).AddHours(16);
                await _db.SaveChangesAsync();
            }
        }

        /// <summary>Bước phụ thuộc dữ liệu hiện tại (tồn kho...): lỗi nghiệp vụ thì bỏ qua bước đó</summary>
        private async Task<T?> Try<T>(string what, Func<Task<T?>> action) where T : class
        {
            try { return await action(); }
            catch (BusinessException ex) { _log.Add($"bỏ qua {what}: {ex.Message}"); _db.ChangeTracker.Clear(); return null; }
        }
    }
}
