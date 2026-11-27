using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu cho các quy trình nghiệp vụ mới (mua hàng PR → RFQ → PO → nhận hàng theo lô, chi phí mua hàng, kho, bán hàng, ngoại tệ, bù trừ công nợ,
/// ngân hàng, CCDC, định kỳ, ngân sách, lương, thuế...). Phát sinh trong tháng hiện tại (kỳ đang mở) bằng chính các service nghiệp vụ nên
/// bút toán, tồn kho, công nợ đều nhất quán. Mỗi phần chạy một lần (có dấu hiệu nhận biết) và lỗi một phần không ảnh hưởng phần khác.
/// </summary>
public static class ProcessDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedProcessDemo", true)) return;
        if (!await db.Items.AnyAsync() || !await db.Partners.AnyAsync(p => p.IsCustomer)) return;
        var c = new Ctx(sp, db);
        async Task Run(string name, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Dữ liệu mẫu \"{Name}\" chưa nạp được: {Msg}", name, ex.Message); }
        }
        await Run("Thiết lập", () => Setup(c));
        await Run("Hợp đồng", () => Contracts(c));
        await Run("Mua hàng", () => Procurement(c));
        await Run("Kho", () => Warehouse(c));
        await Run("Bán hàng", () => Sales(c));
        await Run("Ngoại tệ", () => Fx(c));
        await Run("Bù trừ công nợ", () => Offsets(c));
        await Run("CCDC & định kỳ", () => ToolsAndRecurring(c));
        await Run("Ngân hàng", () => Bank(c));
        await Run("Ngân sách", () => Budgets(c));
        await Run("Lương", () => Payroll(c));
        await Run("Thuế", () => Tax(c));
        await Run("Thiết bị", () => Devices(c));
        log.LogInformation("Đã nạp dữ liệu mẫu quy trình nghiệp vụ.");
    }

    private class Ctx
    {
        public readonly IServiceProvider Sp; public readonly ErpDbContext Db; public readonly DateTime Today = DateTime.Today;
        public Ctx(IServiceProvider sp, ErpDbContext db) { Sp = sp; Db = db; }
        public T S<T>() where T : notnull => Sp.GetRequiredService<T>();
        /// <summary>Ngày trong tháng hiện tại, không vượt quá hôm nay</summary>
        public DateTime Day(int d) { var x = new DateTime(Today.Year, Today.Month, 1).AddDays(d - 1); return x > Today ? Today : x; }
        public Task<Item> Item(string code) => Db.Items.FirstAsync(i => i.Code == code);
        public async Task<List<Partner>> Customers(int n) => await Db.Partners.Where(p => p.IsCustomer && !p.IsVendor && p.IsActive && !p.IsBlocked && p.GroupCompanyId == null).OrderBy(p => p.Code).Take(n).ToListAsync();
        public async Task<List<Partner>> Vendors(int n) => await Db.Partners.Where(p => p.IsVendor && !p.IsCustomer && p.IsActive && !p.IsBlocked && p.GroupCompanyId == null).OrderBy(p => p.Code).Take(n).ToListAsync();
        public async Task<Warehouse> Wh(string code) => await Db.Warehouses.FirstOrDefaultAsync(w => w.Code == code) ?? await Db.Warehouses.OrderBy(w => w.Id).FirstAsync();
    }

    // ------------------------------------------------------------------ thiết lập chung
    private static async Task Setup(Ctx c)
    {
        var db = c.Db;
        if (!await db.ApprovalRules.AnyAsync(r => r.DocType == ApprovalDocType.PurchaseRequisition))
            db.ApprovalRules.Add(new ApprovalRule { DocType = ApprovalDocType.PurchaseRequisition, Name = "Ban Giám đốc duyệt đề nghị mua lớn", Level = 1, MinAmount = 50_000_000, ApproverRole = AppRoles.Director });
        if (!await db.Partners.AnyAsync(p => p.Code == "NCC-TN"))
            db.Partners.Add(new Partner { Code = "NCC-TN", Name = "Công ty TNHH Vật tư Bình An (tạm ngừng)", TaxCode = "0312999111", IsVendor = true, IsBlocked = true, BlockReason = "Giao hàng sai quy cách nhiều lần, chờ đánh giá lại", Grade = "D", PartnerGroup = "Nhà cung cấp", Address = "Bình Dương" });
        // Quy đổi đơn vị tính
        var ocvit = await db.Items.FirstOrDefaultAsync(i => i.Code == "NVL-OCVIT");
        if (ocvit != null && !await db.UomConversions.AnyAsync(u => u.ItemId == ocvit.Id)) db.UomConversions.AddRange(new UomConversion { ItemId = ocvit.Id, Unit = "Hộp", Factor = 100 }, new UomConversion { ItemId = ocvit.Id, Unit = "Thùng", Factor = 1000 });
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ hợp đồng
    private static async Task Contracts(Ctx c)
    {
        var db = c.Db; if (await db.Contracts.AnyAsync()) return;
        var customers = await c.Customers(3); var vendors = await c.Vendors(1);
        var y = c.Today.Year;
        var hdb1 = new Contract { Number = "HDB-0001", Type = ContractType.Sale, PartnerId = customers[0].Id, Title = "Cung cấp nội thất văn phòng khu căn hộ – giai đoạn 1", SignDate = new DateTime(y, 1, 10), EndDate = new DateTime(y, 12, 31), Value = 1_200_000_000, PaymentTermDays = 30, PaymentTerms = "Tạm ứng 30% khi ký, 50% khi giao hàng, 20% sau nghiệm thu" };
        var hdb2 = new Contract { Number = "HDB-0002", Type = ContractType.Sale, PartnerId = customers[Math.Min(1, customers.Count - 1)].Id, Title = "Cung cấp bàn ghế học sinh – dự án trường học", SignDate = c.Day(1).AddMonths(-2), EndDate = c.Day(1).AddMonths(3), Value = 650_000_000, PaymentTermDays = 45, PaymentTerms = "Thanh toán 3 đợt theo tiến độ giao hàng" };
        var hdm1 = new Contract { Number = "HDM-0001", Type = ContractType.Purchase, PartnerId = vendors[0].Id, Title = "Mua ván MDF và vật tư phụ năm " + y, SignDate = new DateTime(y, 1, 5), EndDate = new DateTime(y, 12, 31), Value = 900_000_000, PaymentTermDays = 30, PaymentTerms = "Thanh toán 30 ngày kể từ ngày nhận hóa đơn" };
        db.Contracts.AddRange(hdb1, hdb2, hdm1); await db.SaveChangesAsync();
        db.ContractMilestones.AddRange(
            new ContractMilestone { ContractId = hdb1.Id, Kind = MilestoneKind.Payment, Name = "Tạm ứng 30%", DueDate = new DateTime(y, 1, 20), Amount = 360_000_000, Done = true, DoneDate = new DateTime(y, 1, 18) },
            new ContractMilestone { ContractId = hdb1.Id, Kind = MilestoneKind.Delivery, Name = "Giao hàng đợt 1", DueDate = c.Day(1).AddDays(-20), Amount = 600_000_000, Done = true, DoneDate = c.Day(1).AddDays(-22) },
            new ContractMilestone { ContractId = hdb1.Id, Kind = MilestoneKind.Payment, Name = "Thanh toán đợt 2 – 50%", DueDate = c.Day(1).AddDays(-5), Amount = 600_000_000 },
            new ContractMilestone { ContractId = hdb1.Id, Kind = MilestoneKind.Delivery, Name = "Giao hàng đợt 2", DueDate = c.Day(1).AddDays(25), Amount = 600_000_000 },
            new ContractMilestone { ContractId = hdb2.Id, Kind = MilestoneKind.Delivery, Name = "Giao 300 bộ bàn ghế", DueDate = c.Day(1).AddDays(10), Amount = 300_000_000 },
            new ContractMilestone { ContractId = hdb2.Id, Kind = MilestoneKind.Payment, Name = "Thanh toán đợt 1", DueDate = c.Day(1).AddDays(40), Amount = 300_000_000 },
            new ContractMilestone { ContractId = hdm1.Id, Kind = MilestoneKind.Delivery, Name = "Giao MDF theo từng đơn đặt hàng", DueDate = c.Day(1).AddDays(15), Amount = 0, Note = "Theo đơn mua" });
        // Gắn các chứng từ hiện có của khách hàng / nhà cung cấp vào hợp đồng
        foreach (var inv in await db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.PartnerId == hdb1.PartnerId && i.ContractId == null).ToListAsync()) inv.ContractId = hdb1.Id;
        foreach (var so in await db.SalesOrders.Where(s => s.CustomerId == hdb1.PartnerId && s.ContractId == null).ToListAsync()) so.ContractId = hdb1.Id;
        foreach (var inv in await db.Invoices.Where(i => i.Direction == InvoiceDirection.Purchase && i.PartnerId == hdm1.PartnerId && i.ContractId == null).ToListAsync()) inv.ContractId = hdm1.Id;
        foreach (var po in await db.PurchaseOrders.Where(p => p.VendorId == hdm1.PartnerId && p.ContractId == null).ToListAsync()) po.ContractId = hdm1.Id;
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ mua hàng: PR → RFQ → PO → nhận hàng theo lô → hóa đơn → chi phí mua hàng
    private static async Task Procurement(Ctx c)
    {
        var db = c.Db; if (await db.PurchaseRequisitions.AnyAsync()) return;
        var req = c.S<RequisitionService>(); var purchase = c.S<PurchaseService>(); var landed = c.S<LandedCostService>(); var acc = c.S<AccountingService>();
        var vendors = await c.Vendors(3); var wNvl = await c.Wh("KNVL");
        var ocvit = await c.Item("NVL-OCVIT"); var mdf = await c.Item("NVL-MDF18"); var gosoi = await c.Item("NVL-GOSOI");
        // Bật theo dõi lô / hạn dùng / FIFO cho vật tư mẫu
        ocvit.TrackMode = TrackMode.Lot; ocvit.TrackExpiry = true; ocvit.CostMethod = CostMethod.Fifo; ocvit.ExpiryAlertDays = 60; if (ocvit.MinStock == 0) ocvit.MinStock = 200;
        await db.SaveChangesAsync();
        var price = ocvit.AvgCost > 0 ? ocvit.AvgCost : 15_000;

        // Đề nghị mua đã duyệt → đơn mua nhận hàng theo từng lô
        var pr1 = new PurchaseRequisition { Number = await req.NextNumberAsync(c.Day(2)), Date = c.Day(2), RequiredDate = c.Day(2).AddDays(10), Department = "Xưởng sản xuất", Purpose = "Vật tư bù hàng thiếu cho kế hoạch sản xuất tuần 2", Source = "Định mức sản xuất", Status = RequestStatus.Draft };
        pr1.Lines.Add(new PurchaseRequisitionLine { ItemId = ocvit.Id, Quantity = 600, EstimatedPrice = price });
        db.PurchaseRequisitions.Add(pr1); await db.SaveChangesAsync(); await req.SubmitAsync(pr1);
        var po1 = await req.ToPurchaseOrderAsync(pr1, vendors[0].Id, wNvl.Id, pr1.Lines.Select(l => l.Id));
        await purchase.SubmitAsync(po1);
        var d1 = c.Day(4);
        await purchase.ReceiveAsync(po1, d1, po1.Lines.ToDictionary(l => l.Id, l => 250m), new Dictionary<int, (string?, DateTime?)> { [ocvit.Id] = ("OV-2611-A", d1.AddDays(28)) });
        await purchase.ReceiveAsync(po1, c.Day(9), po1.Lines.ToDictionary(l => l.Id, l => 200m), new Dictionary<int, (string?, DateTime?)> { [ocvit.Id] = ("OV-2611-B", c.Today.AddDays(300)) });
        var bill = await purchase.CreateInvoiceAsync(po1, c.Day(9), "1C26TNV", "0004521");
        await c.S<InvoiceService>().IssueAsync(bill); await db.SaveChangesAsync();
        // Chi phí vận chuyển có hóa đơn riêng, phân bổ vào giá nhập (tăng giá vốn các lô còn tồn)
        await landed.CreateAsync(po1.Id, c.Day(10), LandedCostKind.Freight, "Cước vận chuyển vật tư đợt 1", 3_200_000, 8, "qty", await acc.IdAsync("331"), vendors[2].Id, "0008810", "1C26TVC");

        // Đơn mua quá hạn giao (để thấy cảnh báo trên báo cáo đối chiếu 3 bên) và lô đã hết hạn
        var po2 = new PurchaseOrder { Date = c.Day(1), VendorId = vendors[1].Id, WarehouseId = wNvl.Id, ExpectedDate = c.Today.AddDays(-9), Note = "Giao hàng chậm theo cam kết", Number = await DocNumbers.NextAsync(db.PurchaseOrders.Select(x => x.Number), "DMH", c.Day(1)) };
        po2.Lines.Add(new PurchaseOrderLine { ItemId = mdf.Id, Quantity = 120, UnitPrice = mdf.AvgCost > 0 ? mdf.AvgCost : 430_000, VatRate = mdf.VatRate });
        po2.Lines.Add(new PurchaseOrderLine { ItemId = gosoi.Id, Quantity = 60, UnitPrice = gosoi.AvgCost > 0 ? gosoi.AvgCost : 180_000, VatRate = gosoi.VatRate });
        PurchaseService.Recalc(po2); db.PurchaseOrders.Add(po2); await db.SaveChangesAsync();
        await purchase.SubmitAsync(po2);
        await purchase.ReceiveAsync(po2, c.Day(5), po2.Lines.ToDictionary(l => l.Id, l => Math.Round(l.Quantity * 0.6m, 0)));
        var po3 = new PurchaseOrder { Date = c.Day(1), VendorId = vendors[0].Id, WarehouseId = wNvl.Id, ExpectedDate = c.Day(3), Note = "Lô cũ còn tồn – hết hạn", Number = await DocNumbers.NextAsync(db.PurchaseOrders.Select(x => x.Number), "DMH", c.Day(1)) };
        po3.Lines.Add(new PurchaseOrderLine { ItemId = ocvit.Id, Quantity = 40, UnitPrice = price, VatRate = ocvit.VatRate });
        PurchaseService.Recalc(po3); db.PurchaseOrders.Add(po3); await db.SaveChangesAsync();
        await purchase.SubmitAsync(po3);
        await purchase.ReceiveAsync(po3, c.Day(2), po3.Lines.ToDictionary(l => l.Id, l => 40m), new Dictionary<int, (string?, DateTime?)> { [ocvit.Id] = ("OV-2510-OLD", c.Today.AddDays(-12)) });
        var bill3 = await purchase.CreateInvoiceAsync(po3, c.Day(3), "1C26TNV", "0004390");
        await c.S<InvoiceService>().IssueAsync(bill3);

        // Đề nghị mua chờ duyệt (giá trị lớn), đề nghị từ tồn tối thiểu, và RFQ so sánh giá
        var prBig = new PurchaseRequisition { Number = await req.NextNumberAsync(c.Day(12)), Date = c.Day(12), RequiredDate = c.Day(12).AddDays(14), Department = "Phòng Kinh doanh", Purpose = "Vật tư cho đơn hàng dự án trường học Q4", Source = "Thủ công" };
        prBig.Lines.Add(new PurchaseRequisitionLine { ItemId = mdf.Id, Quantity = 200, EstimatedPrice = mdf.AvgCost > 0 ? mdf.AvgCost : 430_000 });
        db.PurchaseRequisitions.Add(prBig); await db.SaveChangesAsync(); await req.SubmitAsync(prBig);
        await req.FromLowStockAsync(c.Day(14), null);
        var pr4 = new PurchaseRequisition { Number = await req.NextNumberAsync(c.Day(15)), Date = c.Day(15), RequiredDate = c.Day(15).AddDays(7), Department = "Kho vận", Purpose = "Gỗ sồi bổ sung – so sánh báo giá", Source = "Thủ công" };
        pr4.Lines.Add(new PurchaseRequisitionLine { ItemId = gosoi.Id, Quantity = 30, EstimatedPrice = gosoi.AvgCost > 0 ? gosoi.AvgCost : 180_000 });
        db.PurchaseRequisitions.Add(pr4); await db.SaveChangesAsync(); await req.SubmitAsync(pr4);
        if (pr4.Status == RequestStatus.Approved)
        {
            var rfq = await req.ToRfqAsync(pr4, vendors.Select(v => v.Id));
            var line = rfq.Lines.First(); var basePrice = gosoi.AvgCost > 0 ? gosoi.AvgCost : 180_000; var k = 0;
            foreach (var v in rfq.Vendors) { db.RfqQuotes.Add(new RfqQuote { RfqId = rfq.Id, RfqLineId = line.Id, PartnerId = v.PartnerId, UnitPrice = Math.Round(basePrice * (1.04m - 0.03m * k), 0) }); v.Responded = true; v.LeadDays = 3 + 2 * k; v.Terms = k == 1 ? "Thanh toán 30 ngày, giao tại kho" : "Thanh toán 15 ngày"; k++; }
            await db.SaveChangesAsync();
            await db.Entry(rfq).Collection(r => r.Quotes).LoadAsync();
            await req.AwardAsync(rfq, vendors[1].Id, wNvl.Id);
        }
        // RFQ nhập tay đang chờ báo giá
        var rfq2 = new Rfq { Number = await DocNumbers.NextAsync(db.Rfqs.Select(x => x.Number), "RFQ", c.Day(16)), Date = c.Day(16), DueDate = c.Today.AddDays(4), Status = RfqStatus.Sent, Note = "Yêu cầu báo giá ray trượt giảm chấn, giao trong 10 ngày" };
        var ray = await db.Items.FirstOrDefaultAsync(i => i.Code == "NVL-RAY") ?? mdf;
        rfq2.Lines.Add(new RfqLine { ItemId = ray.Id, Quantity = 500 }); foreach (var v in vendors.Take(2)) rfq2.Vendors.Add(new RfqVendor { PartnerId = v.Id });
        db.Rfqs.Add(rfq2); await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ kho: vị trí, pallet, kiểm kê, cảnh báo
    private static async Task Warehouse(Ctx c)
    {
        var db = c.Db; if (await db.Pallets.AnyAsync()) return;
        var wNvl = await c.Wh("KNVL"); var bins = await db.BinLocations.Where(b => b.WarehouseId == wNvl.Id).OrderBy(b => b.Id).ToListAsync();
        var levels = (await db.StockLevels.Where(s => s.WarehouseId == wNvl.Id && s.Quantity > 0).ToListAsync());
        var ocvit = await c.Item("NVL-OCVIT"); var mdf = await c.Item("NVL-MDF18");
        decimal Lvl(int id) => levels.Where(l => l.ItemId == id).Sum(l => l.Quantity);
        if (bins.Count >= 3)
        {
            var q1 = Math.Min(300, Math.Floor(Lvl(ocvit.Id))); var q2 = Math.Min(80, Math.Floor(Lvl(mdf.Id)));
            var p1 = new Pallet { Code = "PLT2611-0001", WarehouseId = wNvl.Id, BinId = bins[0].Id, Note = "Ốc vít nhập đợt 1 – 2 lô" };
            var p2 = new Pallet { Code = "PLT2611-0002", WarehouseId = wNvl.Id, BinId = bins[2].Id, Note = "Ván MDF 18mm" };
            var p3 = new Pallet { Code = "PLT2611-0003", WarehouseId = wNvl.Id, Note = "Pallet trống chờ xếp hàng" };
            if (q1 > 0) { p1.Items.Add(new PalletItem { ItemId = ocvit.Id, LotNo = "OV-2611-A", Quantity = Math.Min(q1, 250) }); db.StockBins.Add(new StockBin { ItemId = ocvit.Id, WarehouseId = wNvl.Id, BinId = bins[0].Id, Quantity = Math.Min(q1, 250) }); }
            if (q2 > 0) { p2.Items.Add(new PalletItem { ItemId = mdf.Id, Quantity = q2 }); db.StockBins.Add(new StockBin { ItemId = mdf.Id, WarehouseId = wNvl.Id, BinId = bins[2].Id, Quantity = q2 }); }
            db.Pallets.AddRange(p1, p2, p3);
            // Hàng không theo pallet nhưng có vị trí
            var gosoi = await c.Item("NVL-GOSOI"); var g = Math.Min(20, Math.Floor(Lvl(gosoi.Id)));
            if (g > 0) db.StockBins.Add(new StockBin { ItemId = gosoi.Id, WarehouseId = wNvl.Id, BinId = bins[1].Id, Quantity = g });
            await db.SaveChangesAsync();
        }
        // Đợt kiểm kê: đếm lệch vài mặt hàng rồi xử lý chênh lệch
        if (!await db.Stocktakes.AnyAsync())
        {
            var svc = c.S<StocktakeService>(); var st = await svc.CreateAsync(wNvl.Id, c.Day(18), null, "Kiểm kê định kỳ kho nguyên vật liệu");
            var lines = st.Lines.Where(l => l.Item!.TrackMode == TrackMode.None).OrderBy(l => l.Item!.Code).ToList(); var k = 0;
            foreach (var l in st.Lines) l.CountedQty = null;
            foreach (var l in lines.Take(5)) { l.CountedQty = k == 1 ? Math.Max(0, l.BookQty - 3) : k == 3 ? l.BookQty + 2 : l.BookQty; if (k is 1 or 3) l.Note = k == 1 ? "Hao hụt, thiếu so sổ sách" : "Thừa chưa rõ nguyên nhân"; k++; }
            await db.SaveChangesAsync();
            await svc.PostAsync(st);
            // Một đợt đang kiểm đếm dở dang ở kho thành phẩm
            var wTp = await db.Warehouses.Where(w => w.Id != wNvl.Id).OrderBy(w => w.Id).FirstOrDefaultAsync();
            if (wTp != null) { try { await svc.CreateAsync(wTp.Id, c.Today, null, "Kiểm kê cuối tháng – đang đếm"); } catch (BusinessException) { } }
        }
        // Cảnh báo tồn: một vật tư dưới mức tối thiểu, một vật tư vượt mức tối đa
        var other = await db.Items.Where(i => i.Type == ItemType.Material && i.Code != "NVL-OCVIT" && i.Code != "NVL-MDF18").OrderBy(i => i.Code).ToListAsync();
        if (other.Count >= 2)
        {
            var onHand = await db.StockLevels.GroupBy(s => s.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q);
            other[0].MinStock = Math.Ceiling(onHand.GetValueOrDefault(other[0].Id) * 1.5m) + 5; other[0].MaxStock = other[0].MinStock * 3;
            other[1].MaxStock = Math.Max(other[1].MinStock + 1, Math.Floor(onHand.GetValueOrDefault(other[1].Id) / 2));
            await db.SaveChangesAsync();
        }
    }

    // ------------------------------------------------------------------ bán hàng: báo giá, đơn bán, hóa đơn, vận chuyển, hoa hồng
    private static async Task Sales(Ctx c)
    {
        var db = c.Db; if (await db.SalesQuotes.CountAsync() > 1) return;
        var sales = c.S<SalesService>(); var invoices = c.S<InvoiceService>(); var quotes = c.S<QuoteService>(); var pricing = c.S<PricingService>();
        var customers = await c.Customers(4); var wTp = await db.Warehouses.Where(w => w.Code != "KNVL").OrderBy(w => w.Id).FirstAsync();
        var reps = await db.Users.Where(u => u.Role == AppRoles.Sales && u.IsActive).OrderBy(u => u.Id).Take(2).ToListAsync();
        var onHand = (await db.StockLevels.Include(s => s.Item).Where(s => s.WarehouseId == wTp.Id && s.Quantity > 0 && s.Item!.Type != ItemType.Service).ToListAsync()).OrderByDescending(s => s.Quantity).ToList();
        if (onHand.Count == 0) return;
        Item FG(int k) => onHand[k % onHand.Count].Item!;
        decimal Q(int k, decimal want) => Math.Max(1, Math.Min(want, Math.Floor(onHand[k % onHand.Count].Quantity / 3)));
        async Task<SalesOrder> So(Partner cust, AppUser? rep, DateTime date, params (Item It, decimal Qty)[] ls)
        {
            var so = new SalesOrder { Date = date, CustomerId = cust.Id, WarehouseId = wTp.Id, DeliveryDate = date.AddDays(3), SalespersonId = rep?.Id, Number = await DocNumbers.NextAsync(db.SalesOrders.Select(x => x.Number), "DBH", date) };
            foreach (var (it, qty) in ls) so.Lines.Add(new SalesOrderLine { ItemId = it.Id, Quantity = qty, UnitPrice = it.SalePrice, VatRate = it.VatRate });
            SalesService.Recalc(so); db.SalesOrders.Add(so); await db.SaveChangesAsync(); return so;
        }
        // Đơn của nhân viên 1: áp bảng giá, giao đủ, phát hành hóa đơn, vận đơn đã giao
        var s1 = await So(customers[0], reps.ElementAtOrDefault(0), c.Day(5), (FG(0), Q(0, 6)), (FG(1), Q(1, 4)));
        await pricing.ApplyAsync(s1); await sales.ConfirmAsync(s1, true);
        await sales.DeliverAsync(s1, c.Day(7), s1.Lines.ToDictionary(l => l.Id, l => l.Quantity));
        var i1 = await sales.CreateInvoiceAsync(s1, c.Day(7)); await invoices.IssueAsync(i1);
        // Đơn của nhân viên 2: giao một phần, hóa đơn phần đã giao, đang vận chuyển
        var s2 = await So(customers[Math.Min(1, customers.Count - 1)], reps.ElementAtOrDefault(1) ?? reps.ElementAtOrDefault(0), c.Day(8), (FG(1), Q(1, 6)), (FG(2), Q(2, 5)));
        await pricing.ApplyAsync(s2); await sales.ConfirmAsync(s2, true);
        await sales.DeliverAsync(s2, c.Day(10), s2.Lines.ToDictionary(l => l.Id, l => Math.Max(1, Math.Floor(l.Quantity / 2))));
        var i2 = await sales.CreateInvoiceAsync(s2, c.Day(10)); await invoices.IssueAsync(i2);
        // Đơn thứ ba: xác nhận, chờ giao
        var s3 = await So(customers[Math.Min(2, customers.Count - 1)], reps.ElementAtOrDefault(0), c.Day(13), (FG(0), Q(0, 3)));
        await sales.ConfirmAsync(s3, true);
        // Đơn nháp để thử "Áp dụng bảng giá"
        await So(customers[0], reps.ElementAtOrDefault(1), c.Day(14), (FG(2), 8), (FG(0), 12));
        await db.SaveChangesAsync();
        // Trạng thái vận đơn
        var ships = await db.Shipments.Where(s => s.SalesOrderId == s1.Id || s.SalesOrderId == s2.Id).ToListAsync();
        foreach (var s in ships)
        {
            if (s.SalesOrderId == s1.Id) { s.Status = ShipmentStatus.Delivered; s.Carrier = "Giao hàng nhanh GHN"; s.VehicleNo = "51C-123.45"; s.Driver = "Nguyễn Văn Tâm"; s.TrackingNo = "GHN" + c.Day(7).ToString("yyMMdd") + "01"; s.ShippedAt = c.Day(7).AddHours(8); s.DeliveredAt = c.Day(8).AddHours(15); s.Freight = 1_200_000; s.Receiver = "Thủ kho bên mua"; }
            else { s.Status = ShipmentStatus.InTransit; s.Carrier = "Xe tải công ty"; s.VehicleNo = "51D-678.90"; s.Driver = "Lê Quang Huy"; s.ShippedAt = c.Day(10).AddHours(9); s.Freight = 800_000; }
        }
        // Báo giá ở các trạng thái khác nhau
        var rep = reps.FirstOrDefault();
        async Task<SalesQuote> Quote(Partner cust, int day, decimal discount, decimal costRatio, decimal other, QuoteStatus status, params (Item It, decimal Qty)[] ls)
        {
            var q = new SalesQuote { Number = await DocNumbers.NextAsync(db.SalesQuotes.Select(x => x.Number), "BG", c.Day(day)), Date = c.Day(day), ValidUntil = c.Day(day).AddDays(20), CustomerId = cust.Id, WarehouseId = wTp.Id, SalespersonId = rep?.Id, OtherCost = other, Status = QuoteStatus.Draft, Note = "Giao hàng trong 7 ngày kể từ khi xác nhận" };
            foreach (var (it, qty) in ls) q.Lines.Add(new SalesQuoteLine { ItemId = it.Id, Quantity = qty, UnitPrice = it.SalePrice, DiscountPct = discount, VatRate = it.VatRate, UnitCost = Math.Round(it.SalePrice * costRatio, 0) });
            QuoteService.Recalc(q); db.SalesQuotes.Add(q); await db.SaveChangesAsync();
            if (status != QuoteStatus.Draft) { await quotes.SubmitAsync(q); if (status == QuoteStatus.Sent && q.Status == QuoteStatus.Approved) q.Status = QuoteStatus.Sent; if (status == QuoteStatus.Rejected) q.Status = QuoteStatus.Rejected; }
            await db.SaveChangesAsync(); return q;
        }
        var qSent = await Quote(customers[1 % customers.Count], 6, 3, 0.72m, 2_000_000, QuoteStatus.Sent, (FG(0), 10), (FG(1), 6));
        var qLow = await Quote(customers[0], 11, 15, 0.86m, 4_500_000, QuoteStatus.PendingApproval, (FG(2), 20));      // lợi nhuận thấp → chờ Giám đốc duyệt
        var qOk = await Quote(customers[2 % customers.Count], 9, 0, 0.7m, 1_000_000, QuoteStatus.Approved, (FG(1), 5));
        await Quote(customers[0], 3, 8, 0.8m, 0, QuoteStatus.Rejected, (FG(0), 4));
        await Quote(customers[1 % customers.Count], 15, 2, 0.7m, 500_000, QuoteStatus.Draft, (FG(2), 2));
        if (qOk.Status == QuoteStatus.Approved) { var so = await quotes.ToOrderAsync(qOk, wTp.Id); await sales.ConfirmAsync(so, true); }
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ ngoại tệ: hóa đơn USD, thu tiền, chênh lệch tỷ giá, đánh giá lại
    private static async Task Fx(Ctx c)
    {
        var db = c.Db; if (await db.Invoices.AnyAsync(i => i.CurrencyCode == "USD")) return;
        var invoices = c.S<InvoiceService>(); var cash = c.S<CashService>(); var fx = c.S<FxService>();
        var cust = await db.Partners.FirstOrDefaultAsync(p => p.Code == "KH-USD") ?? db.Partners.Add(new Partner { Code = "KH-USD", Name = "Oceanic Furniture Trading LLC (Mỹ)", TaxCode = null, IsCustomer = true, Address = "Los Angeles, USA", PartnerGroup = "Dự án", PaymentTermDays = 45, PaymentCondition = "T/T 45 ngày, thanh toán bằng USD" }).Entity;
        await db.SaveChangesAsync();
        var usd = await db.CashAccounts.FirstOrDefaultAsync(a => a.CurrencyCode == "USD"); if (usd == null) return;
        if (!await db.ExchangeRates.AnyAsync(r => r.CurrencyCode == "USD" && r.Date == c.Day(3))) db.ExchangeRates.Add(new ExchangeRate { CurrencyCode = "USD", Date = c.Day(3), Rate = 26_250 });
        if (!await db.ExchangeRates.AnyAsync(r => r.CurrencyCode == "USD" && r.Date == c.Day(12))) db.ExchangeRates.Add(new ExchangeRate { CurrencyCode = "USD", Date = c.Day(12), Rate = 26_480 });
        if (!await db.ExchangeRates.AnyAsync(r => r.CurrencyCode == "USD" && r.Date == c.Today)) db.ExchangeRates.Add(new ExchangeRate { CurrencyCode = "USD", Date = c.Today, Rate = 26_520 });
        await db.SaveChangesAsync();
        var svc = await db.Items.FirstAsync(i => i.Type == ItemType.Service);
        var inv = new Invoice { Direction = InvoiceDirection.Sales, Date = c.Day(3), DueDate = c.Day(3).AddDays(45), PartnerId = cust.Id, CurrencyCode = "USD", ExchangeRate = 26_250, Series = "1C26TAA", Note = "Dịch vụ thiết kế & tư vấn bố trí nội thất – hợp đồng xuất khẩu dịch vụ", Number = await invoices.NextNumberAsync(InvoiceDirection.Sales, c.Day(3)) };
        inv.Lines.Add(new InvoiceLine { ItemId = svc.Id, Description = "Thiết kế bố trí nội thất showroom (giai đoạn 1)", Unit = "Gói", Quantity = 1, FcUnitPrice = 3_000, VatRate = 0 });
        inv.Lines.Add(new InvoiceLine { ItemId = svc.Id, Description = "Tư vấn vật liệu & giám sát lắp đặt từ xa", Unit = "Gói", Quantity = 2, FcUnitPrice = 750, VatRate = 0 });
        InvoiceService.Recalc(inv); await invoices.IssueAsync(inv); await db.SaveChangesAsync();
        // Khách trả một phần bằng USD ở tỷ giá cao hơn → lãi chênh lệch tỷ giá đã thực hiện
        await cash.CreateAsync(new CashVoucherForm { Type = CashVoucherType.Receipt, Date = c.Day(12), CashAccountId = usd.Id, Purpose = CashPurpose.CustomerReceipt, PartnerId = cust.Id, InvoiceId = inv.Id, Amount = 2_000, ExchangeRate = 26_480, Description = "Khách thanh toán đợt 1 hóa đơn USD" });
        // Đánh giá lại số dư ngoại tệ cuối kỳ (tự lập bút toán đảo ngày hôm sau)
        try { await fx.RevalueAsync(c.Today); } catch (BusinessException) { }
    }

    // ------------------------------------------------------------------ bù trừ công nợ & lịch thanh toán
    private static async Task Offsets(Ctx c)
    {
        var db = c.Db; if (await db.DebtOffsets.AnyAsync()) return;
        var invoices = c.S<InvoiceService>(); var acc = c.S<AccountingService>(); var off = c.S<OffsetService>();
        var p = await db.Partners.FirstOrDefaultAsync(x => x.Code == "KH-NH") ?? db.Partners.Add(new Partner { Code = "KH-NH", Name = "Công ty TNHH Xây dựng & Nội thất Bạn Hàng", TaxCode = "0313456789", IsCustomer = true, IsVendor = true, Address = "Quận 7, TP. Hồ Chí Minh", PartnerGroup = "Đại lý", PaymentTermDays = 30 }).Entity;
        await db.SaveChangesAsync();
        var svc = await db.Items.Where(i => i.Type == ItemType.Service).OrderBy(i => i.Code).FirstAsync();
        var sale = new Invoice { Direction = InvoiceDirection.Sales, Date = c.Day(6), DueDate = c.Day(6).AddDays(30), PartnerId = p.Id, Series = "1C26TAA", Number = await invoices.NextNumberAsync(InvoiceDirection.Sales, c.Day(6)), Note = "Dịch vụ lắp đặt cho Bạn Hàng" };
        sale.Lines.Add(new InvoiceLine { ItemId = svc.Id, Description = "Dịch vụ lắp đặt nội thất tại công trình", Unit = "Lần", Quantity = 1, UnitPrice = 30_000_000, VatRate = 8 });
        InvoiceService.Recalc(sale); await invoices.IssueAsync(sale);
        var buy = new Invoice { Direction = InvoiceDirection.Purchase, Date = c.Day(7), DueDate = c.Day(7).AddDays(30), PartnerId = p.Id, Series = "1C26TBH", InvoiceNo = "0000317", Number = await invoices.NextNumberAsync(InvoiceDirection.Purchase, c.Day(7)), Note = "Thuê gia công sơn PU của Bạn Hàng" };
        buy.Lines.Add(new InvoiceLine { Description = "Gia công sơn PU bề mặt", Unit = "Lô", Quantity = 1, UnitPrice = 18_000_000, VatRate = 8, AccountId = await acc.IdAsync("6427") });
        InvoiceService.Recalc(buy); await invoices.IssueAsync(buy); await db.SaveChangesAsync();
        await off.CreateAsync(p.Id, c.Day(20), null, "Biên bản đối chiếu và bù trừ công nợ tháng này");
        // Lịch thanh toán: ngày dự kiến trả cho các hóa đơn mua còn nợ
        var open = await db.Invoices.Where(i => i.Direction == InvoiceDirection.Purchase && i.Status == InvoiceStatus.Issued && i.PaidAmount < i.Total).OrderBy(i => i.DueDate).Take(6).ToListAsync();
        var k = 0; foreach (var i in open) { i.PlannedPayDate = c.Today.AddDays(3 + 6 * k++); }
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ CCDC, TSCĐ điều chuyển, bút toán định kỳ & phân bổ
    private static async Task ToolsAndRecurring(Ctx c)
    {
        var db = c.Db; if (await db.RecurringJournals.AnyAsync()) return;
        var invoices = c.S<InvoiceService>(); var acc = c.S<AccountingService>(); var tools = c.S<ToolService>(); var rec = c.S<RecurringService>();
        var vendor = (await c.Vendors(2)).Last();
        // Hóa đơn mua trả trước: bảo hiểm 12 tháng (242) và dụng cụ xưởng (242)
        var a242 = await acc.IdAsync("242");
        var inv = new Invoice { Direction = InvoiceDirection.Purchase, Date = c.Day(2), DueDate = c.Day(2).AddDays(30), PartnerId = vendor.Id, Series = "1C26TBV", InvoiceNo = "0002288", Number = await invoices.NextNumberAsync(InvoiceDirection.Purchase, c.Day(2)), Note = "Bảo hiểm cháy nổ và dụng cụ xưởng" };
        inv.Lines.Add(new InvoiceLine { Description = "Bảo hiểm cháy nổ bắt buộc 12 tháng", Unit = "Hợp đồng", Quantity = 1, UnitPrice = 36_000_000, VatRate = 10, AccountId = a242 });
        inv.Lines.Add(new InvoiceLine { Description = "Bộ dụng cụ cầm tay, máy khoan, máy chà nhám cho xưởng", Unit = "Lô", Quantity = 1, UnitPrice = 24_000_000, VatRate = 10, AccountId = a242 });
        InvoiceService.Recalc(inv); await invoices.IssueAsync(inv); await db.SaveChangesAsync();
        // CCDC: đã hạch toán 242 trên hóa đơn → chỉ khai báo thẻ phân bổ
        var a627 = await acc.IdAsync("6273"); var a6423 = await acc.IdAsync("6423");
        foreach (var (code, name, dept, cost, months, exp) in new[] { ("CCDC001", "Bộ dụng cụ cầm tay xưởng mộc", "Xưởng sản xuất", 14_000_000m, 12, a627), ("CCDC002", "Máy khoan, máy chà nhám cầm tay", "Xưởng sản xuất", 10_000_000m, 24, a627), ("CCDC003", "Bàn ghế, tủ hồ sơ văn phòng kế toán", "Phòng Kế toán", 9_600_000m, 24, a6423) })
            if (!await db.ToolItems.AnyAsync(t => t.Code == code)) await tools.RegisterAsync(new ToolItem { Code = code, Name = name, Department = dept, Quantity = 1, Cost = cost, Months = months, StartDate = c.Day(2), ExpenseAccountId = exp, Note = "Theo hóa đơn 0002288" }, null, null);
        try { await tools.RunAsync(c.Today.Year, c.Today.Month); } catch (BusinessException) { }
        // Điều chuyển một TSCĐ sang bộ phận khác
        var asset = await db.FixedAssets.Where(a => a.Status == AssetStatus.InUse).OrderBy(a => a.Code).FirstOrDefaultAsync();
        if (asset != null) { var dept = asset.Department == "Phòng Kinh doanh" ? "Phòng Kế toán" : "Phòng Kinh doanh"; await tools.TransferAssetAsync(asset, c.Day(15), dept, null, "Bàn giao theo nhu cầu sử dụng"); }
        // Bút toán định kỳ (đến hạn hôm nay → tự sinh) và bút toán phân bổ
        db.RecurringJournals.Add(new RecurringJournal
        {
            Name = "Phân bổ phí bảo hiểm hàng tháng", Description = "Phân bổ bảo hiểm cháy nổ trả trước", IntervalMonths = 1, DayOfMonth = Math.Min(28, c.Today.Day), NextDate = c.Today, AutoPost = true,
            Lines = { new RecurringJournalLine { AccountId = await acc.IdAsync("6427"), Debit = 3_000_000, Department = "Phòng Kế toán" }, new RecurringJournalLine { AccountId = a242, Credit = 3_000_000 } }
        });
        db.RecurringJournals.Add(new RecurringJournal
        {
            Name = "Phân bổ chi phí điện nước cho các bộ phận", Description = "Chia chi phí dịch vụ mua ngoài theo tỷ lệ sử dụng", IntervalMonths = 1, DayOfMonth = 28, NextDate = new DateTime(c.Today.Year, c.Today.Month, 1).AddMonths(1).AddDays(27), AutoPost = false, IsAllocation = true,
            SourceAccountId = await acc.IdAsync("6428"),
            Lines = { new RecurringJournalLine { AccountId = await acc.IdAsync("6277"), Percent = 60, Department = "Xưởng sản xuất" }, new RecurringJournalLine { AccountId = await acc.IdAsync("6417"), Percent = 15, Department = "Phòng Kinh doanh" }, new RecurringJournalLine { AccountId = await acc.IdAsync("6421"), Percent = 25, Department = "Phòng Kế toán" } }
        });
        await db.SaveChangesAsync();
        await rec.GenerateDueAsync(c.Today);
    }

    // ------------------------------------------------------------------ sao kê ngân hàng
    private static async Task Bank(Ctx c)
    {
        var db = c.Db; if (await db.BankStatements.AnyAsync()) return;
        var bank = c.S<BankService>();
        var vcb = await db.CashAccounts.FirstOrDefaultAsync(a => a.Code == "VCB"); if (vcb == null) return;
        var from = c.Today.AddDays(-25);
        var vs = await db.CashVouchers.Where(v => v.Status == DocStatus.Posted && (v.CashAccountId == vcb.Id || v.ToCashAccountId == vcb.Id) && v.Date >= from).OrderBy(v => v.Date).Take(8).ToListAsync();
        var rows = vs.Select(v => new BankService.StatementRow(v.Date, $"{(v.Type == CashVoucherType.Receipt ? "CK từ" : "CK đến")} {v.PayerPayee} {v.Number}", "FT" + v.Id.ToString("00000000"), BankService.Signed(v, vcb.Id, false))).ToList();
        rows.Add(new(c.Day(11), "Phí quản lý tài khoản tháng", "FEE" + c.Today.ToString("yyMM"), -55_000));
        rows.Add(new(c.Day(12), "Lãi tiền gửi không kỳ hạn", "INT" + c.Today.ToString("yyMM"), 142_000));
        rows.Add(new(c.Day(13), "CK khach hang thanh toan HD chua xac dinh", "FT99120013", 25_000_000));
        var first = rows.Min(r => r.Date);
        var opening = await bank.BookBalanceAsync(vcb, first.AddDays(-1));
        var st = await bank.ImportAsync(vcb.Id, "", opening, null, rows, "Dữ liệu mẫu (internet banking)");
        var interest = st.Lines.FirstOrDefault(l => l.Description.StartsWith("Lãi tiền gửi") && l.CashVoucherId == null);
        if (interest != null) await bank.CreateVoucherAsync(interest.Id, CashPurpose.Other, null, await c.S<AccountingService>().IdAsync("515"));
    }

    // ------------------------------------------------------------------ ngân sách
    private static async Task Budgets(Ctx c)
    {
        var db = c.Db; if (await db.Budgets.AnyAsync()) return;
        var b = new Budget { Year = c.Today.Year, Name = $"Ngân sách chi phí hoạt động {c.Today.Year}", Control = BudgetControl.Warn };
        void L(string prefix, string? dept, decimal perMonth, string note, decimal? hot = null) { var l = new BudgetLine { AccountPrefix = prefix, Department = dept, Note = note }; for (var m = 1; m <= 12; m++) l.SetMonth(m, hot != null && m == c.Today.Month ? hot.Value : perMonth); b.Lines.Add(l); }
        L("641", "Phòng Kinh doanh", 35_000_000, "Chi phí bán hàng"); L("6421", null, 70_000_000, "Chi phí nhân viên quản lý");
        L("6427", null, 12_000_000, "Dịch vụ mua ngoài", 8_000_000); L("627", "Xưởng sản xuất", 55_000_000, "Chi phí sản xuất chung"); L("6428", null, 6_000_000, "Chi phí bằng tiền khác");
        db.Budgets.Add(b);
        db.Budgets.Add(new Budget { Year = c.Today.Year, Name = "Ngân sách khối văn phòng (chặn khi vượt)", Control = BudgetControl.Block, IsActive = false, Lines = { new BudgetLine { AccountPrefix = "6423", Department = "Phòng Kế toán", M01 = 2_000_000, M02 = 2_000_000, M03 = 2_000_000, M04 = 2_000_000, M05 = 2_000_000, M06 = 2_000_000, M07 = 2_000_000, M08 = 2_000_000, M09 = 2_000_000, M10 = 2_000_000, M11 = 2_000_000, M12 = 2_000_000 } } });
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ lương
    private static async Task Payroll(Ctx c)
    {
        var db = c.Db; if (await db.PayrollRuns.AnyAsync()) return;
        var svc = c.S<PayrollService>(); var prev = c.Today.AddMonths(-1);
        // Tháng trước: hạch toán thẳng nếu kỳ còn mở; nếu đã khóa sổ thì chỉ lập bảng nháp
        try
        {
            var r0 = await svc.CreateAsync(prev.Year, prev.Month, null);
            try { await svc.PostAsync(r0); } catch (BusinessException) { }
        }
        catch (BusinessException) { }
        var r1 = await svc.CreateAsync(c.Today.Year, c.Today.Month, null);
        try { await svc.SubmitAsync(r1); } catch (PendingApprovalException) { }
    }

    // ------------------------------------------------------------------ thuế
    private static async Task Tax(Ctx c)
    {
        var db = c.Db; if (await db.VatDeclarations.AnyAsync()) return;
        var tax = c.S<TaxService>(); var prev = c.Today.AddMonths(-1);
        var d = await tax.ComputeAsync(prev.Year, prev.Month); await tax.SaveAsync(d, false);
        var q = (c.Today.Month - 1) / 3 + 1;
        var cit = await tax.ComputeCitAsync(c.Today.Year, q > 1 ? q - 1 : 1, 15_000_000, 0, 0); await tax.SaveCitAsync(cit, false);
        // Bảng kê hóa đơn tải từ cổng thuế: khớp phần lớn, 1 hóa đơn lệch tiền, 1 hóa đơn chưa ghi sổ
        if (!await db.TaxPortalInvoices.AnyAsync())
        {
            var from = new DateTime(c.Today.Year, c.Today.Month, 1).AddMonths(-1); var to = from.AddMonths(1).AddDays(-1);
            var mine = await db.Invoices.Include(i => i.Partner).Where(i => i.Status == InvoiceStatus.Issued && i.Date >= from && i.Date <= to && i.Series != "DK" && i.Series != "TSCD" && i.InvoiceNo != null).OrderBy(i => i.Date).Take(8).ToListAsync();
            var k = 0;
            foreach (var i in mine) db.TaxPortalInvoices.Add(new TaxPortalInvoice { Direction = i.Direction, Series = i.Series, InvoiceNo = i.InvoiceNo!, Date = i.Date, PartnerTaxCode = i.Partner?.TaxCode, PartnerName = i.Partner?.Name, Subtotal = k == 2 ? i.Subtotal + 150_000 : i.Subtotal, VatAmount = k == 2 ? i.VatAmount + 12_000 : i.VatAmount, ImportedAt = DateTime.Now }) ;
            db.TaxPortalInvoices.Add(new TaxPortalInvoice { Direction = InvoiceDirection.Purchase, Series = "1C26TXX", InvoiceNo = "0009917", Date = from.AddDays(10), PartnerTaxCode = "0100112233", PartnerName = "Công ty CP Vận tải Đường Việt", Subtotal = 4_500_000, VatAmount = 360_000 });
            await db.SaveChangesAsync();
        }
    }

    // ------------------------------------------------------------------ thiết bị cân, chấm công
    private static async Task Devices(Ctx c)
    {
        var db = c.Db; if (await db.ScaleTickets.AnyAsync()) return;
        var ocvit = await db.Items.FirstOrDefaultAsync(i => i.Code == "NVL-OCVIT"); var mdf = await db.Items.FirstOrDefaultAsync(i => i.Code == "NVL-MDF18");
        var vendors = await c.Vendors(2); var k = 0;
        foreach (var (item, gross, tare, dir) in new[] { (mdf, 12_400m, 4_100m, "IN"), (mdf, 11_950m, 4_100m, "IN"), (ocvit, 2_850m, 1_900m, "IN"), (mdf, 9_800m, 4_300m, "OUT"), (ocvit, 2_300m, 1_900m, "OUT") })
            db.ScaleTickets.Add(new ScaleTicket { TicketNo = $"CAN{c.Today:yyMM}-{++k:000}", WeighedAt = c.Today.AddDays(-k).AddHours(8 + k), VehicleNo = $"51C-{100 + k * 7}.{20 + k}", ItemCode = item?.Code, ItemId = item?.Id, PartnerCode = vendors[k % vendors.Count].Code, Direction = dir, GrossKg = gross, TareKg = tare, NetKg = gross - tare, Device = "Cân điện tử kho NVL", Reference = k == 1 ? "DMH-theo dõi" : null });
        await db.SaveChangesAsync();
    }
}
