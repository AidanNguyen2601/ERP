using Microsoft.AspNetCore.Mvc;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services.Reporting;

namespace ERP.Web.Controllers;

public record CatalogReport(string Group, string Title, string Description, string Icon, string Url, string Kind);

/// <summary>Trung tâm báo cáo: danh mục mọi báo cáo chuẩn, báo cáo quản trị dựng sẵn và lối vào báo cáo động theo quyền người dùng</summary>
public class ReportCenterController : ErpControllerBase
{
    public ReportCenterController(ErpDbContext db) : base(db) { }

    public IActionResult Index()
    {
        var fin = User.SeesFinance();
        var items = new List<CatalogReport>();
        void Add(bool allowed, string group, string title, string desc, string icon, string action, string controller, object? route = null, string kind = "Chuẩn")
        { if (allowed) items.Add(new CatalogReport(group, title, desc, icon, Url.Action(action, controller, route) ?? "#", kind)); }

        // ---- Báo cáo chuẩn
        Add(fin, "Kế toán – tài chính", "Bảng cân đối số phát sinh", "Số dư đầu kỳ, phát sinh và số dư cuối kỳ của mọi tài khoản.", "bi-table", "TrialBalance", "Reports");
        Add(fin, "Kế toán – tài chính", "Sổ cái / sổ chi tiết tài khoản", "Chi tiết phát sinh từng tài khoản kèm tài khoản đối ứng.", "bi-journal-bookmark", "Ledger", "Reports");
        Add(fin, "Kế toán – tài chính", "Sổ nhật ký chung (S03a-DN)", "Toàn bộ bút toán đã ghi sổ theo thứ tự thời gian; xuất Excel.", "bi-journal-text", "GeneralJournal", "StandardReports");
        Add(fin, "Kế toán – tài chính", "Báo cáo kết quả hoạt động kinh doanh (B02-DN)", "Doanh thu, giá vốn, chi phí, lợi nhuận, so sánh cùng kỳ.", "bi-graph-up-arrow", "IncomeStatement", "Reports");
        Add(fin, "Kế toán – tài chính", "Báo cáo tình hình tài chính (B01-DN, TT99)", "Tài sản, nợ phải trả, vốn chủ sở hữu tại một ngày.", "bi-bank", "BalanceSheet", "Reports");
        Add(fin, "Kế toán – tài chính", "Báo cáo lưu chuyển tiền tệ (B03-DN)", "Dòng tiền theo hoạt động kinh doanh, đầu tư, tài chính; tự đối chiếu số dư tiền.", "bi-cash-stack", "CashFlowStatement", "StandardReports");
        Add(fin, "Kế toán – tài chính", "Bảng kê thuế GTGT", "Hóa đơn đầu ra, đầu vào, điều chỉnh giảm và số thuế phải nộp.", "bi-percent", "VatReport", "Invoices");
        Add(fin, "Kế toán – tài chính", "Đối chiếu phân hệ – kế toán", "24 phép kiểm tra kho, mua, bán, công nợ, quỹ, TSCĐ, thuế khớp sổ cái.", "bi-check2-square", "Index", "Reconciliation");

        Add(fin, "Công nợ & tiền", "Công nợ phải thu", "Dư nợ từng khách hàng, hạn mức.", "bi-box-arrow-in-down-left", "Index", "Debts", new { kind = "ar" });
        Add(fin, "Công nợ & tiền", "Công nợ phải trả", "Dư nợ từng nhà cung cấp.", "bi-box-arrow-up-right", "Index", "Debts", new { kind = "ap" });
        Add(fin, "Công nợ & tiền", "Tuổi nợ phải thu", "Phân tích nợ theo nhóm quá hạn.", "bi-hourglass", "Aging", "Debts", new { kind = "ar" });
        Add(fin, "Công nợ & tiền", "Tuổi nợ phải trả", "Phân tích nợ phải trả theo nhóm quá hạn.", "bi-hourglass-split", "Aging", "Debts", new { kind = "ap" });
        Add(fin, "Công nợ & tiền", "Quỹ & ngân hàng – sổ quỹ", "Phiếu thu chi và sổ quỹ / sổ tiền gửi.", "bi-wallet2", "Index", "Cash");
        Add(fin, "Công nợ & tiền", "Báo cáo dòng tiền & dự báo 8 tuần", "Thu chi theo tháng và dự báo từ công nợ đến hạn.", "bi-cash-coin", "Flow", "Cash");

        Add(true, "Kho", "Tồn kho theo mặt hàng × kho", "Tồn, hàng đang về, đã cam kết, khả dụng; cảnh báo tồn tối thiểu.", "bi-boxes", "Index", "Inventory");
        Add(User.InAny(AppRoles.FinanceView + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing + "," + AppRoles.Production), "Kho", "Tổng hợp nhập – xuất – tồn kho",
            "Tồn đầu kỳ, nhập, xuất, tồn cuối kỳ theo mặt hàng; đối chiếu sổ cái; xuất Excel.", "bi-arrow-left-right", "StockSummary", "StandardReports");
        Add(true, "Kho", "Phiếu nhập / xuất kho", "Danh sách phiếu kho, thẻ kho từng mặt hàng.", "bi-clipboard-data", "Docs", "Inventory");
        Add(true, "Sản xuất", "Hoạch định vật tư (MRP)", "Nhu cầu NVL − tồn − đang mua = cần mua.", "bi-clipboard-data", "Mrp", "Production");
        Add(User.InAny(AppRoles.FinanceView + "," + AppRoles.Quality + "," + AppRoles.Warehouse + "," + AppRoles.Production + "," + AppRoles.Purchasing), "Chất lượng", "Báo cáo chất lượng",
            "Tỷ lệ lô đạt, tỷ lệ lỗi, xếp loại nhà cung cấp, NCR.", "bi-patch-check", "Index", "QcReports");
        Add(fin, "Tài sản", "Sổ tài sản cố định & khấu hao", "Nguyên giá, hao mòn, giá trị còn lại, lịch sử khấu hao.", "bi-building", "Index", "Assets");

        // ---- Báo cáo quản trị dựng sẵn (mẫu báo cáo động)
        var allowed = DatasetRegistry.All.Where(d => DatasetRegistry.Allowed(d, User)).Select(d => d.Key).ToHashSet();
        foreach (var t in ReportTemplates.All.Where(t => allowed.Contains(t.Def.Dataset)))
            items.Add(new CatalogReport(t.Group switch { "Bán hàng" => "Bán hàng", "Mua hàng" => "Mua hàng", "Kho" => "Kho", "Sản xuất" => "Sản xuất", "Chất lượng" => "Chất lượng", "Công nợ & tiền" => "Công nợ & tiền", "Tài sản" => "Tài sản", _ => "Kế toán – tài chính" },
                t.Title, t.Description, t.Icon, Url.Action("Designer", "DynamicReports", new { slug = t.Slug }) ?? "#", "Quản trị"));

        ViewBag.AllowDynamic = true;
        var order = new[] { "Kế toán – tài chính", "Bán hàng", "Mua hàng", "Kho", "Công nợ & tiền", "Sản xuất", "Chất lượng", "Tài sản" };
        ViewBag.Order = order;
        return View(items);
    }
}
