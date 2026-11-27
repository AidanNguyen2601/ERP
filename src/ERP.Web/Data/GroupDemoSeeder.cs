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
}
