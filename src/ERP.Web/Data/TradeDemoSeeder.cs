using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>Dữ liệu nền cho mua hàng – bán hàng – kho nâng cao: nhóm hàng, vị trí kho, bảng giá, khuyến mại, hoa hồng, đánh giá NCC, báo giá mẫu. Không phát sinh bút toán.</summary>
public static class TradeDemoSeeder
{
    public static async Task SeedAsync(ErpDbContext db)
    {
        if (await db.CommissionTiers.AnyAsync()) return;
        static decimal R(decimal v) => Math.Round(v / 1000m, 0) * 1000m;

        // ---- Nhóm hàng
        if (!await db.ItemGroups.AnyAsync())
        {
            db.ItemGroups.AddRange(new ItemGroup { Code = "NVL", Name = "Nguyên vật liệu" }, new ItemGroup { Code = "TP", Name = "Thành phẩm" }, new ItemGroup { Code = "HH", Name = "Hàng hóa" }, new ItemGroup { Code = "DV", Name = "Dịch vụ" });
            await db.SaveChangesAsync();
        }
        var gl = await db.ItemGroups.ToDictionaryAsync(g => g.Code);
        var groups = new Dictionary<ItemType, ItemGroup> { [ItemType.Material] = gl["NVL"], [ItemType.FinishedGood] = gl["TP"], [ItemType.Merchandise] = gl["HH"], [ItemType.Service] = gl["DV"] };
        var items = await db.Items.ToListAsync();
        foreach (var i in items) { i.GroupId ??= groups[i.Type].Id; if (i.Type == ItemType.Material && i.MinStock > 0 && i.MaxStock == 0) i.MaxStock = i.MinStock * 5; }

        // ---- Vị trí lưu kho
        var wh = await db.Warehouses.OrderBy(w => w.Id).FirstOrDefaultAsync();
        if (wh != null && !await db.BinLocations.AnyAsync())
            db.BinLocations.AddRange(new BinLocation { WarehouseId = wh.Id, Code = "A-01-01", Name = "Kệ A, dãy 1, tầng 1", Kind = "Kệ" }, new BinLocation { WarehouseId = wh.Id, Code = "A-01-02", Name = "Kệ A, dãy 1, tầng 2", Kind = "Kệ" },
                new BinLocation { WarehouseId = wh.Id, Code = "B-02-01", Name = "Kệ B, dãy 2", Kind = "Kệ" }, new BinLocation { WarehouseId = wh.Id, Code = "PLT-001", Name = "Pallet nhận hàng", Kind = "Pallet" });

        // ---- Nhóm khách hàng
        var customers = await db.Partners.Where(p => p.IsCustomer && p.PartnerGroup == null).OrderBy(p => p.Code).ToListAsync();
        var names = new[] { "Đại lý", "Dự án", "Bán lẻ" };
        for (var k = 0; k < customers.Count; k++) { customers[k].PartnerGroup = names[k % 3]; customers[k].PaymentCondition ??= $"Thanh toán trong {customers[k].PaymentTermDays} ngày kể từ ngày hóa đơn"; }
        foreach (var v in await db.Partners.Where(p => p.IsVendor && p.PartnerGroup == null && !p.IsCustomer).ToListAsync()) v.PartnerGroup = "Nhà cung cấp";

        // ---- Bảng giá & khuyến mại
        var fg = items.Where(i => i.Type == ItemType.FinishedGood && i.SalePrice > 0).Take(4).ToList();
        if (fg.Count > 0)
        {
            var agent = new PriceList { Code = "BG-DAILY", Name = "Bảng giá đại lý", PartnerGroup = "Đại lý", Priority = 10, ValidFrom = new DateTime(DateTime.Today.Year, 1, 1) };
            foreach (var i in fg) { agent.Lines.Add(new PriceListLine { ItemId = i.Id, MinQty = 1, Price = R(i.SalePrice * 0.92m) }); agent.Lines.Add(new PriceListLine { ItemId = i.Id, MinQty = 5, Price = R(i.SalePrice * 0.88m) }); }
            var project = new PriceList { Code = "BG-DUAN", Name = "Bảng giá dự án", PartnerGroup = "Dự án", Priority = 10, ValidFrom = new DateTime(DateTime.Today.Year, 1, 1) };
            foreach (var i in fg) project.Lines.Add(new PriceListLine { ItemId = i.Id, MinQty = 1, Price = R(i.SalePrice * 0.9m), DiscountPct = 2 });
            db.PriceLists.AddRange(agent, project);
        }
        db.Promotions.Add(new Promotion { Name = "Mua từ 10 sản phẩm giảm thêm 3%", MinQty = 10, DiscountPct = 3, ValidFrom = new DateTime(DateTime.Today.Year, 1, 1), ValidTo = new DateTime(DateTime.Today.Year, 12, 31) });

        // ---- Hoa hồng & chỉ tiêu
        db.CommissionTiers.AddRange(new CommissionTier { Name = "Bậc 1: mọi doanh số", MinRevenue = 0, RatePct = 1 }, new CommissionTier { Name = "Bậc 2: từ 200 triệu", MinRevenue = 200_000_000, RatePct = 1.5m }, new CommissionTier { Name = "Bậc 3: từ 500 triệu", MinRevenue = 500_000_000, RatePct = 2 });
        foreach (var u in await db.Users.Where(u => u.Role == AppRoles.Sales && u.IsActive).ToListAsync())
            db.SalesTargets.Add(new SalesTarget { UserId = u.Id, Year = DateTime.Today.Year, Month = DateTime.Today.Month, Amount = 300_000_000 });
        // Đơn bán cũ chưa có người phụ trách: gán người lập đơn
        await db.Database.ExecuteSqlRawAsync("UPDATE SalesOrders SET SalespersonId = CreatedById WHERE SalespersonId IS NULL AND CreatedById IS NOT NULL");

        // ---- Đánh giá nhà cung cấp mẫu
        var vendors = await db.Partners.Where(p => p.IsVendor && p.IsActive).OrderBy(p => p.Code).Take(3).ToListAsync();
        var scores = new[] { (9m, 8.5m, 7.5m, 8m), (7m, 6.5m, 8m, 7m), (5m, 5.5m, 6m, 6m) };
        for (var k = 0; k < vendors.Count; k++)
        {
            var (q, d, p, s) = scores[k]; var total = Math.Round(q * 0.35m + d * 0.30m + p * 0.20m + s * 0.15m, 2);
            db.SupplierEvaluations.Add(new SupplierEvaluation { PartnerId = vendors[k].Id, Period = $"Quý {(DateTime.Today.Month - 1) / 3 + 1}/{DateTime.Today.Year}", Quality = q, Delivery = d, Price = p, Service = s, Total = total, Grade = SupplierService.GradeOf(total), EvaluatedBy = "Dữ liệu mẫu", Note = "Đánh giá mẫu" });
            vendors[k].Grade = SupplierService.GradeOf(total);
        }
        await db.SaveChangesAsync();

        // ---- Báo giá mẫu
        var cust = customers.FirstOrDefault(); var sales = await db.Users.FirstOrDefaultAsync(u => u.Role == AppRoles.Sales);
        if (cust != null && fg.Count >= 2 && !await db.SalesQuotes.AnyAsync())
        {
            var q = new SalesQuote { Number = await DocNumbers.NextAsync(db.SalesQuotes.Select(x => x.Number), "BG", DateTime.Today), CustomerId = cust.Id, SalespersonId = sales?.Id, Status = QuoteStatus.Approved, OtherCost = 3_000_000, Note = "Giao hàng trong 7 ngày, thanh toán 30% khi đặt hàng" };
            foreach (var i in fg.Take(2)) q.Lines.Add(new SalesQuoteLine { ItemId = i.Id, Quantity = 4, UnitPrice = i.SalePrice, DiscountPct = 5, VatRate = i.VatRate, UnitCost = i.AvgCost > 0 ? i.AvgCost : Math.Round(i.SalePrice * 0.7m, 0) });
            QuoteService.Recalc(q);
            db.SalesQuotes.Add(q); await db.SaveChangesAsync();
        }
    }
}
