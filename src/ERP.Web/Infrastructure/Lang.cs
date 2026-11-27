namespace ERP.Web.Infrastructure;

/// <summary>
/// Đa ngôn ngữ giao diện (Tiếng Việt / English): ngôn ngữ lấy từ cookie erp.lang (đặt khi đăng nhập theo hồ sơ người dùng, hoặc chọn trên thanh tiêu đề).
/// Dịch theo nguyên văn tiếng Việt: menu, nhóm chức năng, tiêu đề trang, nhãn chung của khung giao diện. Văn bản chưa có bản dịch giữ nguyên tiếng Việt.
/// </summary>
public static class Lang
{
    public const string Cookie = "erp.lang";

    public static string Current(HttpContext? ctx) => ctx?.Request.Cookies[Cookie] == "en" ? "en" : "vi";
    public static bool IsEn(HttpContext? ctx) => Current(ctx) == "en";

    public static string T(HttpContext? ctx, string vi) => IsEn(ctx) && En.TryGetValue(vi, out var en) ? en : vi;

    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // Khung giao diện
        ["Trang chủ"] = "Home", ["Cấu hình giao diện"] = "UI settings", ["Đổi mật khẩu"] = "Change password", ["Đăng xuất"] = "Sign out",
        ["Tất cả chi nhánh"] = "All branches", ["Chi nhánh"] = "Branch", ["Ngôn ngữ"] = "Language", ["Chờ duyệt"] = "Pending approval", ["Tìm chức năng..."] = "Find a feature...",
        // Nhóm
        ["Kế toán tổng hợp"] = "General ledger", ["Phải thu · Phải trả"] = "Receivables · Payables", ["Tài sản · Dòng tiền"] = "Assets · Cash", ["Hóa đơn"] = "Invoices · Tax",
        ["Kho"] = "Inventory", ["Mua hàng · Bán hàng"] = "Purchasing · Sales", ["Sản xuất"] = "Manufacturing", ["Nhân sự · Tiền lương"] = "HR · Payroll",
        ["Quản lý chất lượng"] = "Quality", ["Báo cáo"] = "Reports", ["Danh mục"] = "Master data", ["Hệ thống"] = "System",
        // Chức năng
        ["Chứng từ kế toán"] = "Journal entries", ["Báo cáo tài chính"] = "Financial statements", ["Khóa sổ & kết chuyển"] = "Period close", ["Đối chiếu phân hệ – kế toán"] = "Subledger reconciliation",
        ["Cấu hình bút toán"] = "Posting rules", ["Bút toán định kỳ & phân bổ"] = "Recurring & allocation entries", ["Tiền tệ & tỷ giá"] = "Currencies & FX rates", ["Ngân sách"] = "Budgets",
        ["Hợp nhất báo cáo"] = "Consolidation", ["Báo cáo theo chuẩn mực khác"] = "Alternate GAAP reporting",
        ["Công nợ phải thu"] = "Accounts receivable", ["Công nợ phải trả"] = "Accounts payable", ["Hợp đồng"] = "Contracts", ["Bù trừ công nợ"] = "AR/AP netting", ["Lịch thanh toán"] = "Payment schedule",
        ["Tài sản cố định"] = "Fixed assets", ["Quỹ & ngân hàng"] = "Cash & bank", ["Báo cáo dòng tiền"] = "Cash flow", ["Đối chiếu sao kê ngân hàng"] = "Bank reconciliation",
        ["Công cụ dụng cụ"] = "Tools & supplies", ["Điều chuyển tài sản"] = "Asset transfers",
        ["Hóa đơn bán ra"] = "Sales invoices", ["Hóa đơn mua vào"] = "Purchase invoices", ["Hàng bán trả lại / giảm giá"] = "Sales returns / allowances", ["Bảng kê thuế GTGT"] = "VAT listing",
        ["Tờ khai thuế GTGT"] = "VAT return", ["Đối chiếu HĐ với cổng thuế"] = "Tax portal invoice matching", ["Thuế TNDN"] = "Corporate income tax",
        ["Tồn kho"] = "Stock on hand", ["Phiếu nhập / xuất kho"] = "Stock receipts / issues", ["Phiếu cân điện tử"] = "Weighbridge tickets",
        ["Đơn mua hàng"] = "Purchase orders", ["Đơn bán hàng"] = "Sales orders",
        ["Lệnh sản xuất"] = "Production orders", ["Định mức (BOM)"] = "Bills of materials", ["Hoạch định vật tư"] = "Material planning (MRP)", ["Giá thành sản phẩm"] = "Product costing",
        ["Nhân viên"] = "Employees", ["Chấm công"] = "Attendance", ["Bảng lương"] = "Payroll",
        ["Phiếu kiểm tra (KCS)"] = "QC inspections", ["Xử lý hàng không đạt (NCR)"] = "Non-conformance (NCR)", ["Kế hoạch & tiêu chí kiểm tra"] = "Inspection plans", ["Báo cáo chất lượng"] = "Quality reports",
        ["Trung tâm báo cáo"] = "Report center", ["Báo cáo động"] = "Ad-hoc reports", ["Sổ nhật ký chung"] = "General journal", ["Lưu chuyển tiền tệ"] = "Cash flow statement", ["Nhập – xuất – tồn kho"] = "Stock movement summary",
        ["Hàng hóa, vật tư"] = "Items", ["Khách hàng"] = "Customers", ["Nhà cung cấp"] = "Vendors", ["Kho hàng"] = "Warehouses", ["Hệ thống tài khoản"] = "Chart of accounts",
        ["Nhóm tài sản cố định"] = "Asset categories", ["Đơn vị tính"] = "Units of measure", ["Phòng ban / bộ phận"] = "Departments", ["Nhập danh mục từ Excel"] = "Import from Excel",
        ["Duyệt chứng từ"] = "Approvals", ["Người dùng & phân quyền"] = "Users & roles", ["Ma trận phân quyền"] = "Permission matrix", ["Công ty & chi nhánh"] = "Companies & branches",
        ["Nhật ký thao tác"] = "Audit log", ["Tham số hệ thống"] = "System settings", ["Kết nối API & thiết bị"] = "API & devices", ["Kịch bản nghiệp vụ"] = "Business scenarios",
        ["Mua hàng"] = "Purchasing", ["Bán hàng"] = "Sales", ["Đề nghị mua hàng"] = "Purchase requisitions", ["Yêu cầu báo giá (RFQ)"] = "Requests for quotation", ["Đánh giá nhà cung cấp"] = "Supplier evaluation",
        ["Đối chiếu 3 bên"] = "3-way matching", ["Chi phí mua hàng"] = "Landed costs", ["Báo giá bán hàng"] = "Sales quotations", ["Giao hàng & vận chuyển"] = "Delivery & shipping", ["Bảng giá & khuyến mại"] = "Price lists & promotions",
        ["Hoa hồng & KPI bán hàng"] = "Commission & sales KPI", ["Cảnh báo tồn kho"] = "Stock alerts", ["Lô, serial & hạn dùng"] = "Lots, serials & expiry", ["Vị trí lưu kho"] = "Storage locations", ["Kiểm kê kho"] = "Stocktaking",
        ["Tuổi hàng tồn"] = "Inventory aging", ["In tem mã vạch"] = "Barcode labels", ["Nhóm hàng"] = "Item groups", ["Pallet"] = "Pallets",
        // Tiêu đề trang thường gặp
        ["Danh mục nhân viên"] = "Employees", ["Nhập dữ liệu từ Excel"] = "Import from Excel", ["Quy tắc phê duyệt"] = "Approval rules", ["Kết nối API & thiết bị"] = "API & devices",
        ["Đối chiếu hóa đơn với cổng thuế"] = "Tax portal invoice matching", ["Thuế thu nhập doanh nghiệp"] = "Corporate income tax", ["Tờ khai thuế GTGT (01/GTGT)"] = "VAT return (01/GTGT)",
        ["Hợp nhất báo cáo tài chính"] = "Consolidated financial statements", ["Bảng cân đối theo chuẩn mực / chế độ khác"] = "Trial balance under alternate GAAP",
        ["Đối chiếu sao kê ngân hàng"] = "Bank reconciliation", ["Lịch thanh toán nợ phải trả"] = "Payables payment schedule", ["Bút toán định kỳ & phân bổ"] = "Recurring & allocation entries",
        ["Điều chuyển tài sản cố định"] = "Fixed asset transfers", ["Phiếu cân điện tử"] = "Weighbridge tickets",
    };
}
