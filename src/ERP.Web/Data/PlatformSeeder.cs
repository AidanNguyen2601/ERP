using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;

namespace ERP.Web.Data;

/// <summary>Dữ liệu nền cho các tính năng nền tảng (chạy mỗi lần khởi động, chỉ thêm phần còn thiếu)</summary>
public static class PlatformSeeder
{
    public static async Task SeedAsync(ErpDbContext db, decimal poThreshold)
    {
        // ---------------- Công ty & chi nhánh
        if (!await db.Companies.AnyAsync())
        {
            var hq = new Company { Code = "CTY01", Name = "Công ty Cổ phần Nội thất Demo", TaxCode = "0312345678", Address = "TP. Hồ Chí Minh", IsParent = true, OwnershipPct = 100 };
            hq.Branches.Add(new Branch { Code = "HCM", Name = "Trụ sở chính – TP. Hồ Chí Minh", Address = "Tân Bình, TP. Hồ Chí Minh" });
            db.Companies.Add(hq);
            await db.SaveChangesAsync();
        }
        var hqBranch = await db.Branches.OrderBy(b => b.Id).Select(b => b.Id).FirstAsync();
        // Gán chi nhánh trụ sở cho dữ liệu cũ chưa có chi nhánh
        foreach (var t in new[] { "JournalEntries", "Invoices", "PurchaseOrders", "SalesOrders", "InventoryDocs", "CashVouchers", "ProductionOrders", "FixedAssets", "Warehouses",
                     "CashAccounts", "SalesCreditNotes", "Employees", "Contracts", "PayrollRuns", "BankStatements", "ToolItems", "DebtOffsets", "VatDeclarations", "CitDeclarations", "Users" })
            await db.Database.ExecuteSqlRawAsync($"UPDATE [{t}] SET BranchId = {{0}} WHERE BranchId IS NULL", hqBranch);

        // ---------------- Tài khoản bổ sung (lương, bảo hiểm, ngoại tệ, CCDC)
        async Task Acc(string code, string name, string parent)
        {
            if (await db.Accounts.AnyAsync(a => a.Code == code)) return;
            var p = await db.Accounts.FirstOrDefaultAsync(a => a.Code == parent);
            if (p == null) return;
            if (p.IsPostable && await db.JournalLines.IgnoreQueryFilters().AnyAsync(l => l.AccountId == p.Id)) return; // TK cha đã có phát sinh: dùng luôn TK cha
            db.Accounts.Add(new Account { Code = code, Name = name, Type = p.Type });
            p.IsPostable = false;
            await db.SaveChangesAsync();
        }
        async Task Root(string code, string name, AccountType type)
        {
            if (!await db.Accounts.AnyAsync(a => a.Code == code)) { db.Accounts.Add(new Account { Code = code, Name = name, Type = type, IsPostable = true }); await db.SaveChangesAsync(); }
        }
        await Acc("3382", "Kinh phí công đoàn", "338"); await Acc("3384", "Bảo hiểm y tế", "338"); await Acc("3386", "Bảo hiểm thất nghiệp", "338");
        await Acc("3335", "Thuế thu nhập cá nhân", "333");
        await Acc("1122", "Tiền gửi ngân hàng – ngoại tệ", "112");
        await Root("153", "Công cụ, dụng cụ", AccountType.Asset);
        await Acc("6423", "Chi phí đồ dùng văn phòng", "642");
        await Root("413", "Chênh lệch tỷ giá hối đoái", AccountType.Equity);

        // ---------------- Tiền tệ & tỷ giá (tỷ giá mẫu – cập nhật theo tỷ giá thực tế trước khi dùng)
        if (!await db.Currencies.AnyAsync())
        {
            db.Currencies.AddRange(new Currency { Code = "VND", Name = "Việt Nam đồng", Decimals = 0 }, new Currency { Code = "USD", Name = "Đô la Mỹ" },
                new Currency { Code = "EUR", Name = "Euro" }, new Currency { Code = "JPY", Name = "Yên Nhật", Decimals = 0 }, new Currency { Code = "CNY", Name = "Nhân dân tệ" });
            var d = DateTime.Today;
            db.ExchangeRates.AddRange(new ExchangeRate { CurrencyCode = "USD", Date = d, Rate = 26_300 }, new ExchangeRate { CurrencyCode = "EUR", Date = d, Rate = 30_500 },
                new ExchangeRate { CurrencyCode = "JPY", Date = d, Rate = 172 }, new ExchangeRate { CurrencyCode = "CNY", Date = d, Rate = 3_650 });
            await db.SaveChangesAsync();
        }
        if (!await db.CashAccounts.AnyAsync(c => c.CurrencyCode == "USD"))
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "1122" && a.IsPostable);
            if (acc != null)
            {
                db.CashAccounts.Add(new CashAccount { Code = "VCB-USD", Name = "Vietcombank – tài khoản USD", Type = CashAccountType.Bank, AccountId = acc.Id, BankName = "Vietcombank", BankAccountNo = "0071 3700 88888", CurrencyCode = "USD", BranchId = hqBranch });
                await db.SaveChangesAsync();
            }
        }

        // ---------------- Quy tắc đặt mã
        foreach (var (target, prefix, digits) in new[] { ("customer", "KH", 3), ("vendor", "NCC", 3), ("item.Material", "NVL", 4), ("item.FinishedGood", "TP", 4), ("item.Merchandise", "HH", 4),
                     ("item.Service", "DV", 3), ("employee", "NV", 4), ("contract.Sale", "HDB", 4), ("contract.Purchase", "HDM", 4), ("tool", "CCDC", 3) })
            if (!await db.CodeRules.AnyAsync(r => r.Target == target)) db.CodeRules.Add(new CodeRule { Target = target, Prefix = prefix, Digits = digits });

        // ---------------- Quy tắc duyệt (tương đương ngưỡng duyệt cũ + ví dụ nhiều cấp)
        if (!await db.ApprovalRules.AnyAsync())
            db.ApprovalRules.AddRange(
                new ApprovalRule { DocType = ApprovalDocType.PurchaseOrder, Name = "Ban Giám đốc duyệt đơn mua lớn", Level = 1, MinAmount = poThreshold, ApproverRole = AppRoles.Director },
                new ApprovalRule { DocType = ApprovalDocType.SalesOrder, Name = "Kế toán kiểm tra đơn bán lớn", Level = 1, MinAmount = 1_000_000_000, ApproverRole = AppRoles.Accountant },
                new ApprovalRule { DocType = ApprovalDocType.SalesOrder, Name = "Ban Giám đốc duyệt đơn bán lớn", Level = 2, MinAmount = 1_000_000_000, ApproverRole = AppRoles.Director },
                new ApprovalRule { DocType = ApprovalDocType.JournalEntry, Name = "Ban Giám đốc duyệt chứng từ kế toán lớn", Level = 1, MinAmount = 500_000_000, ApproverRole = AppRoles.Director },
                new ApprovalRule { DocType = ApprovalDocType.Payroll, Name = "Ban Giám đốc duyệt bảng lương", Level = 1, MinAmount = 0, ApproverRole = AppRoles.Director });

        // ---------------- Cài đặt mặc định
        var defaults = new Dictionary<string, string>
        {
            ["EInvoice.Enabled"] = "true", ["EInvoice.Provider"] = "demo", ["EInvoice.Url"] = "", ["EInvoice.Username"] = "", ["EInvoice.TemplateCode"] = "1",
            ["Budget.Enabled"] = "true",
            ["Payroll.EmpBhxh"] = "8", ["Payroll.EmpBhyt"] = "1.5", ["Payroll.EmpBhtn"] = "1", ["Payroll.ErBhxh"] = "17.5", ["Payroll.ErBhyt"] = "3", ["Payroll.ErBhtn"] = "1", ["Payroll.ErKpcd"] = "2",
            ["Payroll.PersonalDeduction"] = "15500000", ["Payroll.DependentDeduction"] = "6200000", ["Payroll.InsuranceCap"] = "46800000", ["Payroll.StandardDays"] = "26",
            ["Payroll.PitBrackets"] = "5000000:5;10000000:10;18000000:15;32000000:20;52000000:25;80000000:30;0:35",
            ["Cit.Rate"] = "20", ["Accounting.Regime"] = "TT99",
        };
        // Chuyển chế độ mặc định từ TT200 sang TT99 một lần (không đè lựa chọn khác của người dùng)
        var regime = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "Accounting.Regime");
        if (regime is { Value: "TT200" } && !await db.SystemSettings.AnyAsync(s => s.Key == "Accounting.Tt99Migrated")) { regime.Value = "TT99"; regime.UpdatedAt = DateTime.Now; }
        if (!await db.SystemSettings.AnyAsync(s => s.Key == "Accounting.Tt99Migrated")) db.SystemSettings.Add(new SystemSetting { Key = "Accounting.Tt99Migrated", Value = "true" });
        var have = (await db.SystemSettings.Select(s => s.Key).ToListAsync()).ToHashSet();
        foreach (var (k, v) in defaults.Where(x => !have.Contains(x.Key))) db.SystemSettings.Add(new SystemSetting { Key = k, Value = v });

        // ---------------- Ánh xạ hệ thống tài khoản sang chuẩn mực khác
        if (!await db.AccountMappings.AnyAsync())
        {
            (string From, string To, string Name, int Sign)[] tt133 =
            {
                ("111", "111", "Tiền mặt", 1), ("112", "112", "Tiền gửi ngân hàng", 1), ("131", "131", "Phải thu của khách hàng", 1), ("133", "133", "Thuế GTGT được khấu trừ", 1),
                ("138", "138", "Phải thu khác", 1), ("141", "141", "Tạm ứng", 1), ("152", "152", "Nguyên liệu, vật liệu", 1), ("153", "153", "Công cụ, dụng cụ", 1),
                ("154", "154", "Chi phí SXKD dở dang", 1), ("155", "155", "Thành phẩm", 1), ("156", "156", "Hàng hóa", 1), ("211", "211", "Tài sản cố định", 1),
                ("214", "214", "Hao mòn tài sản cố định", 1), ("242", "242", "Chi phí trả trước", 1), ("331", "331", "Phải trả cho người bán", 1), ("333", "333", "Thuế và các khoản phải nộp Nhà nước", 1),
                ("334", "334", "Phải trả người lao động", 1), ("338", "338", "Phải trả, phải nộp khác", 1), ("341", "341", "Vay và nợ thuê tài chính", 1), ("411", "411", "Vốn đầu tư của chủ sở hữu", 1),
                ("413", "413", "Chênh lệch tỷ giá hối đoái", 1), ("421", "421", "Lợi nhuận sau thuế chưa phân phối", 1),
                ("5111", "5111", "Doanh thu bán hàng hóa", 1), ("5112", "5112", "Doanh thu bán các thành phẩm", 1), ("5113", "5113", "Doanh thu cung cấp dịch vụ", 1),
                ("521", "511", "Doanh thu (giảm trừ ghi giảm trực tiếp 511)", 1), ("515", "515", "Doanh thu hoạt động tài chính", 1),
                ("621", "154", "Chi phí SXKD dở dang (TT133 không dùng 621)", 1), ("622", "154", "Chi phí SXKD dở dang (TT133 không dùng 622)", 1), ("627", "154", "Chi phí SXKD dở dang (TT133 không dùng 627)", 1),
                ("632", "632", "Giá vốn hàng bán", 1), ("635", "635", "Chi phí tài chính", 1), ("641", "6421", "Chi phí bán hàng", 1), ("642", "6422", "Chi phí quản lý doanh nghiệp", 1),
                ("711", "711", "Thu nhập khác", 1), ("811", "811", "Chi phí khác", 1), ("821", "821", "Chi phí thuế TNDN", 1), ("911", "911", "Xác định kết quả kinh doanh", 1),
            };
            db.AccountMappings.AddRange(tt133.Select(x => new AccountMapping { Standard = "TT133", FromPrefix = x.From, ToCode = x.To, ToName = x.Name, Sign = x.Sign }));
        }
        await db.SaveChangesAsync();

        // ---------------- Nhân viên mẫu & chấm công 2 tháng gần nhất
        if (!await db.Employees.AnyAsync())
        {
            (string Code, string Name, string Dept, string Pos, decimal Salary, decimal Allow, int Dep, string Acc)[] emps =
            {
                ("NV0001", "Trần Quốc Bảo", "Ban Giám đốc", "Giám đốc", 45_000_000, 5_000_000, 2, "6421"),
                ("NV0002", "Nguyễn Thị Mai", "Phòng Kế toán", "Kế toán trưởng", 22_000_000, 2_000_000, 1, "6421"),
                ("NV0003", "Lê Minh Tuấn", "Phòng Kinh doanh", "Trưởng phòng kinh doanh", 18_000_000, 3_000_000, 1, "6411"),
                ("NV0004", "Phạm Thu Trang", "Phòng Mua hàng", "Chuyên viên mua hàng", 14_000_000, 1_000_000, 0, "6421"),
                ("NV0005", "Võ Văn Nam", "Kho vận", "Thủ kho", 11_000_000, 1_000_000, 2, "6271"),
                ("NV0006", "Đặng Hữu Phước", "Xưởng sản xuất", "Quản đốc", 16_000_000, 2_000_000, 1, "6271"),
                ("NV0007", "Hồ Thị Lan", "Phòng Quản lý chất lượng", "Trưởng nhóm QC", 13_000_000, 1_000_000, 0, "6271"),
                ("NV0008", "Nguyễn Văn Hùng", "Xưởng sản xuất", "Thợ mộc", 9_500_000, 500_000, 1, "622"),
                ("NV0009", "Trần Văn Lợi", "Xưởng sản xuất", "Thợ sơn", 9_000_000, 500_000, 0, "622"),
                ("NV0010", "Lý Thị Hoa", "Phòng Kinh doanh", "Nhân viên kinh doanh", 10_000_000, 1_500_000, 0, "6411"),
            };
            foreach (var e in emps)
                db.Employees.Add(new Employee
                {
                    Code = e.Code, FullName = e.Name, Department = e.Dept, Position = e.Pos, BaseSalary = e.Salary, Allowance = e.Allow, NonTaxAllowance = 730_000,
                    InsuranceSalary = e.Salary, Dependents = e.Dep, ExpenseAccount = e.Acc, StartDate = new DateTime(2023, 1, 1), BranchId = hqBranch,
                    PersonalTaxCode = "80" + e.Code[2..] + "12345", BankName = "Vietcombank", BankAccountNo = "0071" + e.Code[2..] + "0099"
                });
            await db.SaveChangesAsync();
            var rnd = new Random(7);
            var ids = await db.Employees.Select(x => x.Id).ToListAsync();
            for (var k = 1; k <= 2; k++)
            {
                var m = DateTime.Today.AddMonths(-k);
                foreach (var id in ids) db.Attendances.Add(new Attendance { EmployeeId = id, Year = m.Year, Month = m.Month, WorkDays = 26 - rnd.Next(0, 3), OvertimeHours = rnd.Next(0, 12), Source = "Máy chấm công" });
            }
            await db.SaveChangesAsync();
        }
    }
}
