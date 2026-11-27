using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ERP.Web.Models.Entities;

namespace ERP.Web.Data;

/// <summary>
/// Chuyển hệ thống tài khoản sang Thông tư 99/2025/TT-BTC (Phụ lục II; hiệu lực 01/01/2026, thay Thông tư 200/2014/TT-BTC).
/// Áp dụng giống nhau cho DB mới và DB đang dùng, chạy lại nhiều lần không trùng:
/// 1. Đổi tên tài khoản theo TT99 (112, 155, 242, 3387, 4211, 4112, 1331...).
/// 2. Bỏ tài khoản cấp 2 TT99 không còn quy định: 1561/1562 → gộp về 156; 5211/5212/5213 → gộp về 521; 4131/4132 → gộp về 413.
/// 3. Thêm tài khoản cấp 2 theo TT99 cho 627 (6271…6278), 641 (6411…6418), 642 (6421…6428), 821 (8211 → 82111, 82112; 8212), 3331 (33312), 138 (1383)...
///    Phát sinh cũ đang ghi trên TK cấp 1 627 / 641 được phân loại lại vào TK cấp 2 theo nguồn chứng từ (lương → 6271/6411, khấu hao → 6274/6414...).
///    Tổng số dư của TK cấp 1 không đổi; chỉ chi tiết hơn.
/// 4. Thêm tài khoản mới: 215 (2151–2153), 332, 3525.
/// Mọi tham chiếu tới tài khoản bị gộp / tách (định khoản, cấu hình bút toán, nhóm TSCĐ, CCDC, mẫu định kỳ...) được chuyển theo.
/// </summary>
public static class Tt99Migration
{
    public static async Task RunAsync(ErpDbContext db, ILogger? log = null)
    {
        // ---- 1. Đổi tên (chỉ đổi khi tên còn là tên cũ – không đè tên người dùng đã sửa)
        foreach (var (code, oldNames, newName) in new (string, string[], string)[]
        {
            ("112", new[] { "Tiền gửi ngân hàng", "Tiền gửi Ngân hàng" }, "Tiền gửi không kỳ hạn"),
            ("1121", new[] { "Tiền gửi ngân hàng – VND" }, "Tiền gửi không kỳ hạn – VND"),
            ("1122", new[] { "Tiền gửi ngân hàng – ngoại tệ" }, "Tiền gửi không kỳ hạn – ngoại tệ"),
            ("1331", new[] { "Thuế GTGT được khấu trừ của HHDV" }, "Thuế GTGT được khấu trừ của hàng hóa, dịch vụ"),
            ("155", new[] { "Thành phẩm" }, "Sản phẩm"),
            ("242", new[] { "Chi phí trả trước" }, "Chi phí chờ phân bổ"),
            ("3387", new[] { "Doanh thu chưa thực hiện", "Doanh thu nhận trước" }, "Doanh thu chờ phân bổ"),
            ("4112", new[] { "Thặng dư vốn cổ phần" }, "Thặng dư vốn"),
            ("4211", new[] { "LNST chưa phân phối năm trước", "Lợi nhuận sau thuế chưa phân phối năm trước" }, "Lợi nhuận sau thuế chưa phân phối lũy kế đến cuối năm trước"),
            ("4212", new[] { "LNST chưa phân phối năm nay" }, "Lợi nhuận sau thuế chưa phân phối năm nay"),
            ("5113", new[] { "Doanh thu cung cấp dịch vụ" }, "Doanh thu cung cấp dịch vụ"),
        })
        {
            var a = await db.Accounts.FirstOrDefaultAsync(x => x.Code == code);
            if (a != null && oldNames.Contains(a.Name)) a.Name = newName;
        }
        await db.SaveChangesAsync();

        // ---- 2. Gộp TK cấp 2 không còn trong TT99 về TK cấp 1
        await MergeIntoParentAsync(db, "156", new[] { "1561", "1562", "1567" });
        await MergeIntoParentAsync(db, "521", new[] { "5211", "5212", "5213" });
        await MergeIntoParentAsync(db, "413", new[] { "4131", "4132" });

        // ---- 3. TK cấp 2 theo TT99 cho chi phí sản xuất chung, bán hàng, quản lý
        await SplitAsync(db, "627", "6278", new[]
        {
            ("6271", "Chi phí nhân viên phân xưởng"), ("6272", "Chi phí vật liệu"), ("6273", "Chi phí dụng cụ sản xuất"), ("6274", "Chi phí khấu hao TSCĐ"),
            ("6275", "Thuế, phí, lệ phí"), ("6277", "Chi phí dịch vụ mua ngoài"), ("6278", "Chi phí bằng tiền khác")
        }, s => s switch
        {
            JournalSource.Payroll => "6271", JournalSource.Inventory => "6272", JournalSource.Allocation => "6273", JournalSource.Depreciation or JournalSource.FixedAsset => "6274",
            JournalSource.Tax => "6275", JournalSource.Cash or JournalSource.PurchaseInvoice => "6277", _ => "6278"
        }, depreciation: "6274", tools: "6273", payroll: "6271");
        await SplitAsync(db, "641", "6418", new[]
        {
            ("6411", "Chi phí nhân viên"), ("6412", "Chi phí vật liệu, bao bì"), ("6413", "Chi phí dụng cụ, đồ dùng"), ("6414", "Chi phí khấu hao TSCĐ"),
            ("6415", "Thuế, phí, lệ phí"), ("6417", "Chi phí dịch vụ mua ngoài"), ("6418", "Chi phí bằng tiền khác")
        }, s => s switch
        {
            JournalSource.Payroll => "6411", JournalSource.Inventory => "6412", JournalSource.Allocation => "6413", JournalSource.Depreciation or JournalSource.FixedAsset => "6414",
            JournalSource.Tax => "6415", JournalSource.Cash or JournalSource.PurchaseInvoice => "6417", _ => "6418"
        }, depreciation: "6414", tools: "6413", payroll: "6411");
        await SplitAsync(db, "642", "6428", new[]
        {
            ("6421", "Chi phí nhân viên quản lý"), ("6422", "Chi phí vật liệu quản lý"), ("6423", "Chi phí đồ dùng văn phòng"), ("6424", "Chi phí khấu hao TSCĐ"),
            ("6425", "Thuế, phí và lệ phí"), ("6426", "Chi phí dự phòng"), ("6427", "Chi phí dịch vụ mua ngoài"), ("6428", "Chi phí bằng tiền khác")
        }, s => s switch
        {
            JournalSource.Payroll => "6421", JournalSource.Depreciation or JournalSource.FixedAsset => "6424", JournalSource.Tax => "6425",
            JournalSource.Cash or JournalSource.PurchaseInvoice => "6427", _ => "6428"
        }, depreciation: "6424", tools: "6423", payroll: "6421");

        // ---- Thuế TNDN: 821 → 8211 (82111 hiện hành theo Luật thuế TNDN, 82112 bổ sung theo thuế tối thiểu toàn cầu), 8212 hoãn lại
        await SplitAsync(db, "821", "82111", new[]
        {
            ("8211", "Chi phí thuế TNDN hiện hành"),
            ("82111", "Chi phí thuế thu nhập doanh nghiệp hiện hành theo quy định của Luật thuế thu nhập doanh nghiệp"),
            ("82112", "Chi phí thuế thu nhập doanh nghiệp bổ sung theo quy định về thuế tối thiểu toàn cầu"),
            ("8212", "Chi phí thuế TNDN hoãn lại")
        }, _ => "82111", null, null, null);

        // ---- 4. Tài khoản mới / bổ sung theo TT99
        await AddAsync(db, "33312", "Thuế GTGT hàng nhập khẩu", AccountType.Liability, "3331");
        await AddAsync(db, "1383", "Thuế TTĐB của hàng nhập khẩu", AccountType.Asset, "138");
        await AddAsync(db, "332", "Phải trả cổ tức, lợi nhuận", AccountType.Liability, null);
        await AddAsync(db, "221", "Đầu tư vào công ty con", AccountType.Asset, null);
        var a221 = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "221"); if (a221 != null && !a221.TrackPartner) { a221.TrackPartner = true; await db.SaveChangesAsync(); }
        await AddAsync(db, "215", "Tài sản sinh học", AccountType.Asset, null);
        await AddAsync(db, "2151", "Súc vật nuôi cho sản phẩm định kỳ", AccountType.Asset, "215");
        await AddAsync(db, "2152", "Súc vật nuôi lấy sản phẩm một lần", AccountType.Asset, "215");
        await AddAsync(db, "2153", "Cây trồng theo mùa vụ hoặc lấy sản phẩm một lần", AccountType.Asset, "215");
        await AddAsync(db, "3335", "Thuế thu nhập cá nhân", AccountType.Liability, "333");
        await db.SaveChangesAsync();
        log?.LogInformation("Hệ thống tài khoản đã chuẩn theo Thông tư 99/2025/TT-BTC.");
    }

    private static async Task AddAsync(ErpDbContext db, string code, string name, AccountType type, string? parent)
    {
        if (await db.Accounts.AnyAsync(a => a.Code == code)) return;
        if (parent != null)
        {
            var p = await db.Accounts.FirstOrDefaultAsync(a => a.Code == parent);
            if (p == null) return;
            if (p.IsPostable && await db.JournalLines.IgnoreQueryFilters().AnyAsync(l => l.AccountId == p.Id)) return; // TK cha đang có phát sinh: không tách
            p.IsPostable = false;
        }
        db.Accounts.Add(new Account { Code = code, Name = name, Type = type });
        await db.SaveChangesAsync();
    }

    /// <summary>Gộp các TK con vào TK cha: chuyển mọi phát sinh và tham chiếu sang TK cha, xóa TK con, TK cha được hạch toán trực tiếp</summary>
    private static async Task MergeIntoParentAsync(ErpDbContext db, string parentCode, string[] childCodes)
    {
        var parent = await db.Accounts.FirstOrDefaultAsync(a => a.Code == parentCode); if (parent == null) return;
        var children = await db.Accounts.Where(a => childCodes.Contains(a.Code)).ToListAsync();
        if (children.Count == 0) return;
        foreach (var c in children)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE [JournalLines] SET [AccountId] = {0} WHERE [AccountId] = {1}", parent.Id, c.Id);
            await RemapReferencesAsync(db, c.Id, parent.Id);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [Accounts] WHERE [Id] = {0}", c.Id);
        }
        db.ChangeTracker.Clear();
        parent = await db.Accounts.FirstAsync(a => a.Code == parentCode);
        if (!await db.Accounts.AnyAsync(a => a.Code.StartsWith(parentCode) && a.Code.Length > parentCode.Length)) parent.IsPostable = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Thêm TK cấp 2 theo TT99 cho TK cấp 1. Phát sinh cũ trên TK cấp 1 được phân loại lại vào TK cấp 2 theo nguồn chứng từ;
    /// tham chiếu khác (cấu hình bút toán, mẫu định kỳ...) chuyển sang TK cấp 2 mặc định; TK cấp 1 không hạch toán trực tiếp nữa.
    /// </summary>
    private static async Task SplitAsync(ErpDbContext db, string parentCode, string defaultChild, (string Code, string Name)[] children,
        Func<JournalSource, string> bySource, string? depreciation, string? tools, string? payroll)
    {
        var parent = await db.Accounts.FirstOrDefaultAsync(a => a.Code == parentCode); if (parent == null) return;
        var ids = new Dictionary<string, int>();
        foreach (var (code, name) in children)
        {
            var a = await db.Accounts.FirstOrDefaultAsync(x => x.Code == code);
            if (a == null) { a = new Account { Code = code, Name = name, Type = parent.Type }; db.Accounts.Add(a); await db.SaveChangesAsync(); }
            ids[code] = a.Id;
        }
        // TK có TK con (VD 8211 có 82111) thì không hạch toán trực tiếp
        foreach (var (code, _) in children)
            if (children.Any(x => x.Code.Length > code.Length && x.Code.StartsWith(code))) { var a = await db.Accounts.FirstAsync(x => x.Code == code); a.IsPostable = false; }
        foreach (var (code, _) in children)
            if (!children.Any(x => x.Code.Length > code.Length && x.Code.StartsWith(code))) { var a = await db.Accounts.FirstAsync(x => x.Code == code); if (!await db.Accounts.AnyAsync(x => x.Code.StartsWith(code) && x.Code.Length > code.Length)) a.IsPostable = true; }
        await db.SaveChangesAsync();

        if (parent.IsPostable || await db.JournalLines.IgnoreQueryFilters().AnyAsync(l => l.AccountId == parent.Id))
        {
            // Phân loại lại phát sinh cũ theo nguồn chứng từ
            foreach (JournalSource s in Enum.GetValues<JournalSource>().Where(x => x is not (JournalSource.Closing or JournalSource.Production)))
            {
                var target = bySource(s); if (!ids.TryGetValue(target, out var tid)) tid = ids[defaultChild];
                await db.Database.ExecuteSqlRawAsync("UPDATE l SET l.[AccountId] = {0} FROM [JournalLines] l JOIN [JournalEntries] e ON e.[Id] = l.[JournalEntryId] WHERE l.[AccountId] = {1} AND e.[Source] = {2}", tid, parent.Id, (int)s);
            }
            await DistributeTransfersAsync(db, parent.Id, ids.Values.ToList(), ids[defaultChild]);
            await db.Database.ExecuteSqlRawAsync("UPDATE [JournalLines] SET [AccountId] = {0} WHERE [AccountId] = {1}", ids[defaultChild], parent.Id);
            if (depreciation != null) await db.Database.ExecuteSqlRawAsync("UPDATE [AssetCategories] SET [ExpenseAccountId] = {0} WHERE [ExpenseAccountId] = {1}", ids[depreciation], parent.Id);
            if (tools != null) await db.Database.ExecuteSqlRawAsync("UPDATE [ToolItems] SET [ExpenseAccountId] = {0} WHERE [ExpenseAccountId] = {1}", ids[tools], parent.Id);
            if (payroll != null)
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE [Employees] SET [ExpenseAccount] = {0} WHERE [ExpenseAccount] = {1}", payroll, parentCode);
                await db.Database.ExecuteSqlRawAsync("UPDATE [PayrollLines] SET [ExpenseAccount] = {0} WHERE [ExpenseAccount] = {1}", payroll, parentCode);
            }
            await RemapReferencesAsync(db, parent.Id, ids[defaultChild]);
        }
        db.ChangeTracker.Clear();
        parent = await db.Accounts.FirstAsync(a => a.Code == parentCode);
        parent.IsPostable = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Bút toán kết chuyển (kết chuyển giá thành, kết chuyển cuối kỳ) đang ghi trên TK cấp 1: tách theo số phát sinh trong tháng của từng TK cấp 2
    /// (kết chuyển đủ thì mỗi TK cấp 2 về 0; kết chuyển một phần thì chia theo tỷ lệ), phần chênh lệch còn lại vào TK cấp 2 mặc định.
    /// </summary>
    private static async Task DistributeTransfersAsync(ErpDbContext db, int parentId, List<int> childIds, int defaultId)
    {
        var lines = await db.JournalLines.IgnoreQueryFilters().Include(l => l.JournalEntry)
            .Where(l => l.AccountId == parentId && (l.JournalEntry!.Source == JournalSource.Production || l.JournalEntry.Source == JournalSource.Closing)).ToListAsync();
        if (lines.Count == 0) return;
        var bal = (await db.JournalLines.IgnoreQueryFilters().Where(l => childIds.Contains(l.AccountId) && l.JournalEntry!.Source != JournalSource.Production && l.JournalEntry.Source != JournalSource.Closing)
            .Select(l => new { l.AccountId, l.JournalEntry!.Date.Year, l.JournalEntry.Date.Month, N = l.Debit - l.Credit }).ToListAsync())
            .GroupBy(x => (x.Year, x.Month, x.AccountId)).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        foreach (var l in lines.OrderBy(x => x.JournalEntry!.Date).ThenBy(x => x.JournalEntry!.Source == JournalSource.Closing).ThenBy(x => x.Id))
        {
            var amt = l.Credit - l.Debit; var (y, mo) = (l.JournalEntry!.Date.Year, l.JournalEntry.Date.Month);
            if (amt <= 0) { l.AccountId = defaultId; continue; }
            var avail = childIds.Select(c => (Id: c, B: bal.GetValueOrDefault((y, mo, c)))).Where(x => x.B > 0).OrderBy(x => x.Id).ToList();
            var total = avail.Sum(x => x.B); var parts = new List<(int Id, decimal A)>();
            if (total <= 0) parts.Add((defaultId, amt));
            else if (amt >= total) { parts.AddRange(avail.Select(x => (x.Id, x.B))); if (amt > total) parts.Add((defaultId, amt - total)); }
            else { decimal used = 0; for (var k = 0; k < avail.Count; k++) { var a = k == avail.Count - 1 ? amt - used : Math.Round(amt * avail[k].B / total, 0); used += a; parts.Add((avail[k].Id, a)); } }
            foreach (var (id, a) in parts) bal[(y, mo, id)] = bal.GetValueOrDefault((y, mo, id)) - a;
            var merged = parts.Where(p => p.A != 0).GroupBy(p => p.Id).Select(g => (Id: g.Key, A: g.Sum(x => x.A))).ToList();
            if (merged.Count == 0) { l.AccountId = defaultId; continue; }
            l.AccountId = merged[0].Id; l.Debit = 0; l.Credit = merged[0].A;
            foreach (var (id, a) in merged.Skip(1))
                db.JournalLines.Add(new JournalLine { JournalEntryId = l.JournalEntryId, AccountId = id, Debit = 0, Credit = a, PartnerId = l.PartnerId, Description = l.Description, Department = l.Department });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Chuyển mọi khóa ngoại trỏ tới TK cũ (trừ định khoản – đã xử lý riêng) sang TK mới, dựa trên mô hình EF</summary>
    private static async Task RemapReferencesAsync(ErpDbContext db, int fromId, int toId)
    {
        var account = db.Model.FindEntityType(typeof(Account))!;
        foreach (var et in db.Model.GetEntityTypes())
        {
            if (et.ClrType == typeof(JournalLine)) continue;
            var table = et.GetTableName(); if (table == null) continue;
            var store = StoreObjectIdentifier.Table(table, et.GetSchema());
            foreach (var fk in et.GetForeignKeys().Where(f => f.PrincipalEntityType == account))
            {
                var col = fk.Properties[0].GetColumnName(store); if (col == null) continue;
                await db.Database.ExecuteSqlRawAsync($"UPDATE [{table}] SET [{col}] = {{0}} WHERE [{col}] = {{1}}", toId, fromId);
            }
        }
    }
}
