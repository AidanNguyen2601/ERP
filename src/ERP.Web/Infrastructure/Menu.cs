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
            new() { Key = "reports", Title = "Báo cáo tài chính", Icon = "bi-graph-up-arrow", Controller = "Reports", Action = "TrialBalance", Visible = Fin, Active = m => (m.Is("Reports") && m.Action != "Periods") || m.Is("FinancialNotes") },
            new() { Key = "periods", Title = "Khóa sổ & kết chuyển", Icon = "bi-lock", Controller = "Reports", Action = "Periods", Visible = Fin, Active = m => m.Is("Reports", "Periods") },
            new() { Key = "reconcile", Title = "Đối chiếu phân hệ – kế toán", Icon = "bi-check2-square", Controller = "Reconciliation", Visible = Fin, Active = m => m.Is("Reconciliation") },
            new() { Key = "posting-rules", Title = "Cấu hình bút toán", Icon = "bi-sliders", Controller = "PostingRules", Visible = Fin, Active = m => m.Is("PostingRules") },
            new() { Key = "recurring", Title = "Bút toán định kỳ & phân bổ", Icon = "bi-arrow-repeat", Controller = "Recurring", Visible = Fin, Active = m => m.Is("Recurring") },
            new() { Key = "fx", Title = "Tiền tệ & tỷ giá", Icon = "bi-currency-exchange", Controller = "Settings", Action = "Currencies", Visible = Fin, Active = m => m.Is("Settings", "Currencies") },
            new() { Key = "budgets", Title = "Ngân sách", Icon = "bi-piggy-bank", Controller = "Budgets", Visible = Fin, Active = m => m.Is("Budgets") },
            new() { Key = "consolidation", Title = "Hợp nhất báo cáo", Icon = "bi-diagram-3-fill", Controller = "Group", Action = "Consolidation", Visible = Fin, Active = m => m.Is("Group", "Consolidation") },
            new() { Key = "standards", Title = "Báo cáo theo chuẩn mực khác", Icon = "bi-translate", Controller = "Group", Action = "Standards", Visible = Fin, Active = m => m.Is("Group", "Standards") },
        }},
        new() { Key = "debts", Title = "Phải thu · Phải trả", Icon = "bi-arrow-down-up", Tint = "#ecfdf5", Items = new MenuItem[]
        {
            new() { Key = "ar", Title = "Công nợ phải thu", Icon = "bi-box-arrow-in-down-left", Controller = "Debts", Route = new { kind = "ar" }, Visible = Fin, Active = m => m.Is("Debts") && (m.QIs("kind", "ar") || m.Q("kind") == "") },
            new() { Key = "ap", Title = "Công nợ phải trả", Icon = "bi-box-arrow-up-right", Controller = "Debts", Route = new { kind = "ap" }, Visible = Fin, Active = m => m.Is("Debts") && m.QIs("kind", "ap") },
            new() { Key = "contracts", Title = "Hợp đồng", Icon = "bi-file-earmark-ruled", Controller = "Contracts", Visible = u => u.InAny(AppRoles.FinanceView + "," + AppRoles.Sales + "," + AppRoles.Purchasing), Active = m => m.Is("Contracts") },
            new() { Key = "offsets", Title = "Bù trừ công nợ", Icon = "bi-arrow-left-right", Controller = "Offsets", Visible = Fin, Active = m => m.Is("Offsets", "Index") },
            new() { Key = "ap-schedule", Title = "Lịch thanh toán", Icon = "bi-calendar-week", Controller = "Offsets", Action = "Schedule", Visible = Fin, Active = m => m.Is("Offsets", "Schedule") },
        }},
        new() { Key = "money", Title = "Tài sản · Dòng tiền", Icon = "bi-bank", Tint = "#fefce8", Items = new MenuItem[]
        {
            new() { Key = "assets", Title = "Tài sản cố định", Icon = "bi-building", Controller = "Assets", Visible = Fin, Active = m => m.Is("Assets") },
            new() { Key = "cash", Title = "Quỹ & ngân hàng", Icon = "bi-wallet2", Controller = "Cash", Visible = Fin, Active = m => m.Is("Cash") && m.Action != "Flow" },
            new() { Key = "cashflow", Title = "Báo cáo dòng tiền", Icon = "bi-cash-stack", Controller = "Cash", Action = "Flow", Visible = Fin, Active = m => m.Is("Cash", "Flow") },
            new() { Key = "bank-recon", Title = "Đối chiếu sao kê ngân hàng", Icon = "bi-bank2", Controller = "Bank", Visible = Fin, Active = m => m.Is("Bank") },
            new() { Key = "tools", Title = "Công cụ dụng cụ", Icon = "bi-hammer", Controller = "Tools", Visible = Fin, Active = m => m.Is("Tools") && m.Action != "Transfers" },
            new() { Key = "asset-transfer", Title = "Điều chuyển tài sản", Icon = "bi-truck-flatbed", Controller = "Tools", Action = "Transfers", Visible = Fin, Active = m => m.Is("Tools", "Transfers") },
        }},
        new() { Key = "invoices", Title = "Hóa đơn", Icon = "bi-receipt", Tint = "#fdf2f8", Items = new MenuItem[]
        {
            new() { Key = "inv-sales", Title = "Hóa đơn bán ra", Icon = "bi-receipt-cutoff", Controller = "Invoices", Route = new { direction = "Sales" }, Visible = Fin,
                    Active = m => m.Is("Invoices") && m.Action != "VatReport" && (m.QIs("direction", "Sales") || (m.Action == "Index" && m.Q("direction") == "")) },
            new() { Key = "inv-purchase", Title = "Hóa đơn mua vào", Icon = "bi-file-earmark-text", Controller = "Invoices", Route = new { direction = "Purchase" }, Visible = Fin, Active = m => m.Is("Invoices") && m.QIs("direction", "Purchase") },
            new() { Key = "credit-notes", Title = "Hàng bán trả lại / giảm giá", Icon = "bi-arrow-return-left", Controller = "SalesCreditNotes", Visible = Fin, Active = m => m.Is("SalesCreditNotes") },
            new() { Key = "vat", Title = "Bảng kê thuế GTGT", Icon = "bi-percent", Controller = "Invoices", Action = "VatReport", Visible = Fin, Active = m => m.Is("Invoices", "VatReport") },
            new() { Key = "vat-return", Title = "Tờ khai thuế GTGT", Icon = "bi-file-earmark-text", Controller = "Tax", Action = "Vat", Visible = Fin, Active = m => m.Is("Tax", "Vat") },
            new() { Key = "tax-match", Title = "Đối chiếu HĐ với cổng thuế", Icon = "bi-check2-all", Controller = "Tax", Action = "Match", Visible = Fin, Active = m => m.Is("Tax", "Match") },
            new() { Key = "cit", Title = "Thuế TNDN", Icon = "bi-building-check", Controller = "Tax", Action = "Cit", Visible = Fin, Active = m => m.Is("Tax", "Cit") },
        }},
        new() { Key = "stock", Title = "Kho", Icon = "bi-box-seam", Tint = "#f0f9ff", Items = new MenuItem[]
        {
            new() { Key = "stock-levels", Title = "Tồn kho", Icon = "bi-boxes", Controller = "Inventory", Active = m => m.Is("Inventory", "Index") || m.Is("Inventory", "StockCard") },
            new() { Key = "stock-docs", Title = "Phiếu nhập / xuất kho", Icon = "bi-arrow-left-right", Controller = "Inventory", Action = "Docs",
                    Active = m => m.Is("Inventory") && m.Action is not ("Index" or "StockCard" or "Warehouses") },
            new() { Key = "scale", Title = "Phiếu cân điện tử", Icon = "bi-speedometer", Controller = "Scale", Active = m => m.Is("Scale") },
            new() { Key = "stock-alerts", Title = "Cảnh báo tồn kho", Icon = "bi-exclamation-triangle", Controller = "Stock", Action = "Alerts", Active = m => m.Is("Stock", "Alerts") },
            new() { Key = "stock-lots", Title = "Lô, serial & hạn dùng", Icon = "bi-upc-scan", Controller = "Stock", Action = "Lots", Active = m => m.Is("Stock") && m.Action is "Lots" or "Trace" },
            new() { Key = "stock-bins", Title = "Vị trí lưu kho", Icon = "bi-grid-3x2-gap", Controller = "Stock", Action = "Bins", Active = m => m.Is("Stock", "Bins") },
            new() { Key = "stock-pallets", Title = "Pallet", Icon = "bi-box-seam", Controller = "Stock", Action = "Pallets", Active = m => m.Is("Stock", "Pallets") },
            new() { Key = "stocktake", Title = "Kiểm kê kho", Icon = "bi-clipboard-check", Controller = "Stock", Action = "Stocktakes", Active = m => m.Is("Stock") && m.Action is "Stocktakes" or "Stocktake" },
            new() { Key = "stock-aging", Title = "Tuổi hàng tồn", Icon = "bi-hourglass-split", Controller = "Stock", Action = "Aging", Active = m => m.Is("Stock", "Aging") },
            new() { Key = "stock-labels", Title = "In tem mã vạch", Icon = "bi-upc", Controller = "Stock", Action = "Labels", Active = m => m.Is("Stock") && m.Action is "Labels" or "PrintLabels" },
        }},
        new() { Key = "purchase", Title = "Mua hàng", Icon = "bi-cart3", Tint = "#fff1f2", Items = new MenuItem[]
        {
            new() { Key = "pr", Title = "Đề nghị mua hàng", Icon = "bi-card-checklist", Controller = "Requisitions", Active = m => m.Is("Requisitions") },
            new() { Key = "rfq", Title = "Yêu cầu báo giá (RFQ)", Icon = "bi-envelope-paper", Controller = "Rfq", Visible = u => u.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director), Active = m => m.Is("Rfq") },
            new() { Key = "po", Title = "Đơn mua hàng", Icon = "bi-cart-plus", Controller = "PurchaseOrders", Active = m => m.Is("PurchaseOrders") },
            new() { Key = "suppliers", Title = "Đánh giá nhà cung cấp", Icon = "bi-award", Controller = "Suppliers", Visible = u => u.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Quality), Active = m => m.Is("Suppliers") },
            new() { Key = "match3", Title = "Đối chiếu 3 bên", Icon = "bi-check2-square", Controller = "Procurement", Action = "Match", Visible = u => u.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Warehouse), Active = m => m.Is("Procurement", "Match") },
            new() { Key = "landed", Title = "Chi phí mua hàng", Icon = "bi-truck", Controller = "Procurement", Action = "LandedCosts", Visible = u => u.InAny(AppRoles.PurchaseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director + "," + AppRoles.Warehouse), Active = m => m.Is("Procurement", "LandedCosts") },
        }},
        new() { Key = "sales", Title = "Bán hàng", Icon = "bi-bag-check", Tint = "#fdf4ff", Items = new MenuItem[]
        {
            new() { Key = "crm", Title = "CRM – khách hàng & cơ hội", Icon = "bi-bullseye", Controller = "Crm", Visible = u => u.InAny(AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director), Active = m => m.Is("Crm") },
            new() { Key = "quotes", Title = "Báo giá bán hàng", Icon = "bi-file-earmark-text", Controller = "Quotes", Visible = u => u.InAny(AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director), Active = m => m.Is("Quotes") },
            new() { Key = "so", Title = "Đơn bán hàng", Icon = "bi-bag-check", Controller = "SalesOrders", Active = m => m.Is("SalesOrders") },
            new() { Key = "shipments", Title = "Giao hàng & vận chuyển", Icon = "bi-truck-flatbed", Controller = "Shipments", Visible = u => u.InAny(AppRoles.SalesTeam + "," + AppRoles.WarehouseTeam + "," + AppRoles.Accountant + "," + AppRoles.Director), Active = m => m.Is("Shipments") },
            new() { Key = "pricing", Title = "Bảng giá & khuyến mại", Icon = "bi-currency-dollar", Controller = "Pricing", Visible = u => u.InAny(AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director), Active = m => m.Is("Pricing") },
            new() { Key = "commission", Title = "Hoa hồng & KPI bán hàng", Icon = "bi-graph-up", Controller = "Commission", Visible = u => u.InAny(AppRoles.SalesTeam + "," + AppRoles.Accountant + "," + AppRoles.Director), Active = m => m.Is("Commission") },
        }},
        new() { Key = "mfg", Title = "Sản xuất", Icon = "bi-gear-wide-connected", Tint = "#f8fafc", Items = new MenuItem[]
        {
            new() { Key = "mo", Title = "Lệnh sản xuất", Icon = "bi-tools", Controller = "Production", Active = m => m.Is("Production") && m.Action is not ("Mrp" or "Outputs" or "Variance") },
            new() { Key = "bom", Title = "Định mức (BOM)", Icon = "bi-diagram-3", Controller = "Boms", Active = m => m.Is("Boms") },
            new() { Key = "wc", Title = "Trung tâm sản xuất", Icon = "bi-building-gear", Controller = "WorkCenters", Active = m => m.Is("WorkCenters") },
            new() { Key = "mps", Title = "Kế hoạch tổng thể (MPS)", Icon = "bi-calendar-range", Controller = "Mps", Active = m => m.Is("Mps") },
            new() { Key = "mrp", Title = "Hoạch định vật tư", Icon = "bi-clipboard-data", Controller = "Production", Action = "Mrp", Active = m => m.Is("Production", "Mrp") },
            new() { Key = "mfgout", Title = "Thống kê sản lượng", Icon = "bi-bar-chart-steps", Controller = "Production", Action = "Outputs", Active = m => m.Is("Production", "Outputs") },
            new() { Key = "mfgvar", Title = "Định mức vs thực tế", Icon = "bi-arrow-left-right", Controller = "Production", Action = "Variance", Visible = u => u.InAny(AppRoles.FinanceView + "," + AppRoles.Production), Active = m => m.Is("Production", "Variance") },
            new() { Key = "outsource", Title = "Gia công ngoài", Icon = "bi-box-arrow-up-right", Controller = "Outsource", Active = m => m.Is("Outsource") },
            new() { Key = "costing", Title = "Giá thành sản phẩm", Icon = "bi-calculator", Controller = "Costing", Visible = u => u.InAny(AppRoles.FinanceView + "," + AppRoles.Production), Active = m => m.Is("Costing") },
        }},
        new() { Key = "maint", Title = "Bảo dưỡng thiết bị", Icon = "bi-wrench-adjustable", Tint = "#fff7ed", Items = new MenuItem[]
        {
            new() { Key = "mt-dash", Title = "Tổng quan bảo dưỡng", Icon = "bi-speedometer2", Controller = "MaintenanceReports", Action = "Index", Active = m => m.Is("MaintenanceReports", "Index") },
            new() { Key = "mt-eq", Title = "Danh mục thiết bị", Icon = "bi-hdd-rack", Controller = "Equipment", Active = m => m.Is("Equipment") && m.Action is not "Types" },
            new() { Key = "mt-types", Title = "Loại bảo dưỡng", Icon = "bi-tags", Controller = "Equipment", Action = "Types", Active = m => m.Is("Equipment", "Types") },
            new() { Key = "mt-plans", Title = "Kế hoạch bảo dưỡng", Icon = "bi-calendar2-check", Controller = "MaintenancePlans", Active = m => m.Is("MaintenancePlans") && m.Action is not "Calendar" },
            new() { Key = "mt-cal", Title = "Lịch bảo dưỡng", Icon = "bi-calendar3", Controller = "MaintenancePlans", Action = "Calendar", Active = m => m.Is("MaintenancePlans", "Calendar") },
            new() { Key = "mt-orders", Title = "Phiếu bảo dưỡng & sửa chữa", Icon = "bi-tools", Controller = "MaintenanceOrders", Active = m => m.Is("MaintenanceOrders") },
            new() { Key = "mt-history", Title = "Lịch sử & chi phí", Icon = "bi-clock-history", Controller = "MaintenanceReports", Action = "History", Active = m => m.Is("MaintenanceReports") && m.Action is "History" or "Costs" },
            new() { Key = "mt-kpi", Title = "MTBF / MTTR", Icon = "bi-graph-up-arrow", Controller = "MaintenanceReports", Action = "Kpi", Active = m => m.Is("MaintenanceReports", "Kpi") },
        }},
        new() { Key = "hr", Title = "Nhân sự · Tiền lương", Icon = "bi-people", Tint = "#fef3c7", Items = new MenuItem[]
        {
            new() { Key = "employees", Title = "Nhân viên", Icon = "bi-person-vcard", Controller = "Payroll", Action = "Employees", Visible = Fin, Active = m => m.Is("Payroll", "Employees") || m.Is("Payroll", "EditEmployee") },
            new() { Key = "attendance", Title = "Chấm công", Icon = "bi-calendar-check", Controller = "Payroll", Action = "Attendance", Visible = Fin, Active = m => m.Is("Payroll", "Attendance") },
            new() { Key = "payroll", Title = "Bảng lương", Icon = "bi-cash-coin", Controller = "Payroll", Visible = Fin, Active = m => m.Is("Payroll") && m.Action is "Index" or "Details" or "Payslip" },
            new() { Key = "hr-contracts", Title = "Hợp đồng lao động", Icon = "bi-file-earmark-person", Controller = "Hr", Action = "Contracts", Visible = Fin, Active = m => m.Is("Hr", "Contracts") || m.Is("Hr", "Profile") },
            new() { Key = "hr-timeclock", Title = "Ca & chấm công ngày", Icon = "bi-fingerprint", Controller = "Hr", Action = "TimeClock", Visible = Fin, Active = m => m.Is("Hr", "TimeClock") || m.Is("Hr", "Shifts") },
            new() { Key = "hr-leave", Title = "Nghỉ phép", Icon = "bi-calendar-x", Controller = "Hr", Action = "Leave", Visible = Fin, Active = m => m.Is("Hr", "Leave") },
            new() { Key = "hr-comp", Title = "Thành phần lương & thưởng", Icon = "bi-sliders", Controller = "Hr", Action = "Components", Visible = Fin, Active = m => m.Is("Hr") && m.Action is "Components" or "Bonuses" },
            new() { Key = "hr-ins", Title = "Bảo hiểm & quyết toán TNCN", Icon = "bi-shield-check", Controller = "Hr", Action = "Insurance", Visible = Fin, Active = m => m.Is("Hr") && m.Action is "Insurance" or "PitFinalization" },
            new() { Key = "hr-compliance", Title = "BHXH, thuế TNCN & tạm ứng", Icon = "bi-bank", Controller = "Compliance", Action = "Pit05", Visible = Fin, Active = m => m.Is("Compliance") },
        }},
        new() { Key = "quality", Title = "Quản lý chất lượng", Icon = "bi-patch-check", Tint = "#ecfeff", Items = new MenuItem[]
        {
            new() { Key = "qc-inspections", Title = "Phiếu kiểm tra (KCS)", Icon = "bi-clipboard-check", Controller = "QcInspections", Active = m => m.Is("QcInspections") },
            new() { Key = "qc-ncrs", Title = "Xử lý hàng không đạt (NCR)", Icon = "bi-exclamation-octagon", Controller = "QcNcrs", Active = m => m.Is("QcNcrs") },
            new() { Key = "qc-plans", Title = "Kế hoạch & tiêu chí kiểm tra", Icon = "bi-list-check", Controller = "QcPlans", Active = m => m.Is("QcPlans") },
            new() { Key = "qc-reports", Title = "Báo cáo chất lượng", Icon = "bi-bar-chart-line", Controller = "QcReports", Active = m => m.Is("QcReports") },
        }},
        new() { Key = "reportcenter", Title = "Báo cáo", Icon = "bi-bar-chart-line", Tint = "#eff6ff", Items = new MenuItem[]
        {
            new() { Key = "report-center", Title = "Trung tâm báo cáo", Icon = "bi-grid-1x2", Controller = "ReportCenter", Active = m => m.Is("ReportCenter") },
            new() { Key = "report-dynamic", Title = "Báo cáo động", Icon = "bi-magic", Controller = "DynamicReports", Active = m => m.Is("DynamicReports") },
            new() { Key = "report-exec", Title = "Dashboard điều hành", Icon = "bi-speedometer2", Controller = "Executive", Visible = Fin, Active = m => m.Is("Executive") },
            new() { Key = "report-sched", Title = "Lịch gửi báo cáo email", Icon = "bi-envelope-paper", Controller = "ReportSchedules", Visible = Fin, Active = m => m.Is("ReportSchedules") },
            new() { Key = "report-alerts", Title = "Cảnh báo tự động", Icon = "bi-bell", Controller = "Alerts", Active = m => m.Is("Alerts") },
            new() { Key = "report-gj", Title = "Sổ nhật ký chung", Icon = "bi-journal-text", Controller = "StandardReports", Action = "GeneralJournal", Visible = Fin, Active = m => m.Is("StandardReports", "GeneralJournal") },
            new() { Key = "report-cf", Title = "Lưu chuyển tiền tệ", Icon = "bi-cash-stack", Controller = "StandardReports", Action = "CashFlowStatement", Visible = Fin, Active = m => m.Is("StandardReports", "CashFlowStatement") },
            new() { Key = "report-nxt", Title = "Nhập – xuất – tồn kho", Icon = "bi-arrow-left-right", Controller = "StandardReports", Action = "StockSummary",
                    Visible = u => u.InAny(AppRoles.FinanceView + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing + "," + AppRoles.Production), Active = m => m.Is("StandardReports", "StockSummary") },
        }},
        new() { Key = "catalogs", Title = "Danh mục", Icon = "bi-collection", Tint = "#f5f3ff", Items = new MenuItem[]
        {
            new() { Key = "items", Title = "Hàng hóa, vật tư", Icon = "bi-tags", Controller = "Items", Active = m => m.Is("Items") && m.Action != "Groups" },
            new() { Key = "item-groups", Title = "Nhóm hàng", Icon = "bi-collection", Controller = "Items", Action = "Groups", Active = m => m.Is("Items", "Groups") },
            new() { Key = "customers", Title = "Khách hàng", Icon = "bi-person-badge", Controller = "Partners", Route = new { type = "customer" },
                    Active = m => m.Is("Partners") && (m.QIs("type", "customer") || m.Q("type") == "") },
            new() { Key = "vendors", Title = "Nhà cung cấp", Icon = "bi-truck", Controller = "Partners", Route = new { type = "vendor" }, Active = m => m.Is("Partners") && m.QIs("type", "vendor") },
            new() { Key = "warehouses", Title = "Kho hàng", Icon = "bi-houses", Controller = "Inventory", Action = "Warehouses", Active = m => m.Is("Inventory", "Warehouses") },
            new() { Key = "accounts", Title = "Hệ thống tài khoản", Icon = "bi-list-columns-reverse", Controller = "Accounts", Visible = Fin, Active = m => m.Is("Accounts") },
            new() { Key = "asset-cats", Title = "Nhóm tài sản cố định", Icon = "bi-buildings", Controller = "AssetCategories", Visible = Fin, Active = m => m.Is("AssetCategories") },
            new() { Key = "units", Title = "Đơn vị tính", Icon = "bi-rulers", Controller = "Catalogs", Route = new { type = "Unit" }, Active = m => m.Is("Catalogs") && (m.QIs("type", "Unit") || m.Q("type") == "") },
            new() { Key = "depts", Title = "Phòng ban / bộ phận", Icon = "bi-diagram-2", Controller = "Catalogs", Route = new { type = "Department" }, Active = m => m.Is("Catalogs") && m.QIs("type", "Department") },
            new() { Key = "import", Title = "Nhập danh mục từ Excel", Icon = "bi-file-earmark-arrow-up", Controller = "Import", Active = m => m.Is("Import") },
        }},
        new() { Key = "system", Title = "Hệ thống", Icon = "bi-shield-lock", Tint = "#f1f5f9", Items = new MenuItem[]
        {
            new() { Key = "approvals", Title = "Duyệt chứng từ", Icon = "bi-check2-circle", Controller = "Approvals", Active = m => m.Is("Approvals") },
            new() { Key = "users", Title = "Người dùng & phân quyền", Icon = "bi-person-gear", Controller = "Users", Visible = Admin, Active = m => m.Is("Users") },
            new() { Key = "permissions", Title = "Ma trận phân quyền", Icon = "bi-grid-3x3-gap", Controller = "Org", Action = "Permissions", Visible = u => u.InAny(AppRoles.Admin + "," + AppRoles.Director), Active = m => m.Is("Org", "Permissions") },
            new() { Key = "org", Title = "Công ty & chi nhánh", Icon = "bi-buildings", Controller = "Org", Visible = Fin, Active = m => m.Is("Org", "Index") },
            new() { Key = "audit", Title = "Nhật ký thao tác", Icon = "bi-clock-history", Controller = "Audit", Visible = u => u.InAny(AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Accountant), Active = m => m.Is("Audit") },
            new() { Key = "backup", Title = "Sao lưu dữ liệu", Icon = "bi-database-down", Controller = "Backup", Visible = u => u.IsAdmin(), Active = m => m.Is("Backup") },
            new() { Key = "channels", Title = "Kênh thông báo (Teams, Zalo)", Icon = "bi-send", Controller = "Notifications", Action = "Channels", Visible = u => u.IsAdmin(), Active = m => m.Is("Notifications", "Channels") },
            new() { Key = "schema", Title = "Phiên bản cơ sở dữ liệu", Icon = "bi-layers", Controller = "Schema", Visible = u => u.IsAdmin(), Active = m => m.Is("Schema") },
            new() { Key = "settings", Title = "Tham số hệ thống", Icon = "bi-gear", Controller = "Settings", Visible = Fin, Active = m => m.Is("Settings", "Index") },
            new() { Key = "api", Title = "Kết nối API & thiết bị", Icon = "bi-plug", Controller = "Settings", Action = "Api", Visible = Admin, Active = m => m.Is("Settings", "Api") },
            new() { Key = "scenarios", Title = "Kịch bản nghiệp vụ", Icon = "bi-signpost-split", Controller = "Scenarios", Active = m => m.Is("Scenarios") },
            new() { Key = "ui", Title = "Cấu hình giao diện", Icon = "bi-palette", Controller = "Preferences", Active = m => m.Is("Preferences") },
        }},
    };

    /// <param name="hidden">Mục bị cấm theo ghi đè quyền (PermissionService.DeniedKeysAsync)</param>
    public static IEnumerable<MenuItem> VisibleItems(MenuGroup g, ClaimsPrincipal u, ISet<string>? hidden = null) => g.Items.Where(i => (i.Visible == null || i.Visible(u)) && (hidden == null || !hidden.Contains(i.Key)));

    public static IEnumerable<(MenuGroup Group, MenuItem[] Items)> VisibleGroups(ClaimsPrincipal u, ISet<string>? hidden = null) =>
        Groups.Select(g => (g, VisibleItems(g, u, hidden).ToArray())).Where(x => x.Item2.Length > 0);

    public static MenuItem? FindActive(MenuContext ctx) =>
        Home.Active(ctx) ? Home : Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Active(ctx));

    public static MenuContext Context(Microsoft.AspNetCore.Mvc.Rendering.ViewContext vc) => new()
    {
        Controller = vc.RouteData.Values["controller"]?.ToString() ?? "",
        Action = vc.RouteData.Values["action"]?.ToString() ?? "",
        Query = vc.HttpContext.Request.Query
    };
}
