using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using ERP.Web.Models.Entities;

namespace ERP.Web.Infrastructure;

/// <summary>Ngữ cảnh trang hiện tại để xác định mục menu đang chọn</summary>
public class MenuContext
{
    public string Controller { get; init; } = "";
    public string Action { get; init; } = "";
    public IQueryCollection Query { get; init; } = default!;
    public bool Is(string c, string? a = null) => Controller == c && (a == null || Action == a);
    public string Q(string key) => Query[key].ToString();
    public bool QIs(string key, string val) => string.Equals(Q(key), val, StringComparison.OrdinalIgnoreCase);
}

public class MenuItem
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required string Controller { get; init; }
    public string Action { get; init; } = "Index";
    public object? Route { get; init; }
    public Func<ClaimsPrincipal, bool>? Visible { get; init; }
    public required Func<MenuContext, bool> Active { get; init; }
    public string Url(IUrlHelper url) => url.Action(Action, Controller, Route) ?? "/";
}

public class MenuGroup
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    /// <summary>Màu nền biểu tượng trên trang chủ</summary>
    public string Tint { get; init; } = "#eef4ff";
    public required MenuItem[] Items { get; init; }
}

/// <summary>Khai báo menu tập trung: dùng cho thanh menu (mọi vị trí), trang chủ và tính năng yêu thích</summary>
public static class Menu
{
    private static bool Fin(ClaimsPrincipal u) => u.SeesFinance();
    private static bool Admin(ClaimsPrincipal u) => u.IsAdmin();

    public static readonly MenuItem Home = new()
    {
        Key = "home", Title = "Trang chủ", Icon = "bi-house-door", Controller = "Home", Active = m => m.Is("Home")
    };

