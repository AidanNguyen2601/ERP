using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.ViewModels;

namespace ERP.Web.Data;

/// <summary>
/// Dữ liệu mẫu: công ty sản xuất & thương mại nội thất văn phòng, hoạt động 3,5 tháng gần nhất.
/// Mọi nghiệp vụ mẫu đều chạy qua service thật (mua → nhập kho → hóa đơn → trả tiền; sản xuất; bán → xuất kho → hóa đơn → thu tiền)
/// nên sổ cái, kho, công nợ luôn khớp nhau.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ErpDbContext>();
        await db.Database.EnsureCreatedAsync();
        await EnsureNewTablesAsync(db);
        if (await db.Users.AnyAsync())
        {
            await SeedCatalogsAsync(db);
            await EnsureAccountsAsync(db);
            await PlatformSeeder.SeedAsync(db, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ErpSettings>>().Value.PoApprovalThreshold);
            await SeedQualityAsync(db);
            await SeedQcDemoAsync(sp, db);
            await GroupDemoSeeder.SeedAsync(sp, db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("GroupDemoSeeder"));
            await TradeDemoSeeder.SeedAsync(db);
            await ProcessDemoSeeder.SeedAsync(sp, db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("ProcessDemoSeeder"));
            await BackfillPurchaseReturnsAsync(sp, db);
            return;
        }
        await SeedCoreAsync(sp, db);
        await SeedCatalogsAsync(db);
        await EnsureAccountsAsync(db);
        await PlatformSeeder.SeedAsync(db, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ErpSettings>>().Value.PoApprovalThreshold);
        await SeedQualityAsync(db);
        await SeedQcDemoAsync(sp, db);
        await GroupDemoSeeder.SeedAsync(sp, db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("GroupDemoSeeder"));
        await TradeDemoSeeder.SeedAsync(db);
        await ProcessDemoSeeder.SeedAsync(sp, db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("ProcessDemoSeeder"));
        await BackfillPurchaseReturnsAsync(sp, db);
    }

    /// <summary>Tài khoản chi tiết của 521 (giảm trừ doanh thu) dùng cho hàng bán bị trả lại / giảm giá hàng bán; thêm vào hệ thống tài khoản đã có nếu thiếu</summary>
    private static async Task EnsureAccountsAsync(ErpDbContext db)
    {
        var parent = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "521");
        if (parent == null) return;
        foreach (var (code, name) in new[] { ("5211", "Chiết khấu thương mại"), ("5212", "Hàng bán bị trả lại"), ("5213", "Giảm giá hàng bán") })
            if (!await db.Accounts.AnyAsync(a => a.Code == code)) db.Accounts.Add(new Account { Code = code, Name = name, Type = parent.Type });
        // TK tổng hợp 521 không hạch toán trực tiếp nữa (nếu chưa có phát sinh)
        if (parent.IsPostable && !await db.JournalLines.AnyAsync(l => l.AccountId == parent.Id)) parent.IsPostable = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Phiếu trả hàng NCR lập trước khi có cơ chế đồng bộ công nợ / thuế / đơn mua: bổ sung bút toán điều chỉnh và cấn trừ hóa đơn.
    /// Chạy lại nhiều lần an toàn (chỉ xử lý NCR chưa có phiếu trả hàng); lỗi thì hoàn tác và bỏ qua.
    /// </summary>
    private static async Task BackfillPurchaseReturnsAsync(IServiceProvider sp, ErpDbContext db)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PurchaseReturnBackfill");
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var n = await sp.GetRequiredService<PurchaseReturnService>().BackfillLegacyAsync();
            await tx.CommitAsync();
            if (n > 0) logger.LogInformation("Đã bổ sung đồng bộ công nợ / thuế cho {Count} phiếu trả hàng nhà cung cấp cũ", n);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(); db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Không bổ sung được đồng bộ cho phiếu trả hàng cũ (đã hoàn tác, ứng dụng vẫn chạy bình thường)");
        }
    }

    /// <summary>Dữ liệu mẫu quản lý chất lượng (tắt bằng Database:SeedQcDemo = false)</summary>
    private static async Task SeedQcDemoAsync(IServiceProvider sp, ErpDbContext db)
    {
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Database:SeedQcDemo", true)) return;
        await QcDemoSeeder.SeedAsync(sp, db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("QcDemoSeeder"));
    }

    /// <summary>
    /// Dự án dùng EnsureCreated (không có migration) nên database tạo từ phiên bản trước không tự có bảng mới.
    /// Lấy script tạo database do EF sinh ra từ model hiện tại và chỉ chạy các lệnh CREATE TABLE của bảng còn thiếu
    /// (kèm CREATE INDEX của chính các bảng đó). Không sửa bảng đã có.
    /// </summary>
    private static async Task EnsureNewTablesAsync(ErpDbContext db)
    {
        // Cột thêm sau vào bảng đã có (EnsureCreated không tự thêm cột)
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.PurchaseOrderLines', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PurchaseOrderLines', N'ReturnedQty') IS NULL
                ALTER TABLE dbo.PurchaseOrderLines ADD ReturnedQty decimal(18,2) NOT NULL CONSTRAINT DF_PurchaseOrderLines_ReturnedQty DEFAULT 0;
            """);
        var existing = (await db.Database.SqlQueryRaw<string>("SELECT TABLE_NAME AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'")
            .ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var script = db.Database.GenerateCreateScript();
        var batches = System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        var tableRx = new System.Text.RegularExpressions.Regex(@"^CREATE TABLE \[(?:\w+)\]\.\[(\w+)\]|^CREATE TABLE \[(\w+)\]");
        var indexRx = new System.Text.RegularExpressions.Regex(@"^CREATE (?:UNIQUE )?INDEX .* ON \[(?:\w+\]\.\[)?(\w+)\]");
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in batches)
        {
            var t = tableRx.Match(b);
            if (t.Success)
            {
                var name = t.Groups[1].Success ? t.Groups[1].Value : t.Groups[2].Value;
                if (existing.Contains(name)) { await AddMissingColumnsAsync(db, name, b); continue; }
                await db.Database.ExecuteSqlRawAsync(b);
                created.Add(name);
                continue;
            }
            var ix = indexRx.Match(b);
            if (ix.Success && created.Contains(ix.Groups[1].Value)) await db.Database.ExecuteSqlRawAsync(b);
        }
    }

    /// <summary>Giá trị mặc định cho cột mới NOT NULL thêm vào bảng đã có dữ liệu</summary>
    private static readonly Dictionary<string, string> ColumnDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AllBranches"] = "1", ["ExchangeRate"] = "1", ["CurrencyCode"] = "N'VND'", ["ExpiryAlertDays"] = "30",
    };

    /// <summary>Thêm các cột có trong model nhưng chưa có trong bảng (dựa trên lệnh CREATE TABLE do EF sinh). Không sửa / xóa cột cũ.</summary>
    private static async Task AddMissingColumnsAsync(ErpDbContext db, string table, string createSql)
    {
        var cols = (await db.Database.SqlQueryRaw<string>("SELECT COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0}", table).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var colRx = new System.Text.RegularExpressions.Regex(@"^\s*\[(\w+)\]\s+(.+?)\s+(NOT NULL|NULL)\b", System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match m in colRx.Matches(createSql))
        {
            var name = m.Groups[1].Value; var type = m.Groups[2].Value.Trim();
            if (cols.Contains(name) || type.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase)) continue;
            string sql;
            if (m.Groups[3].Value == "NULL") sql = $"ALTER TABLE [{table}] ADD [{name}] {type} NULL";
            else
            {
                var def = ColumnDefaults.TryGetValue(name, out var d) ? d
                    : type.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase) || type.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) ? "N''"
                    : type.StartsWith("datetime", StringComparison.OrdinalIgnoreCase) || type.StartsWith("date", StringComparison.OrdinalIgnoreCase) ? "'1900-01-01'" : "0";
                sql = $"ALTER TABLE [{table}] ADD [{name}] {type} NOT NULL CONSTRAINT [DF_{table}_{name}] DEFAULT {def}";
            }
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    /// <summary>Danh mục mặc định + mọi giá trị đang dùng trong dữ liệu (chạy lại nhiều lần không trùng)</summary>
    private static async Task SeedCatalogsAsync(ErpDbContext db)
    {
        var existing = await db.CatalogItems.ToListAsync();
        var units = new[] { "Cái", "Bộ", "Chiếc", "Tấm", "Thanh", "Cuộn", "Hộp", "Thùng", "Kg", "Tấn", "Lít", "Mét", "m²", "m³", "Lần", "Chuyến", "Tháng", "Giờ" }
            .Concat(await db.Items.Select(i => i.Unit).Distinct().ToListAsync())
            .Concat(await db.InvoiceLines.Where(l => l.Unit != null).Select(l => l.Unit!).Distinct().ToListAsync());
        var depts = new[] { "Ban Giám đốc", "Phòng Kế toán", "Phòng Kinh doanh", "Phòng Mua hàng", "Phòng CNTT", "Phòng Hành chính – Nhân sự", "Kho vận", "Xưởng sản xuất", "Phòng Quản lý chất lượng" }
            .Concat(await db.Users.Select(u => u.Department).Distinct().ToListAsync())
            .Concat(await db.FixedAssets.Where(a => a.Department != null).Select(a => a.Department!).Distinct().ToListAsync());

        void Add(CatalogType type, IEnumerable<string> names, string prefix)
        {
            var list = existing.Where(c => c.Type == type).ToList();
            var order = list.Count == 0 ? 0 : list.Max(c => c.SortOrder);
            foreach (var n in names.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (list.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
                var no = list.Count + 1;
                string code;
                do code = $"{prefix}{no++:00}"; while (list.Any(c => c.Code == code));
                var c = new CatalogItem { Type = type, Code = code, Name = n, SortOrder = order += 10 };
                list.Add(c); db.CatalogItems.Add(c);
            }
        }
        Add(CatalogType.Unit, units, "DVT");
        Add(CatalogType.Department, depts, "PB");
        await db.SaveChangesAsync();
    }

    /// <summary>Kế hoạch kiểm tra chất lượng mẫu (chỉ tạo khi chưa có kế hoạch nào)</summary>
    private static async Task SeedQualityAsync(ErpDbContext db)
    {
        if (await db.QcPlans.AnyAsync()) return;
        var mdf = await db.Items.FirstOrDefaultAsync(i => i.Code == "NVL-MDF18");

        static QcPlanLine PF(string name, string? standard = null, string? method = "Quan sát", bool critical = false) =>
            new() { Name = name, Standard = standard, Method = method, Type = QcCriterionType.PassFail, IsCritical = critical };
        static QcPlanLine M(string name, decimal min, decimal max, string unit, string? method, bool critical = false) =>
            new() { Name = name, Type = QcCriterionType.Measure, MinValue = min, MaxValue = max, Unit = unit, Method = method, IsCritical = critical, Standard = $"{min:0.##} – {max:0.##} {unit}" };

        var plans = new List<QcPlan>();
        if (mdf != null)
            plans.Add(new QcPlan
            {
                Code = "QC-IN-MDF", Name = "Kiểm tra đầu vào ván MDF 18mm", Stage = QcStage.Incoming, ItemId = mdf.Id, SamplePct = 5,
                Lines = { M("Độ dày tấm ván", 17.7m, 18.3m, "mm", "Thước kẹp, đo 4 góc", true), M("Độ ẩm", 6, 10, "%", "Máy đo độ ẩm gỗ"),
                          PF("Bề mặt không trầy xước, phồng rộp", "Không có khuyết tật nhìn thấy ở 1m", critical: true), PF("Có chứng từ CO / CQ của lô hàng", method: "Kiểm tra hồ sơ") }
            });
        plans.Add(new QcPlan
        {
            Code = "QC-IN-NVL", Name = "Kiểm tra đầu vào nguyên vật liệu", Stage = QcStage.Incoming, ItemType = ItemType.Material, SamplePct = 10,
            Lines = { PF("Đúng chủng loại, quy cách theo đơn mua", critical: true), PF("Số lượng khớp phiếu giao hàng", method: "Đếm / cân"),
                      PF("Bao bì nguyên vẹn, không ẩm mốc"), PF("Có chứng từ CO / CQ", method: "Kiểm tra hồ sơ") }
        });
        plans.Add(new QcPlan
        {
            Code = "QC-IN-HH", Name = "Kiểm tra đầu vào hàng hóa thương mại", Stage = QcStage.Incoming, ItemType = ItemType.Merchandise, SamplePct = 10,
            Lines = { PF("Đúng model, màu sắc theo đơn", critical: true), PF("Vận hành thử đạt yêu cầu", method: "Chạy thử", critical: true),
                      PF("Ngoại quan không móp méo, trầy xước"), PF("Đủ phụ kiện, hướng dẫn sử dụng, phiếu bảo hành") }
        });
        plans.Add(new QcPlan
        {
            Code = "QC-FG", Name = "Kiểm tra thành phẩm nội thất", Stage = QcStage.Final, ItemType = ItemType.FinishedGood, SamplePct = 20,
            Lines = { M("Sai lệch kích thước tổng thể", -2, 2, "mm", "Thước cuộn so bản vẽ", true), M("Độ phẳng mặt bàn / cánh tủ", 0, 1, "mm", "Thước thẳng + căn lá"),
                      PF("Màng sơn PU không chảy, bọt, lỗ kim", critical: true), PF("Liên kết chắc chắn, không lung lay", method: "Lắc thử", critical: true),
                      PF("Bản lề, ray trượt vận hành êm", method: "Đóng mở 10 lần"), PF("Vệ sinh sạch, đóng gói đúng quy cách") }
        });
        plans.Add(new QcPlan
        {
            Code = "QC-OUT", Name = "Kiểm tra trước khi giao hàng", Stage = QcStage.Outgoing, SamplePct = 100,
            Lines = { PF("Đúng mã hàng, số lượng theo đơn bán", method: "Đối chiếu phiếu xuất", critical: true), PF("Bao bì, tem nhãn đầy đủ"),
                      PF("Không trầy xước, móp méo khi xếp xe") }
        });
        foreach (var p in plans) { var i = 1; foreach (var l in p.Lines) l.Seq = i++; }
        db.QcPlans.AddRange(plans);
        await db.SaveChangesAsync();
    }

    private static async Task SeedCoreAsync(IServiceProvider sp, ErpDbContext db)
    {

        var hasher = sp.GetRequiredService<IPasswordHasher<AppUser>>();
        var acc = sp.GetRequiredService<AccountingService>();
        var inv = sp.GetRequiredService<InventoryService>();
        var invoices = sp.GetRequiredService<InvoiceService>();
        var purchase = sp.GetRequiredService<PurchaseService>();
        var sales = sp.GetRequiredService<SalesService>();
        var cash = sp.GetRequiredService<CashService>();
        var assets = sp.GetRequiredService<AssetService>();
        var production = sp.GetRequiredService<ProductionService>();
        var periods = sp.GetRequiredService<PeriodService>();

        // ------------------------------------------------------------ Người dùng
        AppUser U(string u, string name, string role, string dept)
        {
            var x = new AppUser { Username = u, FullName = name, Role = role, Department = dept, Email = $"{u}@noithatdemo.vn" };
            x.PasswordHash = hasher.HashPassword(x, "Abc@123");
            return x;
        }
        db.Users.AddRange(
            U("admin", "Quản trị hệ thống", AppRoles.Admin, "Phòng CNTT"),
            U("director", "Trần Quốc Bảo", AppRoles.Director, "Ban Giám đốc"),
            U("ketoan", "Nguyễn Thị Mai", AppRoles.Accountant, "Phòng Kế toán"),
            U("kinhdoanh", "Lê Minh Tuấn", AppRoles.Sales, "Phòng Kinh doanh"),
            U("muahang", "Phạm Thu Trang", AppRoles.Purchasing, "Phòng Mua hàng"),
            U("thukho", "Võ Văn Nam", AppRoles.Warehouse, "Kho vận"),
            U("sanxuat", "Đặng Hữu Phước", AppRoles.Production, "Xưởng sản xuất"),
            U("qc", "Hồ Thị Lan", AppRoles.Quality, "Phòng Quản lý chất lượng"));

        // ------------------------------------------------------------ Hệ thống tài khoản (TT200 rút gọn)
        var coa = new (string Code, string Name, AccountType Type, bool Partner)[]
        {
            ("111", "Tiền mặt", AccountType.Asset, false), ("1111", "Tiền Việt Nam", AccountType.Asset, false),
            ("112", "Tiền gửi ngân hàng", AccountType.Asset, false), ("1121", "Tiền gửi ngân hàng – VND", AccountType.Asset, false),
            ("11211", "TGNH Vietcombank – CN Tân Bình", AccountType.Asset, false), ("11212", "TGNH Techcombank – CN Cộng Hòa", AccountType.Asset, false),
            ("131", "Phải thu của khách hàng", AccountType.Asset, true),
            ("133", "Thuế GTGT được khấu trừ", AccountType.Asset, false), ("1331", "Thuế GTGT được khấu trừ của HHDV", AccountType.Asset, false),
            ("138", "Phải thu khác", AccountType.Asset, false), ("1381", "Tài sản thiếu chờ xử lý", AccountType.Asset, false), ("1388", "Phải thu khác", AccountType.Asset, false),
            ("141", "Tạm ứng", AccountType.Asset, false),
            ("152", "Nguyên liệu, vật liệu", AccountType.Asset, false),
            ("154", "Chi phí sản xuất, kinh doanh dở dang", AccountType.Asset, false),
            ("155", "Thành phẩm", AccountType.Asset, false),
            ("156", "Hàng hóa", AccountType.Asset, false), ("1561", "Giá mua hàng hóa", AccountType.Asset, false),
            ("211", "Tài sản cố định hữu hình", AccountType.Asset, false), ("2111", "Nhà cửa, vật kiến trúc", AccountType.Asset, false),
            ("2112", "Máy móc, thiết bị", AccountType.Asset, false), ("2113", "Phương tiện vận tải, truyền dẫn", AccountType.Asset, false),
            ("2114", "Thiết bị, dụng cụ quản lý", AccountType.Asset, false),
            ("214", "Hao mòn tài sản cố định", AccountType.Asset, false), ("2141", "Hao mòn TSCĐ hữu hình", AccountType.Asset, false),
            ("242", "Chi phí trả trước", AccountType.Asset, false),
            ("331", "Phải trả cho người bán", AccountType.Liability, true),
            ("333", "Thuế và các khoản phải nộp Nhà nước", AccountType.Liability, false), ("3331", "Thuế giá trị gia tăng phải nộp", AccountType.Liability, false),
            ("33311", "Thuế GTGT đầu ra", AccountType.Liability, false), ("3334", "Thuế thu nhập doanh nghiệp", AccountType.Liability, false),
            ("334", "Phải trả người lao động", AccountType.Liability, false), ("3341", "Phải trả công nhân viên", AccountType.Liability, false),
            ("338", "Phải trả, phải nộp khác", AccountType.Liability, false), ("3381", "Tài sản thừa chờ giải quyết", AccountType.Liability, false),
            ("3383", "Bảo hiểm xã hội", AccountType.Liability, false), ("3388", "Phải trả, phải nộp khác", AccountType.Liability, false),
            ("341", "Vay và nợ thuê tài chính", AccountType.Liability, false), ("3411", "Các khoản đi vay", AccountType.Liability, false),
            ("411", "Vốn đầu tư của chủ sở hữu", AccountType.Equity, false), ("4111", "Vốn góp của chủ sở hữu", AccountType.Equity, false),
            ("421", "Lợi nhuận sau thuế chưa phân phối", AccountType.Equity, false), ("4211", "LNST chưa phân phối năm trước", AccountType.Equity, false),
            ("4212", "LNST chưa phân phối năm nay", AccountType.Equity, false),
            ("511", "Doanh thu bán hàng và cung cấp dịch vụ", AccountType.Revenue, false), ("5111", "Doanh thu bán hàng hóa", AccountType.Revenue, false),
            ("5112", "Doanh thu bán các thành phẩm", AccountType.Revenue, false), ("5113", "Doanh thu cung cấp dịch vụ", AccountType.Revenue, false),
            ("515", "Doanh thu hoạt động tài chính", AccountType.Revenue, false),
            ("521", "Các khoản giảm trừ doanh thu", AccountType.Revenue, false),
            ("621", "Chi phí nguyên liệu, vật liệu trực tiếp", AccountType.Expense, false),
            ("622", "Chi phí nhân công trực tiếp", AccountType.Expense, false),
            ("627", "Chi phí sản xuất chung", AccountType.Expense, false),
            ("632", "Giá vốn hàng bán", AccountType.Expense, false),
            ("635", "Chi phí tài chính", AccountType.Expense, false),
            ("641", "Chi phí bán hàng", AccountType.Expense, false),
            ("642", "Chi phí quản lý doanh nghiệp", AccountType.Expense, false), ("6421", "Chi phí nhân viên quản lý", AccountType.Expense, false),
            ("6424", "Chi phí khấu hao TSCĐ", AccountType.Expense, false), ("6427", "Chi phí dịch vụ mua ngoài", AccountType.Expense, false),
            ("6428", "Chi phí bằng tiền khác", AccountType.Expense, false),
            ("711", "Thu nhập khác", AccountType.Revenue, false),
            ("811", "Chi phí khác", AccountType.Expense, false),
            ("821", "Chi phí thuế thu nhập doanh nghiệp", AccountType.Expense, false),
            ("911", "Xác định kết quả kinh doanh", AccountType.Result, false)
        };
        var codes = coa.Select(c => c.Code).ToList();
        foreach (var c in coa)
            db.Accounts.Add(new Account { Code = c.Code, Name = c.Name, Type = c.Type, TrackPartner = c.Partner, IsPostable = !codes.Any(x => x.Length > c.Code.Length && x.StartsWith(c.Code)) });
        await db.SaveChangesAsync();
        async Task<int> A(string code) => await acc.IdAsync(code);

        // ------------------------------------------------------------ Đối tác
        Partner P(string code, string name, bool cust, string tax, string addr, decimal limit, int term, string contact) => new()
        {
            Code = code, Name = name, IsCustomer = cust, IsVendor = !cust, TaxCode = tax, Address = addr, CreditLimit = limit, PaymentTermDays = term,
            ContactPerson = contact, Phone = "028 38" + (100000 + code.GetHashCode() % 99999).ToString("000000")[..6]
        };
        var kh1 = P("KH001", "Công ty TNHH Nội thất Hòa Bình", true, "0301112223", "12 Nguyễn Văn Trỗi, Phú Nhuận, TP.HCM", 600_000_000, 30, "Anh Hoàng – Phó GĐ");
        var kh2 = P("KH002", "Công ty CP Văn phòng Xanh", true, "0314445556", "88 Lê Lợi, Quận 1, TP.HCM", 300_000_000, 30, "Chị Ngọc – Hành chính");
        var kh3 = P("KH003", "Trường Đại học Công nghệ Sài Gòn", true, "0302223334", "180 Cao Lỗ, Quận 8, TP.HCM", 0, 45, "Thầy Phúc – P. Quản trị");
        var kh4 = P("KH004", "Công ty TNHH Thương mại Minh Phát", true, "0315556667", "25 Trường Chinh, Tân Bình, TP.HCM", 150_000_000, 15, "Anh Phát – Giám đốc");
        var kh5 = P("KH005", "Cửa hàng Nội thất An Gia", true, "0316667778", "301 Phan Văn Trị, Gò Vấp, TP.HCM", 100_000_000, 15, "Chị Gia");
        var ncc1 = P("NCC001", "Công ty CP Gỗ Ván Phương Nam", false, "3701234567", "KCN Sóng Thần, Dĩ An, Bình Dương", 0, 30, "Anh Lực – Kinh doanh");
        var ncc2 = P("NCC002", "Công ty TNHH Kim khí Phú Thịnh", false, "0309876543", "45 Lũy Bán Bích, Tân Phú, TP.HCM", 0, 15, "Chị Thịnh");
        var ncc3 = P("NCC003", "Công ty TNHH Sơn Phủ Đông Á", false, "0308765432", "KCN Tân Tạo, Bình Tân, TP.HCM", 0, 30, "Anh Đông");
        var ncc4 = P("NCC004", "Công ty TNHH Ghế Văn phòng Đại Việt", false, "0107654321", "Lô 5, KCN Quang Minh, Hà Nội", 0, 30, "Chị Việt");
        var ncc5 = P("NCC005", "Công ty TNHH Vận tải Nhanh Sài Gòn", false, "0312223344", "9 Quốc lộ 1A, Bình Tân, TP.HCM", 0, 15, "Anh Nhanh");
        db.Partners.AddRange(kh1, kh2, kh3, kh4, kh5, ncc1, ncc2, ncc3, ncc4, ncc5);

        // ------------------------------------------------------------ Kho & hàng hóa
        var wNvl = new Warehouse { Code = "KNVL", Name = "Kho nguyên vật liệu", Address = "Xưởng KCN Tân Bình" };
        var wTp = new Warehouse { Code = "KTP", Name = "Kho thành phẩm", Address = "Xưởng KCN Tân Bình" };
        var wHh = new Warehouse { Code = "KHH", Name = "Kho hàng hóa", Address = "Showroom 25 Cộng Hòa" };
        db.Warehouses.AddRange(wNvl, wTp, wHh);
        Item I(string code, string name, string unit, ItemType t, decimal price, decimal min = 0, int vat = 10) =>
            new() { Code = code, Name = name, Unit = unit, Type = t, SalePrice = price, MinStock = min, VatRate = vat };
        var mdf = I("NVL-MDF18", "Ván MDF chống ẩm 18mm", "Tấm", ItemType.Material, 0, 120);
        var soi = I("NVL-GOSOI", "Gỗ sồi thanh xẻ sấy 2m", "Thanh", ItemType.Material, 0, 100);
        var vit = I("NVL-OCVIT", "Bộ ốc vít liên kết", "Bộ", ItemType.Material, 0, 150);
        var banle = I("NVL-BANLE", "Bản lề giảm chấn", "Cái", ItemType.Material, 0, 200);
        var son = I("NVL-SONPU", "Sơn PU phủ bóng", "Lít", ItemType.Material, 0, 60);
        var ray = I("NVL-RAY", "Ray trượt ngăn kéo", "Bộ", ItemType.Material, 0, 80);
        var blv = I("TP-BLV120", "Bàn làm việc 1m2 chân gỗ sồi", "Cái", ItemType.FinishedGood, 3_600_000, 20);
        var ths = I("TP-THS3", "Tủ hồ sơ 3 cánh", "Cái", ItemType.FinishedGood, 5_900_000, 10);
        var ks = I("TP-KS06", "Kệ sách 6 tầng", "Cái", ItemType.FinishedGood, 2_400_000, 10);
        var ghe = I("HH-GXVP", "Ghế xoay văn phòng lưng lưới", "Cái", ItemType.Merchandise, 1_950_000, 20);
        var den = I("HH-DENLED", "Đèn bàn LED chống cận", "Cái", ItemType.Merchandise, 520_000, 30);
        var lapdat = I("DV-LAPDAT", "Dịch vụ lắp đặt tại công trình", "Lần", ItemType.Service, 300_000, 0, 8);
        var vanchuyen = I("DV-VC", "Vận chuyển nội thành", "Chuyến", ItemType.Service, 500_000, 0, 8);
        db.Items.AddRange(mdf, soi, vit, banle, son, ray, blv, ths, ks, ghe, den, lapdat, vanchuyen);

        // ------------------------------------------------------------ Quỹ, ngân hàng
        var cashTm = new CashAccount { Code = "TM", Name = "Quỹ tiền mặt", Type = CashAccountType.Cash, AccountId = await A("1111") };
        var vcb = new CashAccount { Code = "VCB", Name = "Vietcombank – CN Tân Bình", Type = CashAccountType.Bank, AccountId = await A("11211"), BankName = "Vietcombank", BankAccountNo = "0071 0012 34567" };
        var tcb = new CashAccount { Code = "TCB", Name = "Techcombank – CN Cộng Hòa", Type = CashAccountType.Bank, AccountId = await A("11212"), BankName = "Techcombank", BankAccountNo = "1903 5566 7788 99" };
        db.CashAccounts.AddRange(cashTm, vcb, tcb);

        // ------------------------------------------------------------ Tài sản cố định
        var catNx = new AssetCategory { Code = "NX", Name = "Nhà xưởng, vật kiến trúc", AssetAccountId = await A("2111"), DepreciationAccountId = await A("2141"), ExpenseAccountId = await A("6424"), DefaultLifeMonths = 240 };
        var catMm = new AssetCategory { Code = "MM", Name = "Máy móc thiết bị sản xuất", AssetAccountId = await A("2112"), DepreciationAccountId = await A("2141"), ExpenseAccountId = await A("6424"), DefaultLifeMonths = 96 };
        var catPt = new AssetCategory { Code = "PT", Name = "Phương tiện vận tải", AssetAccountId = await A("2113"), DepreciationAccountId = await A("2141"), ExpenseAccountId = await A("641"), DefaultLifeMonths = 120 };
        var catQl = new AssetCategory { Code = "QL", Name = "Thiết bị quản lý", AssetAccountId = await A("2114"), DepreciationAccountId = await A("2141"), ExpenseAccountId = await A("6424"), DefaultLifeMonths = 36 };
        db.AssetCategories.AddRange(catNx, catMm, catPt, catQl);
        await db.SaveChangesAsync();

        // Mốc thời gian: số dư đầu kỳ ngày 01 của tháng cách đây 3 tháng
        var today = DateTime.Today;
        var d0 = new DateTime(today.Year, today.Month, 1).AddMonths(-3);
        DateTime D(int m, int day) => new DateTime(d0.Year, d0.Month, 1).AddMonths(m).AddDays(day - 1) is var x && x > today ? today : x;

        FixedAsset FA(string code, string name, AssetCategory cat, string dept, decimal cost, int life, DateTime start, string serial)
        {
            var months = Math.Max(0, (d0.Year - start.Year) * 12 + d0.Month - start.Month);
            var a = new FixedAsset { Code = code, Name = name, CategoryId = cat.Id, Department = dept, OriginalCost = cost, UsefulLifeMonths = life, AcquisitionDate = start, DepreciationStartDate = start, SerialNo = serial };
            a.AccumulatedDepreciation = Math.Min(cost, a.MonthlyDepreciation * months);
            return a;
        }
        var fas = new[]
        {
            FA("TS001", "Nhà xưởng sản xuất 1.200 m²", catNx, "Xưởng sản xuất", 1_200_000_000, 240, d0.AddMonths(-55), "NX-01"),
            FA("TS002", "Máy CNC cắt ván 2 đầu", catMm, "Xưởng sản xuất", 620_000_000, 96, d0.AddMonths(-29), "CNC-2H-2024"),
            FA("TS003", "Máy chà nhám thùng", catMm, "Xưởng sản xuất", 240_000_000, 60, d0.AddMonths(-19), "CN-1300"),
            FA("TS004", "Xe tải 2,5 tấn", catPt, "Kho vận", 950_000_000, 120, d0.AddMonths(-38), "51D-123.45"),
            FA("TS005", "Hệ thống máy tính, máy chủ văn phòng", catQl, "Văn phòng", 145_000_000, 36, d0.AddMonths(-23), "SRV-2024")
        };
        foreach (var a in fas) await assets.RegisterAsync(a, null, null);

        // ------------------------------------------------------------ Số dư đầu kỳ
        var accum = fas.Sum(a => a.AccumulatedDepreciation);
        var openLines = new List<JLine>
        {
            new(await A("1111"), 150_000_000, 0), new(await A("11211"), 1_850_000_000, 0), new(await A("11212"), 620_000_000, 0),
            new(await A("131"), 180_000_000, 0, kh1.Id), new(await A("131"), 95_000_000, 0, kh3.Id),
            new(await A("2111"), 1_200_000_000, 0), new(await A("2112"), 860_000_000, 0), new(await A("2113"), 950_000_000, 0), new(await A("2114"), 145_000_000, 0),
            new(await A("2141"), 0, accum),
            new(await A("331"), 0, 120_000_000, ncc1.Id),
            new(await A("3411"), 0, 800_000_000),
            new(await A("4211"), 0, 300_000_000)
        };
        var diff = openLines.Sum(l => l.Debit - l.Credit);
        openLines.Add(new JLine(await A("4111"), 0, diff));
        await acc.PostAsync(d0, "Số dư đầu kỳ chuyển sang", JournalSource.Opening, "SDDK", null, openLines);

        // Hóa đơn tồn đầu kỳ (đã có trong số dư 131/331) – để theo dõi tuổi nợ, cấn trừ thanh toán
        Invoice OpenInv(InvoiceDirection dir, Partner p, string no, decimal amt, int daysBefore, int term) => new()
        {
            Direction = dir, Number = $"DK-{(dir == InvoiceDirection.Sales ? "131" : "331")}-{p.Code}", Series = "DK", InvoiceNo = no, PartnerId = p.Id,
            Date = d0.AddDays(-daysBefore), DueDate = d0.AddDays(-daysBefore + term), Status = InvoiceStatus.Issued, Subtotal = amt, Total = amt,
            Note = "Công nợ đầu kỳ chuyển sang",
            Lines = { new InvoiceLine { Description = "Công nợ đầu kỳ", Quantity = 1, UnitPrice = amt, Amount = amt } }
        };
        db.Invoices.AddRange(
            OpenInv(InvoiceDirection.Sales, kh1, "0000958", 180_000_000, 20, 30),
            OpenInv(InvoiceDirection.Sales, kh3, "0000962", 95_000_000, 12, 45),
            OpenInv(InvoiceDirection.Purchase, ncc1, "0004417", 120_000_000, 18, 30));
        await db.SaveChangesAsync();

        // Tồn kho đầu kỳ (đối ứng 4111 – vốn chủ sở hữu)
        async Task OpenStock(Warehouse w, params (Item Item, decimal Qty, decimal Cost)[] lines)
        {
            var doc = new InventoryDoc
            {
                Type = InvDocType.Receipt, Date = d0, WarehouseId = w.Id, CounterAccountId = await A("4111"), Reason = "Tồn kho đầu kỳ",
                Number = await inv.NextNumberAsync(InvDocType.Receipt, d0)
            };
            foreach (var l in lines) doc.Lines.Add(new InventoryDocLine { ItemId = l.Item.Id, Quantity = l.Qty, UnitCost = l.Cost });
            await inv.PostAsync(doc);
        }
        await OpenStock(wNvl, (mdf, 120, 420_000), (soi, 300, 180_000), (vit, 600, 15_000), (banle, 150, 25_000), (son, 150, 120_000), (ray, 60, 60_000));
        await OpenStock(wTp, (blv, 40, 1_450_000), (ths, 20, 2_250_000), (ks, 30, 1_100_000));
        await OpenStock(wHh, (ghe, 60, 1_100_000), (den, 80, 280_000));

        // ------------------------------------------------------------ Định mức sản xuất
        Bom B(string code, Item fg, decimal labor, decimal oh, params (Item Item, decimal Qty, decimal Scrap)[] lines)
        {
            var b = new Bom { Code = code, FinishedItemId = fg.Id, OutputQuantity = 1, LaborCostPerUnit = labor, OverheadPerUnit = oh };
            foreach (var l in lines) b.Lines.Add(new BomLine { ItemId = l.Item.Id, Quantity = l.Qty, ScrapPct = l.Scrap });
            return b;
        }
        var bomBlv = B("BOM-BLV120", blv, 250_000, 120_000, (mdf, 1.5m, 2), (soi, 2, 0), (vit, 1, 0), (son, 0.3m, 0), (ray, 1, 0));
        var bomThs = B("BOM-THS3", ths, 400_000, 200_000, (mdf, 3, 2), (soi, 1, 0), (banle, 6, 0), (vit, 2, 0), (son, 0.5m, 0));
        var bomKs = B("BOM-KS06", ks, 150_000, 80_000, (mdf, 2, 2), (vit, 1, 0), (son, 0.3m, 0));
        db.Boms.AddRange(bomBlv, bomThs, bomKs);
        await db.SaveChangesAsync();

        // ------------------------------------------------------------ Hàm tiện ích nghiệp vụ mẫu
        var labor = new decimal[4]; var overhead = new decimal[4];
        async Task<PurchaseOrder> Po(int m, int day, Partner v, Warehouse w, params (Item Item, decimal Qty, decimal Price)[] lines)
        {
            var po = new PurchaseOrder { Date = D(m, day), VendorId = v.Id, WarehouseId = w.Id, ExpectedDate = D(m, day).AddDays(5), Number = await DocNumbers.NextAsync(db.PurchaseOrders.Select(x => x.Number), "DMH", D(m, day)) };
            foreach (var l in lines) po.Lines.Add(new PurchaseOrderLine { ItemId = l.Item.Id, Quantity = l.Qty, UnitPrice = l.Price, VatRate = l.Item.VatRate });
            PurchaseService.Recalc(po);
            db.PurchaseOrders.Add(po); await db.SaveChangesAsync();
            return po;
        }
        async Task Receive(PurchaseOrder po, int m, int day, decimal? fraction = null) =>
            await purchase.ReceiveAsync(po, D(m, day), po.Lines.ToDictionary(l => l.Id, l => fraction.HasValue ? Math.Round(l.Quantity * fraction.Value, 0) : l.Quantity - l.ReceivedQty));
        async Task<Invoice> Bill(PurchaseOrder po, int m, int day, string no)
        {
            var i = await purchase.CreateInvoiceAsync(po, D(m, day), "1C26T" + po.Vendor!.Code[^2..], no);
            await invoices.IssueAsync(i); return i;
        }
        async Task<SalesOrder> So(int m, int day, Partner c, Warehouse w, params (Item Item, decimal Qty, decimal Price, decimal Disc)[] lines)
        {
            var so = new SalesOrder { Date = D(m, day), CustomerId = c.Id, WarehouseId = w.Id, DeliveryDate = D(m, day).AddDays(3), Number = await DocNumbers.NextAsync(db.SalesOrders.Select(x => x.Number), "DBH", D(m, day)) };
            foreach (var l in lines) so.Lines.Add(new SalesOrderLine { ItemId = l.Item.Id, Quantity = l.Qty, UnitPrice = l.Price, DiscountPct = l.Disc, VatRate = l.Item.VatRate });
            SalesService.Recalc(so);
            db.SalesOrders.Add(so); await db.SaveChangesAsync();
            return so;
        }
        async Task Deliver(SalesOrder so, int m, int day, Func<SalesOrderLine, decimal>? qty = null) =>
            await sales.DeliverAsync(so, D(m, day), so.Lines.ToDictionary(l => l.Id, l => qty?.Invoke(l) ?? l.Quantity - l.DeliveredQty));
        async Task<Invoice> Invoice(SalesOrder so, int m, int day) { var i = await sales.CreateInvoiceAsync(so, D(m, day)); await invoices.IssueAsync(i); return i; }
        async Task Make(int m, int day, Bom bom, decimal qty, int doneDay)
        {
            var po = await production.CreateAsync(new ProductionOrder { BomId = bom.Id, Quantity = qty, MaterialWarehouseId = wNvl.Id, FinishedWarehouseId = wTp.Id, StartDate = D(m, day), DueDate = D(m, doneDay) });
            await production.ReleaseAsync(po);
            await production.IssueMaterialsAsync(po, D(m, day));
            var l = bom.LaborCostPerUnit * qty; var o = bom.OverheadPerUnit * qty;
            await production.CompleteAsync(po, D(m, doneDay), qty, l, o);
            labor[m] += l; overhead[m] += o;
        }
        async Task Pay(CashVoucherType t, CashPurpose p, int m, int day, CashAccount ca, decimal amount, string desc, Partner? partner = null, string? counter = null, int? invoiceId = null) =>
            await cash.CreateAsync(new CashVoucherForm
            {
                Type = t, Purpose = p, Date = D(m, day), CashAccountId = ca.Id, Amount = amount, Description = desc, PartnerId = partner?.Id,
                CounterAccountId = counter == null ? null : await A(counter), InvoiceId = invoiceId, PayerPayee = partner?.ContactPerson
            });
        async Task MonthEnd(int m, bool payroll = true)
        {
            // Điện, nước xưởng (chi phí SXC) bằng đúng phần đã phân bổ vào giá thành; chi phí văn phòng
            if (overhead[m] > 0) await Pay(CashVoucherType.Payment, CashPurpose.Other, m, 27, vcb, overhead[m], $"Tiền điện, nước, bảo trì xưởng tháng {D(m, 1):MM/yyyy}", counter: "627");
            await Pay(CashVoucherType.Payment, CashPurpose.Other, m, 28, cashTm, 3_000_000, $"Chi phí văn phòng phẩm, tiếp khách tháng {D(m, 1):MM/yyyy}", counter: "6428");
            if (payroll)
            {
                var lines = new List<JLine>();
                if (labor[m] > 0) lines.Add(new JLine(await A("622"), labor[m], 0));
                lines.Add(new JLine(await A("641"), 12_000_000, 0));
                lines.Add(new JLine(await A("6421"), 24_000_000, 0));
                lines.Add(new JLine(await A("3341"), 0, lines.Sum(x => x.Debit)));
                await acc.PostAsync(PeriodService.MonthEnd(D(m, 1).Year, D(m, 1).Month), $"Tiền lương phải trả tháng {D(m, 1):MM/yyyy}", JournalSource.Manual, $"BL{D(m, 1):MMyy}", null, lines);
                await assets.RunDepreciationAsync(D(m, 1).Year, D(m, 1).Month);
            }
        }
        decimal Payroll(int m) => labor[m] + 12_000_000 + 24_000_000;

        // ============================================================ THÁNG 1
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 0, 5, vcb, 10_000_000, $"Tiền thuê showroom tháng {D(0, 1):MM/yyyy}", counter: "6427");
        var po1 = await Po(0, 3, ncc1, wNvl, (mdf, 200, 430_000));
        await purchase.SubmitAsync(po1); await Receive(po1, 0, 6); var hm1 = await Bill(po1, 0, 6, "0001234");
        var po2 = await Po(0, 5, ncc2, wNvl, (vit, 500, 15_000), (banle, 200, 25_000), (ray, 200, 58_000));
        await purchase.SubmitAsync(po2); await Receive(po2, 0, 9); var hm2 = await Bill(po2, 0, 9, "0000871");
        await Pay(CashVoucherType.Receipt, CashPurpose.CustomerReceipt, 0, 12, vcb, 180_000_000, "Thu công nợ đầu kỳ – Nội thất Hòa Bình", kh1);
        await Pay(CashVoucherType.Payment, CashPurpose.VendorPayment, 0, 15, vcb, 120_000_000, "Trả nợ đầu kỳ – Gỗ Ván Phương Nam", ncc1);
        await Make(0, 8, bomBlv, 30, 15);
        await Make(0, 12, bomThs, 15, 22);
        var so1 = await So(0, 10, kh1, wTp, (blv, 20, 3_600_000, 0), (lapdat, 20, 300_000, 0));
        await sales.ConfirmAsync(so1, false); await Deliver(so1, 0, 18); var hb1 = await Invoice(so1, 0, 18);
        var so1b = await So(0, 11, kh1, wHh, (ghe, 20, 1_950_000, 0));
        await sales.ConfirmAsync(so1b, false); await Deliver(so1b, 0, 18); var hb1b = await Invoice(so1b, 0, 18);
        await Pay(CashVoucherType.Payment, CashPurpose.VendorPayment, 0, 20, vcb, hm1.Total, $"Thanh toán HĐ {hm1.InvoiceNo} – Gỗ Ván Phương Nam", ncc1, invoiceId: hm1.Id);
        var so2 = await So(0, 20, kh3, wTp, (ths, 10, 5_900_000, 0), (ks, 10, 2_400_000, 0));
        await sales.ConfirmAsync(so2, false); await Deliver(so2, 0, 28); var hb2 = await Invoice(so2, 0, 28);
        await Pay(CashVoucherType.Receipt, CashPurpose.CustomerReceipt, 0, 25, tcb, 95_000_000, "Thu công nợ đầu kỳ – ĐH Công nghệ Sài Gòn", kh3);
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 0, 28, vcb, 6_000_000, "Lãi vay ngân hàng tháng " + D(0, 1).ToString("MM/yyyy"), counter: "635");
        await MonthEnd(0);

        // ============================================================ THÁNG 2
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 1, 5, vcb, Payroll(0), $"Trả lương tháng {D(0, 1):MM/yyyy}", counter: "3341");
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 1, 5, vcb, 10_000_000, $"Tiền thuê showroom tháng {D(1, 1):MM/yyyy}", counter: "6427");
        await Pay(CashVoucherType.Payment, CashPurpose.VendorPayment, 1, 6, tcb, hm2.Total, $"Thanh toán HĐ {hm2.InvoiceNo} – Kim khí Phú Thịnh", ncc2, invoiceId: hm2.Id);
        var po3 = await Po(1, 4, ncc1, wNvl, (mdf, 250, 425_000));
        await purchase.SubmitAsync(po3); await purchase.ApproveAsync(po3, true); po3.ApprovedById = (await db.Users.FirstAsync(u => u.Username == "director")).Id;
        await Receive(po3, 1, 9, 0.8m); var hm3 = await Bill(po3, 1, 9, "0001302");
        var po4 = await Po(1, 4, ncc3, wNvl, (son, 100, 118_000));
        await purchase.SubmitAsync(po4); await Receive(po4, 1, 8); await Bill(po4, 1, 8, "0000215");
        var po5 = await Po(1, 6, ncc4, wHh, (ghe, 50, 1_080_000), (den, 100, 275_000));
        await purchase.SubmitAsync(po5); await Receive(po5, 1, 12); var hm5 = await Bill(po5, 1, 12, "0002688");
        await Make(1, 10, bomKs, 40, 14);
        await Make(1, 15, bomBlv, 25, 22);
        await Pay(CashVoucherType.Receipt, CashPurpose.CustomerReceipt, 1, 10, vcb, hb1.Total + hb1b.Total, "Thu tiền đơn hàng tháng trước – Nội thất Hòa Bình", kh1);
        var so3 = await So(1, 12, kh2, wTp, (ks, 30, 2_400_000, 0), (vanchuyen, 2, 500_000, 0));
        await sales.ConfirmAsync(so3, false); await Deliver(so3, 1, 16); await Invoice(so3, 1, 16);
        var so3b = await So(1, 12, kh2, wHh, (den, 40, 520_000, 0));
        await sales.ConfirmAsync(so3b, false); await Deliver(so3b, 1, 16); await Invoice(so3b, 1, 16);
        var so4 = await So(1, 25, kh5, wHh, (ghe, 30, 1_950_000, 5));
        await sales.ConfirmAsync(so4, false); await Deliver(so4, 1, 27); var hb4 = await Invoice(so4, 1, 27);
        var dv = new Invoice
        {
            Direction = InvoiceDirection.Purchase, Number = await invoices.NextNumberAsync(InvoiceDirection.Purchase, D(1, 28)), Date = D(1, 28), DueDate = D(1, 28).AddDays(15),
            PartnerId = ncc5.Id, Series = "1C26TVC", InvoiceNo = "0000412", Note = "Cước vận chuyển giao hàng tháng " + D(1, 1).ToString("MM/yyyy"),
            Lines = { new InvoiceLine { Description = "Cước vận chuyển giao hàng nội thành", Unit = "Tháng", Quantity = 1, UnitPrice = 12_000_000, VatRate = 8, AccountId = await A("641") } }
        };
        await invoices.IssueAsync(dv);
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 1, 28, vcb, 6_000_000, "Lãi vay ngân hàng tháng " + D(1, 1).ToString("MM/yyyy"), counter: "635");
        await MonthEnd(1);

        // ============================================================ THÁNG 3
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 2, 5, vcb, Payroll(1), $"Trả lương tháng {D(1, 1):MM/yyyy}", counter: "3341");
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 2, 5, vcb, 10_000_000, $"Tiền thuê showroom tháng {D(2, 1):MM/yyyy}", counter: "6427");
        await Pay(CashVoucherType.Receipt, CashPurpose.CustomerReceipt, 2, 2, tcb, 30_000_000, "ĐH Công nghệ Sài Gòn thanh toán đợt 1", kh3, invoiceId: hb2.Id);
        await Pay(CashVoucherType.Payment, CashPurpose.VendorPayment, 2, 10, tcb, 50_000_000, "Trả trước một phần HĐ ghế, đèn – Đại Việt", ncc4, invoiceId: hm5.Id);
        await Receive(po3, 2, 3);
        await Bill(po3, 2, 3, "0001355");
        await Make(2, 5, bomThs, 12, 12);
        var so5 = await So(2, 8, kh1, wTp, (blv, 25, 3_600_000, 3), (ths, 8, 5_900_000, 3));
        await sales.ConfirmAsync(so5, false); await Deliver(so5, 2, 15); var hb5 = await Invoice(so5, 2, 15);
        await Pay(CashVoucherType.Receipt, CashPurpose.CustomerReceipt, 2, 20, vcb, Math.Round(hb4.Total / 2, 0), "An Gia thanh toán 50% HĐ ghế xoay", kh5, invoiceId: hb4.Id);
        var so6 = await So(2, 20, kh3, wTp, (ks, 20, 2_400_000, 0), (ghe, 10, 1_950_000, 0));
        await sales.ConfirmAsync(so6, false);
        await Deliver(so6, 2, 25, l => l.ItemId == ks.Id ? l.Quantity : 0);
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 2, 28, vcb, 6_000_000, "Lãi vay ngân hàng tháng " + D(2, 1).ToString("MM/yyyy"), counter: "635");
        await Pay(CashVoucherType.Receipt, CashPurpose.Other, 2, 29, tcb, 1_250_000, "Lãi tiền gửi không kỳ hạn", counter: "515");
        await MonthEnd(2);

        // ============================================================ THÁNG HIỆN TẠI (đến hôm nay)
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 3, 5, vcb, Payroll(2), $"Trả lương tháng {D(2, 1):MM/yyyy}", counter: "3341");
        await Pay(CashVoucherType.Payment, CashPurpose.Other, 3, 5, vcb, 10_000_000, $"Tiền thuê showroom tháng {D(3, 1):MM/yyyy}", counter: "6427");
        var so7 = await So(3, 3, kh4, wHh, (ghe, 40, 1_950_000, 0));
        await sales.ConfirmAsync(so7, false); await Deliver(so7, 3, 6); await Invoice(so7, 3, 6);
        await Pay(CashVoucherType.Receipt, CashPurpose.CustomerReceipt, 3, 8, vcb, 100_000_000, "Hòa Bình thanh toán một phần HĐ tháng trước", kh1, invoiceId: hb5.Id);
        await So(3, 10, kh4, wTp, (blv, 30, 3_600_000, 0));                                  // vượt hạn mức → còn nháp
        var so9 = await So(3, 12, kh2, wTp, (ths, 5, 5_900_000, 0), (lapdat, 1, 300_000, 0));
        await sales.ConfirmAsync(so9, false);                                                // chờ giao
        var po6 = await Po(3, 10, ncc1, wNvl, (mdf, 300, 430_000));
        await purchase.SubmitAsync(po6);                                                     // vượt ngưỡng → chờ Giám đốc duyệt
        await Po(3, 11, ncc2, wNvl, (banle, 300, 24_500));                                   // nháp
        var lsx6 = await production.CreateAsync(new ProductionOrder { BomId = bomBlv.Id, Quantity = 20, MaterialWarehouseId = wNvl.Id, FinishedWarehouseId = wTp.Id, StartDate = D(3, 11), DueDate = D(3, 11).AddDays(7) });
        await production.ReleaseAsync(lsx6);                                                 // đã phát lệnh, chưa xuất NVL
        await production.CreateAsync(new ProductionOrder { BomId = bomThs.Id, Quantity = 60, MaterialWarehouseId = wNvl.Id, FinishedWarehouseId = wTp.Id, StartDate = D(3, 14), DueDate = D(3, 14).AddDays(14), Note = "Kế hoạch cho dự án trường học Q4" });
        // Hóa đơn bán nháp chờ phát hành (phần đã giao của đơn ĐH Công nghệ)
        await sales.CreateInvoiceAsync(so6, D(3, 1));

        // Khóa sổ & kết chuyển 2 tháng đầu
        await periods.CloseAsync(D(0, 1).Year, D(0, 1).Month, true);
        await periods.CloseAsync(D(1, 1).Year, D(1, 1).Month, true);
    }
}
