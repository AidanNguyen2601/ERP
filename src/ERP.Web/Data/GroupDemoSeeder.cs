using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu tập đoàn để minh họa hợp nhất: công ty con miền Bắc (chi nhánh Hà Nội), giao dịch nội bộ
/// (công ty mẹ cung cấp dịch vụ thiết kế cho công ty con) và doanh thu bên ngoài của công ty con. Chỉ chạy một lần.
/// </summary>
public static class GroupDemoSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (await db.Companies.AnyAsync(c => c.Code == "CTY02")) return;
        var parent = await db.Companies.Include(c => c.Branches).FirstOrDefaultAsync(c => c.IsParent);
        var hq = parent?.Branches.OrderBy(b => b.Id).FirstOrDefault();
        var a6427 = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "6427" && a.IsPostable);
        var extCustomer = await db.Partners.Where(p => p.IsCustomer && p.IsActive && p.GroupCompanyId == null).OrderBy(p => p.Id).FirstOrDefaultAsync();
        if (parent == null || hq == null || a6427 == null || extCustomer == null) return;

        var sub = new Company { Code = "CTY02", Name = "Công ty TNHH Nội thất Demo Miền Bắc", TaxCode = "0109876543", Address = "Cầu Giấy, Hà Nội", OwnershipPct = 100 };
        sub.Branches.Add(new Branch { Code = "HN", Name = "Công ty con – Hà Nội", Address = "Cầu Giấy, Hà Nội" });
        db.Companies.Add(sub);
        var toSub = new Partner { Code = "NB-HN", Name = sub.Name, TaxCode = sub.TaxCode, Address = sub.Address, IsCustomer = true, PaymentTermDays = 45, GroupCompany = sub };
        var toParent = new Partner { Code = "NB-HCM", Name = parent.Name, TaxCode = parent.TaxCode, Address = parent.Address, IsVendor = true, PaymentTermDays = 45, GroupCompanyId = parent.Id };
        db.Partners.AddRange(toSub, toParent);
        await db.SaveChangesAsync();
        var hn = sub.Branches.First();

        var invoices = sp.GetRequiredService<InvoiceService>();
        var date = DateTime.Today;
        var prev = db.DefaultBranchId;
        try
        {
            // Công ty mẹ xuất hóa đơn dịch vụ cho công ty con
            db.DefaultBranchId = hq.Id;
            var s = new Invoice { Direction = InvoiceDirection.Sales, Number = await invoices.NextNumberAsync(InvoiceDirection.Sales, date), Date = date, DueDate = date.AddDays(45), PartnerId = toSub.Id, BranchId = hq.Id,
                Note = "Giao dịch nội bộ tập đoàn (dữ liệu mẫu hợp nhất)" };
            s.Lines.Add(new InvoiceLine { Description = "Dịch vụ thiết kế mẫu & quản lý thương hiệu quý này", Unit = "Gói", Quantity = 1, UnitPrice = 120_000_000, VatRate = 10 });
            InvoiceService.Recalc(s);
            await invoices.IssueAsync(s);
            await db.SaveChangesAsync();

            // Công ty con ghi nhận hóa đơn mua dịch vụ từ công ty mẹ và doanh thu bán ra bên ngoài
            db.DefaultBranchId = hn.Id;
            var p = new Invoice { Direction = InvoiceDirection.Purchase, Number = await invoices.NextNumberAsync(InvoiceDirection.Purchase, date), Date = date, DueDate = date.AddDays(45), PartnerId = toParent.Id, BranchId = hn.Id,
                Series = s.Series, InvoiceNo = s.InvoiceNo, Note = "Giao dịch nội bộ tập đoàn (dữ liệu mẫu hợp nhất)" };
            p.Lines.Add(new InvoiceLine { Description = "Dịch vụ thiết kế mẫu & quản lý thương hiệu", Unit = "Gói", Quantity = 1, UnitPrice = 120_000_000, VatRate = 10, AccountId = a6427.Id });
            InvoiceService.Recalc(p);
            await invoices.IssueAsync(p);
            await db.SaveChangesAsync();

            var e = new Invoice { Direction = InvoiceDirection.Sales, Number = await invoices.NextNumberAsync(InvoiceDirection.Sales, date), Date = date, DueDate = date.AddDays(30), PartnerId = extCustomer.Id, BranchId = hn.Id,
                Series = "1C26THN", Note = "Doanh thu dịch vụ của công ty con (dữ liệu mẫu hợp nhất)" };
            e.Lines.Add(new InvoiceLine { Description = "Dịch vụ lắp đặt, bảo trì nội thất khu vực miền Bắc", Unit = "Gói", Quantity = 1, UnitPrice = 280_000_000, VatRate = 10 });
            InvoiceService.Recalc(e);
            await invoices.IssueAsync(e);
            await db.SaveChangesAsync();
            log.LogInformation("Đã tạo dữ liệu mẫu tập đoàn: công ty con {Code}, hóa đơn nội bộ {Inv}.", sub.Code, s.InvoiceNo);
        }
        catch (Exception ex) { log.LogWarning(ex, "Không tạo được giao dịch mẫu tập đoàn"); }
        finally { db.DefaultBranchId = prev; }
    }

    /// <summary>
    /// Dữ liệu mẫu bổ sung cho hợp nhất: công ty mẹ góp 80% vốn điều lệ công ty con (TK 221 ↔ 411, cổ đông khác góp 20%);
    /// công ty mẹ bán hàng tồn kho cho công ty con, công ty con mới bán ra ngoài một phần (còn lãi chưa thực hiện trong tồn kho). Chạy một lần.
    /// </summary>
    public static async Task SeedInvestmentAsync(IServiceProvider sp, ErpDbContext db, ILogger log)
    {
        if (await db.Warehouses.IgnoreQueryFilters().AnyAsync(w => w.Code == "KHN")) return;
        var sub = await db.Companies.Include(c => c.Branches).FirstOrDefaultAsync(c => c.Code == "CTY02");
        var parent = await db.Companies.Include(c => c.Branches).FirstOrDefaultAsync(c => c.IsParent);
        var toSub = await db.Partners.FirstOrDefaultAsync(p => p.Code == "NB-HN"); var toParent = await db.Partners.FirstOrDefaultAsync(p => p.Code == "NB-HCM");
        var a221 = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "221" && a.IsPostable); var a4111 = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "4111" && a.IsPostable);
        var a1121 = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "1121");
        var vcb = await db.CashAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Code == "VCB");
        if (sub == null || parent == null || toSub == null || toParent == null || a221 == null || a4111 == null || a1121 == null || vcb == null) return;
        var hn = sub.Branches.OrderBy(b => b.Id).First(); var hq = parent.Branches.OrderBy(b => b.Id).First();
        var date = DateTime.Today; var prev = db.DefaultBranchId;
        try
        {
            // Kho, tài khoản ngân hàng của công ty con
            var a11213 = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "11213");
            if (a11213 == null) { db.Accounts.Add(a11213 = new Account { Code = "11213", Name = "TGNH Vietcombank – CN Hà Nội (công ty con)", Type = AccountType.Asset }); a1121.IsPostable = false; }
            var whHn = new Warehouse { Code = "KHN", Name = "Kho hàng Hà Nội (công ty con)", Address = sub.Address, BranchId = hn.Id };
            db.Warehouses.Add(whHn); await db.SaveChangesAsync();
            var bankHn = new CashAccount { Code = "VCB-HN", Name = "Vietcombank – CN Hà Nội (CTY02)", Type = CashAccountType.Bank, AccountId = a11213.Id, BankName = "Vietcombank", BankAccountNo = "0021 0099 88776", BranchId = hn.Id };
            db.CashAccounts.Add(bankHn);
            sub.OwnershipPct = 80; sub.AcquisitionDate = date; sub.RetainedAtAcquisition = 0;
            await db.SaveChangesAsync();

            var cash = sp.GetRequiredService<CashService>();
            // Công ty mẹ góp 800 triệu (80%) – Nợ 221 (đối tượng: công ty con) / Có 112
            db.DefaultBranchId = hq.Id;
            await cash.CreateAsync(new ERP.Web.ViewModels.CashVoucherForm { Type = CashVoucherType.Payment, Date = date, CashAccountId = vcb.Id, Purpose = CashPurpose.Other, PartnerId = toSub.Id, CounterAccountId = a221.Id,
                Amount = 800_000_000, Description = "Góp vốn điều lệ vào Công ty TNHH Nội thất Demo Miền Bắc (80%)", PayerPayee = sub.Name });
            // Công ty con nhận vốn góp 1 tỷ (công ty mẹ 800 triệu, cổ đông khác 200 triệu) – Nợ 112 / Có 4111
            db.DefaultBranchId = hn.Id;
            await cash.CreateAsync(new ERP.Web.ViewModels.CashVoucherForm { Type = CashVoucherType.Receipt, Date = date, CashAccountId = bankHn.Id, Purpose = CashPurpose.Other, CounterAccountId = a4111.Id,
                Amount = 1_000_000_000, Description = "Nhận vốn góp điều lệ: công ty mẹ 800 triệu, cổ đông khác 200 triệu", PayerPayee = "Các thành viên góp vốn" });

            // Bán hàng nội bộ: công ty mẹ bán thành phẩm cho công ty con
            var hqWh = await db.Warehouses.IgnoreQueryFilters().Where(w => w.BranchId == hq.Id).Select(w => w.Id).ToListAsync();
            var stock = await db.StockLevels.IgnoreQueryFilters().Include(s => s.Item).Where(s => hqWh.Contains(s.WarehouseId) && s.Quantity >= 10 && s.Item!.Type == ItemType.FinishedGood && s.Item.SalePrice > 0)
                .OrderByDescending(s => s.Quantity).FirstOrDefaultAsync();
            if (stock != null)
            {
                var item = stock.Item!; var qty = 10m; var price = item.SalePrice;
                var sales = sp.GetRequiredService<SalesService>(); var purchase = sp.GetRequiredService<PurchaseService>(); var invoices = sp.GetRequiredService<InvoiceService>();
                db.DefaultBranchId = hq.Id;
                var so = new SalesOrder { Date = date, CustomerId = toSub.Id, WarehouseId = stock.WarehouseId, DeliveryDate = date, BranchId = hq.Id, Note = "Bán hàng nội bộ cho công ty con",
                    Number = await DocNumbers.NextAsync(db.SalesOrders.Select(x => x.Number), "DBH", date) };
                so.Lines.Add(new SalesOrderLine { ItemId = item.Id, Quantity = qty, UnitPrice = price, VatRate = item.VatRate });
                SalesService.Recalc(so); db.SalesOrders.Add(so); await db.SaveChangesAsync();
                await sales.ConfirmAsync(so, true);
                await sales.DeliverAsync(so, date, so.Lines.ToDictionary(l => l.Id, l => l.Quantity));
                var sInv = await sales.CreateInvoiceAsync(so, date); await invoices.IssueAsync(sInv); await db.SaveChangesAsync();

                // Công ty con mua vào kho Hà Nội theo đúng hóa đơn của công ty mẹ
                db.DefaultBranchId = hn.Id;
                var po = new PurchaseOrder { Date = date, VendorId = toParent.Id, WarehouseId = whHn.Id, ExpectedDate = date, BranchId = hn.Id, Note = "Mua hàng nội bộ từ công ty mẹ",
                    Number = await DocNumbers.NextAsync(db.PurchaseOrders.Select(x => x.Number), "DMH", date) };
                po.Lines.Add(new PurchaseOrderLine { ItemId = item.Id, Quantity = qty, UnitPrice = price, VatRate = item.VatRate });
                PurchaseService.Recalc(po); db.PurchaseOrders.Add(po); await db.SaveChangesAsync();
                await purchase.SubmitAsync(po);
                if (po.Status == PoStatus.PendingApproval) { po.Status = PoStatus.Approved; po.ApprovedAt = DateTime.Now; await db.SaveChangesAsync(); }
                await purchase.ReceiveAsync(po, date, po.Lines.ToDictionary(l => l.Id, l => l.Quantity));
                var pInv = await purchase.CreateInvoiceAsync(po, date, sInv.Series, sInv.InvoiceNo); await invoices.IssueAsync(pInv); await db.SaveChangesAsync();

                // Công ty con bán ra ngoài 4 / 10 sản phẩm – 6 sản phẩm còn tồn mang lãi chưa thực hiện
                var ext = await db.Partners.Where(p => p.IsCustomer && p.IsActive && !p.IsBlocked && p.GroupCompanyId == null).OrderBy(p => p.Id).FirstAsync();
                var so2 = new SalesOrder { Date = date, CustomerId = ext.Id, WarehouseId = whHn.Id, DeliveryDate = date, BranchId = hn.Id, Note = "Công ty con bán ra ngoài tập đoàn",
                    Number = await DocNumbers.NextAsync(db.SalesOrders.Select(x => x.Number), "DBH", date) };
                so2.Lines.Add(new SalesOrderLine { ItemId = item.Id, Quantity = 4, UnitPrice = Math.Round(price * 1.15m / 1000m, 0) * 1000m, VatRate = item.VatRate });
                SalesService.Recalc(so2); db.SalesOrders.Add(so2); await db.SaveChangesAsync();
                await sales.ConfirmAsync(so2, true);
                await sales.DeliverAsync(so2, date, so2.Lines.ToDictionary(l => l.Id, l => l.Quantity));
                var eInv = await sales.CreateInvoiceAsync(so2, date); eInv.Series = "1C26THN"; await invoices.IssueAsync(eInv); await db.SaveChangesAsync();
            }
            log.LogInformation("Đã tạo dữ liệu mẫu đầu tư vào công ty con (80%) và bán hàng nội bộ còn tồn kho.");
        }
        catch (Exception ex) { db.ChangeTracker.Clear(); log.LogWarning(ex, "Không tạo được dữ liệu mẫu đầu tư / bán hàng nội bộ: {Msg}", ex.Message); }
        finally { db.DefaultBranchId = prev; }
    }
}
