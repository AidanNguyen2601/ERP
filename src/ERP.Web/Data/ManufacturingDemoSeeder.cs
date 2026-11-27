using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu cho sản xuất nâng cao: trung tâm sản xuất, quy trình (routing), bán thành phẩm và định mức nhiều cấp / nhiều phiên bản,
/// lệnh sản xuất có công đoạn và sản lượng theo ca, xuất bổ sung NVL (để có chênh lệch định mức – thực tế), lệnh chờ duyệt, MTO, kế hoạch tổng thể (MPS), gia công ngoài.
/// Chạy bằng chính các service nghiệp vụ nên kho, sổ cái và giá thành khớp nhau. Mỗi bước chạy một lần; lỗi một bước không ảnh hưởng bước khác.
/// </summary>
public static class ManufacturingDemoSeeder
{
    private const string Mark = "[Mẫu SX]";

    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedProcessDemo", true)) return;
        if (!await db.Boms.AnyAsync(b => b.Code == "BOM-BLV120")) return;
        async Task Run(string name, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Dữ liệu mẫu sản xuất \"{Name}\" chưa nạp được: {Msg}", name, ex.Message); }
        }
        await Run("Trung tâm & routing", () => CentersAndRouting(db));
        await Run("Bán thành phẩm & phiên bản định mức", () => SemiFinished(db));
        await Run("Quy tắc duyệt", () => ApprovalRule(db));
        await Run("Lệnh sản xuất", () => Orders(sp, db));
        await Run("MPS", () => Mps(sp, db));
        await Run("Gia công ngoài", () => Outsource(sp, db));
        log.LogInformation("Đã nạp dữ liệu mẫu sản xuất nâng cao.");
    }

    private static async Task CentersAndRouting(ErpDbContext db)
    {
        if (await db.WorkCenters.AnyAsync()) return;
        db.WorkCenters.AddRange(
            new WorkCenter { Code = "CAT", Name = "Tổ cắt ván CNC", Department = "Xưởng sản xuất", HoursPerDay = 8, Efficiency = 85, Workers = 2, LaborRatePerHour = 45_000, OverheadRatePerHour = 60_000, UnitsPerHour = 5 },
            new WorkCenter { Code = "LR", Name = "Chuyền lắp ráp", Department = "Xưởng sản xuất", HoursPerDay = 8, Efficiency = 80, Workers = 4, LaborRatePerHour = 40_000, OverheadRatePerHour = 35_000, UnitsPerHour = 2.5m },
            new WorkCenter { Code = "SON", Name = "Chuyền sơn PU", Department = "Xưởng sản xuất", HoursPerDay = 8, Efficiency = 75, Workers = 3, LaborRatePerHour = 50_000, OverheadRatePerHour = 70_000, UnitsPerHour = 3 },
            new WorkCenter { Code = "DG", Name = "Tổ hoàn thiện & đóng gói", Department = "Kho vận", HoursPerDay = 8, Efficiency = 90, Workers = 2, LaborRatePerHour = 35_000, OverheadRatePerHour = 20_000, UnitsPerHour = 8 });
        await db.SaveChangesAsync();
        var wc = await db.WorkCenters.ToDictionaryAsync(w => w.Code, w => w.Id);
        void Ops(Bom b, params (string Name, string Wc, decimal Setup, decimal Run, int Workers)[] ops)
        {
            if (b.Operations.Any()) return;
            var seq = 0;
            foreach (var o in ops) b.Operations.Add(new BomOperation { Seq = ++seq * 10, Name = o.Name, WorkCenterId = wc[o.Wc], SetupMinutes = o.Setup, RunMinutesPerUnit = o.Run, Workers = o.Workers });
        }
        var boms = await db.Boms.Include(b => b.Operations).ToDictionaryAsync(b => b.Code);
        if (boms.TryGetValue("BOM-BLV120", out var blv)) Ops(blv, ("Cắt ván", "CAT", 20, 10, 2), ("Lắp ráp", "LR", 10, 24, 3), ("Sơn PU", "SON", 15, 18, 2), ("Hoàn thiện & đóng gói", "DG", 0, 6, 2));
        if (boms.TryGetValue("BOM-THS3", out var ths)) Ops(ths, ("Cắt ván", "CAT", 20, 16, 2), ("Lắp ráp", "LR", 10, 40, 3), ("Sơn PU", "SON", 15, 25, 2), ("Hoàn thiện & đóng gói", "DG", 0, 8, 2));
        if (boms.TryGetValue("BOM-KS06", out var ks)) Ops(ks, ("Cắt ván", "CAT", 20, 12, 2), ("Lắp ráp", "LR", 10, 22, 3), ("Hoàn thiện & đóng gói", "DG", 0, 5, 2));
        await db.SaveChangesAsync();
    }

    /// <summary>Bán thành phẩm "Khung bàn" + định mức nhiều cấp; định mức bàn làm việc phiên bản 2 dùng khung bàn (phiên bản 1 hết hiệu lực hôm qua)</summary>
    private static async Task SemiFinished(ErpDbContext db)
    {
        if (await db.Items.AnyAsync(i => i.Code == "BTP-KHUNG-BLV")) return;
        var khung = new Item { Code = "BTP-KHUNG-BLV", Name = "Khung bàn gỗ sồi (bán thành phẩm)", Unit = "Cái", Type = ItemType.SemiFinished, SalePrice = 0, MinStock = 0, VatRate = 10 };
        db.Items.Add(khung);
        await db.SaveChangesAsync();
        var wc = await db.WorkCenters.ToDictionaryAsync(w => w.Code, w => w.Id);
        var soi = await db.Items.FirstAsync(i => i.Code == "NVL-GOSOI"); var vit = await db.Items.FirstAsync(i => i.Code == "NVL-OCVIT");
        var mdf = await db.Items.FirstAsync(i => i.Code == "NVL-MDF18"); var son = await db.Items.FirstAsync(i => i.Code == "NVL-SONPU"); var ray = await db.Items.FirstAsync(i => i.Code == "NVL-RAY");
        var blv = await db.Items.FirstAsync(i => i.Code == "TP-BLV120");

        var bk = new Bom { Code = "BOM-KHUNG-BLV", FinishedItemId = khung.Id, OutputQuantity = 1, LaborCostPerUnit = 60_000, OverheadPerUnit = 30_000, Note = "Bán thành phẩm: khung chân bàn", EffectiveFrom = DateTime.Today.AddMonths(-3) };
        bk.Lines.Add(new BomLine { ItemId = soi.Id, Quantity = 2, ScrapPct = 1 }); bk.Lines.Add(new BomLine { ItemId = vit.Id, Quantity = 0.5m });
        bk.Operations.Add(new BomOperation { Seq = 10, Name = "Cắt gỗ sồi", WorkCenterId = wc["CAT"], SetupMinutes = 10, RunMinutesPerUnit = 6, Workers = 2 });
        bk.Operations.Add(new BomOperation { Seq = 20, Name = "Lắp khung", WorkCenterId = wc["LR"], SetupMinutes = 5, RunMinutesPerUnit = 12, Workers = 2 });
        db.Boms.Add(bk);

        var v1 = await db.Boms.FirstAsync(b => b.Code == "BOM-BLV120");
        v1.EffectiveTo = DateTime.Today.AddDays(-1); v1.Note = "Phiên bản 1 – dùng gỗ sồi trực tiếp";
        var v2 = new Bom { Code = "BOM-BLV120-V2", FinishedItemId = blv.Id, OutputQuantity = 1, LaborCostPerUnit = v1.LaborCostPerUnit, OverheadPerUnit = v1.OverheadPerUnit, VersionNo = 2, EffectiveFrom = DateTime.Today, Note = "Phiên bản 2 – dùng bán thành phẩm khung bàn (định mức nhiều cấp)" };
        v2.Lines.Add(new BomLine { ItemId = khung.Id, Quantity = 1 }); v2.Lines.Add(new BomLine { ItemId = mdf.Id, Quantity = 1.5m, ScrapPct = 2 });
        v2.Lines.Add(new BomLine { ItemId = vit.Id, Quantity = 0.5m }); v2.Lines.Add(new BomLine { ItemId = son.Id, Quantity = 0.3m }); v2.Lines.Add(new BomLine { ItemId = ray.Id, Quantity = 1 });
        v2.Operations.Add(new BomOperation { Seq = 10, Name = "Cắt ván", WorkCenterId = wc["CAT"], SetupMinutes = 20, RunMinutesPerUnit = 6, Workers = 2 });
        v2.Operations.Add(new BomOperation { Seq = 20, Name = "Lắp ráp", WorkCenterId = wc["LR"], SetupMinutes = 10, RunMinutesPerUnit = 14, Workers = 3 });
        v2.Operations.Add(new BomOperation { Seq = 30, Name = "Sơn PU", WorkCenterId = wc["SON"], SetupMinutes = 15, RunMinutesPerUnit = 18, Workers = 2 });
        v2.Operations.Add(new BomOperation { Seq = 40, Name = "Hoàn thiện & đóng gói", WorkCenterId = wc["DG"], SetupMinutes = 0, RunMinutesPerUnit = 6, Workers = 2 });
        db.Boms.Add(v2);
        await db.SaveChangesAsync();
    }

    private static async Task ApprovalRule(ErpDbContext db)
    {
        if (await db.ApprovalRules.AnyAsync(r => r.DocType == ApprovalDocType.ProductionOrder)) return;
        db.ApprovalRules.Add(new ApprovalRule { DocType = ApprovalDocType.ProductionOrder, Name = "Ban Giám đốc duyệt lệnh sản xuất lớn", Level = 1, MinAmount = 30_000_000, ApproverRole = AppRoles.Director });
        await db.SaveChangesAsync();
    }

    private static Task<ProductionOrder> Load(ErpDbContext db, int id) => db.ProductionOrders.Include(p => p.Item).Include(p => p.Bom).ThenInclude(b => b!.Lines).Include(p => p.Lines).ThenInclude(l => l.Item)
        .Include(p => p.Operations).Include(p => p.Outputs).AsSplitQuery().FirstAsync(p => p.Id == id);

    private static async Task Orders(IServiceProvider sp, ErpDbContext db)
    {
        if (await db.ProductionOrders.AnyAsync(p => p.Note != null && p.Note.StartsWith(Mark))) return;
        var prod = sp.GetRequiredService<ProductionService>(); var mfg = sp.GetRequiredService<ManufacturingService>(); var boms = sp.GetRequiredService<BomService>();
        var nvl = await db.Warehouses.FirstAsync(w => w.Code == "KNVL"); var tp = await db.Warehouses.FirstAsync(w => w.Code == "KTP");
        var today = DateTime.Today; var m1 = new DateTime(today.Year, today.Month, 1);
        DateTime Day(int d) { var x = m1.AddDays(d - 1); return x > today ? today : x; }
        async Task<ProductionOrder> NewOrder(string bomCode, decimal qty, int start, int due, string note, WarehouseRef? fin = null, int? salesOrderId = null)
        {
            var bom = await db.Boms.FirstAsync(b => b.Code == bomCode);
            var po = await prod.CreateAsync(new ProductionOrder { BomId = bom.Id, Quantity = qty, MaterialWarehouseId = nvl.Id, FinishedWarehouseId = fin?.Id ?? tp.Id, StartDate = Day(start), DueDate = m1.AddDays(due - 1), SalesOrderId = salesOrderId, Note = $"{Mark} {note}" });
            return await Load(db, po.Id);
        }

        // A. Bán thành phẩm khung bàn (nhập về kho NVL) – hoàn thành, có sản lượng theo ca
        var a = await NewOrder("BOM-KHUNG-BLV", 20, 2, 6, "Bán thành phẩm khung bàn cho lệnh bàn làm việc", new WarehouseRef(nvl.Id));
        await prod.ReleaseAsync(a);
        await prod.IssueMaterialsAsync(a, Day(2));
        var a1 = a.Operations.OrderBy(o => o.Seq).First(); var a2 = a.Operations.OrderBy(o => o.Seq).Last();
        await mfg.AddOutputAsync(a, a1.Id, Day(3), "Ca 1", null, 12, 1, 150, 2);
        await mfg.AddOutputAsync(a, a1.Id, Day(3), "Ca 2", null, 8, 0, 100, 2);
        await mfg.AddOutputAsync(a, a2.Id, Day(4), "Ca 1", null, 10, 0, 140, 2);
        await mfg.AddOutputAsync(a, a2.Id, Day(4), "Ca 2", null, 9, 1, 135, 2);
        await prod.CompleteAsync(a, Day(4), 19, a.Bom!.LaborCostPerUnit * 19, a.Bom.OverheadPerUnit * 19);
        await mfg.CloseAsync(a);

        // B. Bàn làm việc phiên bản 2 (dùng bán thành phẩm): xuất bổ sung MDF → chênh lệch so với định mức
        var b = await NewOrder("BOM-BLV120-V2", 10, 5, 12, "Bàn làm việc phiên bản 2 dùng khung bàn bán thành phẩm");
        await prod.ReleaseAsync(b);
        await prod.IssueMaterialsAsync(b, Day(5));
        var mdfLine = b.Lines.First(l => l.Item?.Code == "NVL-MDF18");
        await mfg.ExtraIssueAsync(b, mdfLine.Id, 1.5m, Day(6));
        var ops = b.Operations.OrderBy(o => o.Seq).ToList();
        string[] shifts = { "Ca 1", "Ca 2" };
        for (var i = 0; i < ops.Count; i++)
        {
            await mfg.AddOutputAsync(b, ops[i].Id, Day(6 + i), shifts[i % 2], null, i == ops.Count - 1 ? 4 : 6, i == 1 ? 1 : 0, 60 + i * 40, 2);
            await mfg.AddOutputAsync(b, ops[i].Id, Day(7 + i), shifts[(i + 1) % 2], null, i == ops.Count - 1 ? 5 : 3, 0, 90 + i * 30, 2);
        }
        await prod.CompleteAsync(b, Day(10), 9, Math.Round(b.Bom!.LaborCostPerUnit * 10 * 1.12m, 0), b.Bom.OverheadPerUnit * 10);
        await mfg.CloseAsync(b);

        // C. Tủ hồ sơ đang sản xuất dở: một phần công đoạn hoàn thành + phiếu cân tham chiếu lệnh
        var c = await NewOrder("BOM-THS3", 8, Math.Min(8, today.Day), 24, "Tủ hồ sơ cho đơn dự án");
        await prod.ReleaseAsync(c);
        await prod.IssueMaterialsAsync(c, Day(8));
        var cops = c.Operations.OrderBy(o => o.Seq).ToList();
        await mfg.AddOutputAsync(c, cops[0].Id, Day(9), "Ca 1", null, 8, 0, 160, 2);
        await mfg.AddOutputAsync(c, cops[1].Id, Day(10), "Ca 1", null, 5, 0, 220, 3);
        await mfg.AddOutputAsync(c, cops[1].Id, Day(10), "Ca 2", null, 2, 1, 140, 3);
        var thsItem = await db.Items.FirstAsync(i => i.Code == "TP-THS3");
        db.ScaleTickets.Add(new ScaleTicket { TicketNo = $"CAN{today:yyMM}-P01", WeighedAt = Day(11).AddHours(9), ItemCode = thsItem.Code, ItemId = thsItem.Id, Direction = "OUT", GrossKg = 3.2m, TareKg = 0.2m, NetKg = 3, Device = "Cân đếm chuyền lắp ráp", Reference = c.Number, Note = "Đếm sản lượng ca sáng" });
        db.ScaleTickets.Add(new ScaleTicket { TicketNo = $"CAN{today:yyMM}-P02", WeighedAt = Day(11).AddHours(15), ItemCode = thsItem.Code, ItemId = thsItem.Id, Direction = "OUT", GrossKg = 2.1m, TareKg = 0.1m, NetKg = 2, Device = "Cân đếm chuyền lắp ráp", Reference = c.Number, Note = "Đếm sản lượng ca chiều" });
        await db.SaveChangesAsync();

        // D. Lệnh giá trị lớn: gửi duyệt theo quy tắc (chờ Ban Giám đốc)
        var d = await NewOrder("BOM-THS3", 60, 15, 28, "Tủ hồ sơ số lượng lớn – chờ duyệt");
        try { await prod.ReleaseAsync(d); } catch (PendingApprovalException) { /* chờ duyệt */ }

        // E. Sản xuất theo đơn hàng (MTO): đơn bán đã xác nhận có thành phẩm chưa giao
        var line = await db.SalesOrderLines.Include(l => l.SalesOrder).Include(l => l.Item)
            .Where(l => l.Item!.Type == ItemType.FinishedGood && l.Quantity > l.DeliveredQty && (l.SalesOrder!.Status == SoStatus.Confirmed || l.SalesOrder.Status == SoStatus.PartiallyDelivered)).OrderBy(l => l.Id).FirstOrDefaultAsync();
        if (line != null)
        {
            var bom = await boms.ResolveAsync(line.ItemId, today);
            if (bom != null)
            {
                var po = await prod.CreateAsync(new ProductionOrder { BomId = bom.Id, Quantity = line.Quantity - line.DeliveredQty, MaterialWarehouseId = nvl.Id, FinishedWarehouseId = tp.Id, StartDate = today, DueDate = line.SalesOrder!.DeliveryDate < today ? today : line.SalesOrder.DeliveryDate, SalesOrderId = line.SalesOrderId, Note = $"{Mark} Sản xuất theo đơn {line.SalesOrder.Number}" });
                _ = po;
            }
        }
    }

    private record WarehouseRef(int Id);

    private static async Task Mps(IServiceProvider sp, ErpDbContext db)
    {
        if (await db.MpsPlans.AnyAsync()) return;
        var mps = sp.GetRequiredService<MpsService>();
        var plan = await mps.CreateAsync("Kế hoạch sản xuất quý (mẫu)", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), 3, "Dữ liệu mẫu: dự báo + đơn hàng đã xác nhận", null);
        var first = await db.MpsLines.Include(l => l.Item).Where(l => l.MpsPlanId == plan.Id && l.Item!.Type == ItemType.FinishedGood).OrderBy(l => l.Period).ThenBy(l => l.Id).ToListAsync();
        foreach (var l in first.Where(x => x.Item!.Code == "TP-BLV120" || x.Item.Code == "TP-KS06")) l.ForecastQty = l.Period == plan.FromMonth ? 15 : 30;
        await db.SaveChangesAsync();
        await mps.ApplySuggestionsAsync(plan);
    }

    private static async Task Outsource(IServiceProvider sp, ErpDbContext db)
    {
        if (await db.OutsourceOrders.AnyAsync()) return;
        var svc = sp.GetRequiredService<OutsourceService>();
        var vendor = await db.Partners.Where(p => p.IsVendor && !p.IsCustomer && p.IsActive && !p.IsBlocked && p.GroupCompanyId == null).OrderBy(p => p.Code).Skip(2).FirstOrDefaultAsync()
            ?? await db.Partners.Where(p => p.IsVendor && p.IsActive && !p.IsBlocked).OrderBy(p => p.Code).FirstAsync();
        var bom = await db.Boms.FirstAsync(b => b.Code == "BOM-KS06");
        var nvl = await db.Warehouses.FirstAsync(w => w.Code == "KNVL"); var tp = await db.Warehouses.FirstAsync(w => w.Code == "KTP");
        var today = DateTime.Today; var m1 = new DateTime(today.Year, today.Month, 1);
        DateTime Day(int d) { var x = m1.AddDays(d - 1); return x > today ? today : x; }
        var o = await svc.CreateAsync(new OutsourceOrder { Date = Day(6), VendorId = vendor.Id, BomId = bom.Id, Quantity = 10, FeePerUnit = 180_000, VatRate = 10, MaterialWarehouseId = nvl.Id, ReceiveWarehouseId = tp.Id, ExpectedDate = m1.AddDays(27), Note = "Gia công lắp ráp & sơn kệ sách đợt tháng này" });
        o = await db.OutsourceOrders.Include(x => x.Materials).Include(x => x.Receipts).Include(x => x.Item).FirstAsync(x => x.Id == o.Id);
        await svc.SendAsync(o, Day(7));
        await svc.ReceiveAsync(o, Day(12), 6, "1C26TGC", "0000123");
    }
}