    public static readonly MenuGroup[] Groups =
    {
        new() { Key = "gl", Title = "Kế toán tổng hợp", Icon = "bi-journal-bookmark", Tint = "#eef4ff", Items = new MenuItem[]
        {
            new() { Key = "journals", Title = "Chứng từ kế toán", Icon = "bi-journal-text", Controller = "Journals", Visible = Fin, Active = m => m.Is("Journals") },
            new() { Key = "reports", Title = "Báo cáo tài chính", Icon = "bi-graph-up-arrow", Controller = "Reports", Action = "TrialBalance", Visible = Fin, Active = m => m.Is("Reports") && m.Action != "Periods" },
            new() { Key = "periods", Title = "Khóa sổ & kết chuyển", Icon = "bi-lock", Controller = "Reports", Action = "Periods", Visible = Fin, Active = m => m.Is("Reports", "Periods") },
            new() { Key = "posting-rules", Title = "Cấu hình bút toán", Icon = "bi-sliders", Controller = "PostingRules", Visible = Fin, Active = m => m.Is("PostingRules") },
        }},
        new() { Key = "debts", Title = "Phải thu · Phải trả", Icon = "bi-arrow-down-up", Tint = "#ecfdf5", Items = new MenuItem[]
        {
            new() { Key = "ar", Title = "Công nợ phải thu", Icon = "bi-box-arrow-in-down-left", Controller = "Debts", Route = new { kind = "ar" }, Visible = Fin, Active = m => m.Is("Debts") && (m.QIs("kind", "ar") || m.Q("kind") == "") },
            new() { Key = "ap", Title = "Công nợ phải trả", Icon = "bi-box-arrow-up-right", Controller = "Debts", Route = new { kind = "ap" }, Visible = Fin, Active = m => m.Is("Debts") && m.QIs("kind", "ap") },
        }},
        new() { Key = "money", Title = "Tài sản · Dòng tiền", Icon = "bi-bank", Tint = "#fefce8", Items = new MenuItem[]
        {
            new() { Key = "assets", Title = "Tài sản cố định", Icon = "bi-building", Controller = "Assets", Visible = Fin, Active = m => m.Is("Assets") },
            new() { Key = "cash", Title = "Quỹ & ngân hàng", Icon = "bi-wallet2", Controller = "Cash", Visible = Fin, Active = m => m.Is("Cash") && m.Action != "Flow" },
            new() { Key = "cashflow", Title = "Báo cáo dòng tiền", Icon = "bi-cash-stack", Controller = "Cash", Action = "Flow", Visible = Fin, Active = m => m.Is("Cash", "Flow") },
        }},
        new() { Key = "invoices", Title = "Hóa đơn", Icon = "bi-receipt", Tint = "#fdf2f8", Items = new MenuItem[]
        {
            new() { Key = "inv-sales", Title = "Hóa đơn bán ra", Icon = "bi-receipt-cutoff", Controller = "Invoices", Route = new { direction = "Sales" }, Visible = Fin,
                    Active = m => m.Is("Invoices") && m.Action != "VatReport" && (m.QIs("direction", "Sales") || (m.Action == "Index" && m.Q("direction") == "")) },
            new() { Key = "inv-purchase", Title = "Hóa đơn mua vào", Icon = "bi-file-earmark-text", Controller = "Invoices", Route = new { direction = "Purchase" }, Visible = Fin, Active = m => m.Is("Invoices") && m.QIs("direction", "Purchase") },
            new() { Key = "vat", Title = "Bảng kê thuế GTGT", Icon = "bi-percent", Controller = "Invoices", Action = "VatReport", Visible = Fin, Active = m => m.Is("Invoices", "VatReport") },
        }},
        new() { Key = "stock", Title = "Kho", Icon = "bi-box-seam", Tint = "#f0f9ff", Items = new MenuItem[]
        {
            new() { Key = "stock-levels", Title = "Tồn kho", Icon = "bi-boxes", Controller = "Inventory", Active = m => m.Is("Inventory", "Index") || m.Is("Inventory", "StockCard") },
            new() { Key = "stock-docs", Title = "Phiếu nhập / xuất kho", Icon = "bi-arrow-left-right", Controller = "Inventory", Action = "Docs",
                    Active = m => m.Is("Inventory") && m.Action is not ("Index" or "StockCard" or "Warehouses") },
        }},
        new() { Key = "trade", Title = "Mua hàng · Bán hàng", Icon = "bi-cart3", Tint = "#fff1f2", Items = new MenuItem[]
        {
            new() { Key = "po", Title = "Đơn mua hàng", Icon = "bi-cart-plus", Controller = "PurchaseOrders", Active = m => m.Is("PurchaseOrders") },
            new() { Key = "so", Title = "Đơn bán hàng", Icon = "bi-bag-check", Controller = "SalesOrders", Active = m => m.Is("SalesOrders") },
        }},
        new() { Key = "mfg", Title = "Sản xuất", Icon = "bi-gear-wide-connected", Tint = "#f8fafc", Items = new MenuItem[]
        {
            new() { Key = "mo", Title = "Lệnh sản xuất", Icon = "bi-tools", Controller = "Production", Active = m => m.Is("Production") && m.Action != "Mrp" },
            new() { Key = "bom", Title = "Định mức (BOM)", Icon = "bi-diagram-3", Controller = "Boms", Active = m => m.Is("Boms") },
            new() { Key = "mrp", Title = "Hoạch định vật tư", Icon = "bi-clipboard-data", Controller = "Production", Action = "Mrp", Active = m => m.Is("Production", "Mrp") },
        }},
        new() { Key = "quality", Title = "Quản lý chất lượng", Icon = "bi-patch-check", Tint = "#ecfeff", Items = new MenuItem[]
        {
            new() { Key = "qc-inspections", Title = "Phiếu kiểm tra (KCS)", Icon = "bi-clipboard-check", Controller = "QcInspections", Active = m => m.Is("QcInspections") },
            new() { Key = "qc-ncrs", Title = "Xử lý hàng không đạt (NCR)", Icon = "bi-exclamation-octagon", Controller = "QcNcrs", Active = m => m.Is("QcNcrs") },
            new() { Key = "qc-plans", Title = "Kế hoạch & tiêu chí kiểm tra", Icon = "bi-list-check", Controller = "QcPlans", Active = m => m.Is("QcPlans") },
            new() { Key = "qc-reports", Title = "Báo cáo chất lượng", Icon = "bi-bar-chart-line", Controller = "QcReports", Active = m => m.Is("QcReports") },
        }},
        new() { Key = "catalogs", Title = "Danh mục", Icon = "bi-collection", Tint = "#f5f3ff", Items = new MenuItem[]
        {
            new() { Key = "items", Title = "Hàng hóa, vật tư", Icon = "bi-tags", Controller = "Items", Active = m => m.Is("Items") },
            new() { Key = "customers", Title = "Khách hàng", Icon = "bi-person-badge", Controller = "Partners", Route = new { type = "customer" },
                    Active = m => m.Is("Partners") && (m.QIs("type", "customer") || m.Q("type") == "") },
            new() { Key = "vendors", Title = "Nhà cung cấp", Icon = "bi-truck", Controller = "Partners", Route = new { type = "vendor" }, Active = m => m.Is("Partners") && m.QIs("type", "vendor") },
            new() { Key = "warehouses", Title = "Kho hàng", Icon = "bi-houses", Controller = "Inventory", Action = "Warehouses", Active = m => m.Is("Inventory", "Warehouses") },
            new() { Key = "accounts", Title = "Hệ thống tài khoản", Icon = "bi-list-columns-reverse", Controller = "Accounts", Visible = Fin, Active = m => m.Is("Accounts") },
            new() { Key = "asset-cats", Title = "Nhóm tài sản cố định", Icon = "bi-buildings", Controller = "AssetCategories", Visible = Fin, Active = m => m.Is("AssetCategories") },
            new() { Key = "units", Title = "Đơn vị tính", Icon = "bi-rulers", Controller = "Catalogs", Route = new { type = "Unit" }, Active = m => m.Is("Catalogs") && (m.QIs("type", "Unit") || m.Q("type") == "") },
            new() { Key = "depts", Title = "Phòng ban / bộ phận", Icon = "bi-diagram-2", Controller = "Catalogs", Route = new { type = "Department" }, Active = m => m.Is("Catalogs") && m.QIs("type", "Department") },
        }},
        new() { Key = "system", Title = "Hệ thống", Icon = "bi-shield-lock", Tint = "#f1f5f9", Items = new MenuItem[]
        {
            new() { Key = "users", Title = "Người dùng & phân quyền", Icon = "bi-person-gear", Controller = "Users", Visible = Admin, Active = m => m.Is("Users") },
            new() { Key = "ui", Title = "Cấu hình giao diện", Icon = "bi-palette", Controller = "Preferences", Active = m => m.Is("Preferences") },
        }},
    };

    public static IEnumerable<MenuItem> VisibleItems(MenuGroup g, ClaimsPrincipal u) => g.Items.Where(i => i.Visible == null || i.Visible(u));

    public static IEnumerable<(MenuGroup Group, MenuItem[] Items)> VisibleGroups(ClaimsPrincipal u) =>
        Groups.Select(g => (g, VisibleItems(g, u).ToArray())).Where(x => x.Item2.Length > 0);

    public static MenuItem? FindActive(MenuContext ctx) =>
        Home.Active(ctx) ? Home : Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Active(ctx));

    public static MenuContext Context(Microsoft.AspNetCore.Mvc.Rendering.ViewContext vc) => new()
    {
        Controller = vc.RouteData.Values["controller"]?.ToString() ?? "",
        Action = vc.RouteData.Values["action"]?.ToString() ?? "",
        Query = vc.HttpContext.Request.Query
    };
}
