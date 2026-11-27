using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using ERP.Web.Services.Reporting;
using Microsoft.EntityFrameworkCore;

namespace ERP.Web.Controllers;

/// <summary>Báo cáo chuẩn: sổ nhật ký chung, báo cáo lưu chuyển tiền tệ, tổng hợp nhập – xuất – tồn kho (có xuất Excel)</summary>
public class StandardReportsController : ErpControllerBase
{
    private readonly StandardReportService _svc;
    public StandardReportsController(ErpDbContext db, StandardReportService svc) : base(db) => _svc = svc;

    private static (DateTime From, DateTime To) Period(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.Today).Date; var f = (from ?? new DateTime(t.Year, t.Month, 1)).Date;
        return f > t ? (t, f) : (f, t);
    }

    private FileContentResult Excel(string name, string title, IEnumerable<string> sub, List<ReportExport.Col> cols, IEnumerable<object?[]> rows, object?[]? totals) =>
        File(ReportExport.Xlsx(name, title, sub, cols, rows, totals), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}_{DateTime.Now:yyyyMMdd}.xlsx");

    private string Company => HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ErpSettings>>().Value.CompanyName;

    // ------------------------------------------------------------------ Sổ nhật ký chung
    [Authorize(Roles = AppRoles.FinanceView)]
    public async Task<IActionResult> GeneralJournal(DateTime? from, DateTime? to, bool closing = false, bool export = false)
    {
        var (f, t) = Period(from, to);
        var rows = await _svc.GeneralJournalAsync(f, t, closing);
        if (export)
        {
            var cols = new List<ReportExport.Col> { new("Ngày ghi sổ", FieldKind.Date), new("Số chứng từ", FieldKind.Text), new("Diễn giải", FieldKind.Text), new("Nguồn", FieldKind.Text), new("Số hiệu TK", FieldKind.Text), new("Tên tài khoản", FieldKind.Text), new("Đối tượng", FieldKind.Text), new("Nợ", FieldKind.Money), new("Có", FieldKind.Money) };
            return Excel("So_nhat_ky_chung", "SỔ NHẬT KÝ CHUNG (Mẫu S03a-DN)", new[] { Company, $"Từ ngày {f:dd/MM/yyyy} đến ngày {t:dd/MM/yyyy}" }, cols,
                rows.Select(r => new object?[] { r.Date, r.EntryNo, r.Description, r.Source, r.AccountCode, r.AccountName, r.Partner, r.Debit, r.Credit }),
                new object?[] { "Tổng cộng", null, null, null, null, null, null, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit) });
        }
        ViewBag.From = f; ViewBag.To = t; ViewBag.Closing = closing;
        return View(rows);
    }

    // ------------------------------------------------------------------ Báo cáo lưu chuyển tiền tệ
    [Authorize(Roles = AppRoles.FinanceView)]
    public async Task<IActionResult> CashFlowStatement(DateTime? from, DateTime? to, bool export = false)
    {
        var (f, t) = Period(from, to);
        var rep = await _svc.CashFlowAsync(f, t);
        if (export)
        {
            var cols = new List<ReportExport.Col> { new("Chỉ tiêu", FieldKind.Text), new("Tài khoản đối ứng", FieldKind.Text), new("Số tiền", FieldKind.Money) };
            var rows = new List<object?[]> { new object?[] { "Tiền và tương đương tiền đầu kỳ", null, rep.Opening } };
            foreach (var s in new[] { ("I", "I. Lưu chuyển tiền từ hoạt động kinh doanh"), ("II", "II. Lưu chuyển tiền từ hoạt động đầu tư"), ("III", "III. Lưu chuyển tiền từ hoạt động tài chính") })
            {
                rows.Add(new object?[] { s.Item2, null, rep.Section(s.Item1) });
                rows.AddRange(rep.Lines.Where(l => l.Section == s.Item1).Select(l => new object?[] { "    " + l.Label, l.Accounts, l.Amount }));
            }
            rows.Add(new object?[] { "Lưu chuyển tiền thuần trong kỳ", null, rep.Net });

            return Excel("Luu_chuyen_tien_te", "BÁO CÁO LƯU CHUYỂN TIỀN TỆ (phương pháp trực tiếp)", new[] { Company, $"Từ ngày {f:dd/MM/yyyy} đến ngày {t:dd/MM/yyyy}" }, cols, rows,
                new object?[] { "Tiền và tương đương tiền cuối kỳ", null, rep.Closing });
        }
        ViewBag.From = f; ViewBag.To = t;
        return View(rep);
    }

    // ------------------------------------------------------------------ Tổng hợp nhập – xuất – tồn
    public async Task<IActionResult> StockSummary(DateTime? from, DateTime? to, int? warehouseId, ItemType? type, string? q, bool export = false)
    {
        if (!User.InAny(AppRoles.FinanceView + "," + AppRoles.Warehouse + "," + AppRoles.Purchasing + "," + AppRoles.Production)) return Denied();
        var (f, t) = Period(from, to);
        var rows = await _svc.StockSummaryAsync(f, t, warehouseId, type, q);
        if (export)
        {
            var cols = new List<ReportExport.Col>
            {
                new("Mã hàng", FieldKind.Text), new("Tên hàng", FieldKind.Text), new("ĐVT", FieldKind.Text), new("Loại hàng", FieldKind.Text),
                new("SL tồn đầu kỳ", FieldKind.Qty), new("Giá trị tồn đầu kỳ", FieldKind.Money), new("SL nhập", FieldKind.Qty), new("Giá trị nhập", FieldKind.Money),
                new("SL xuất", FieldKind.Qty), new("Giá trị xuất", FieldKind.Money), new("SL tồn cuối kỳ", FieldKind.Qty), new("Giá trị tồn cuối kỳ", FieldKind.Money)
            };
            var wh = warehouseId.HasValue ? (await Db.Warehouses.FindAsync(warehouseId))?.Name : "Tất cả các kho";
            return Excel("Nhap_xuat_ton", "BÁO CÁO TỔNG HỢP NHẬP – XUẤT – TỒN KHO", new[] { Company, $"Từ ngày {f:dd/MM/yyyy} đến ngày {t:dd/MM/yyyy} · {wh}" }, cols,
                rows.Select(r => new object?[] { r.Code, r.Name, r.Unit, r.Type.GetDisplayName(), r.OpenQty, r.OpenVal, r.InQty, r.InVal, r.OutQty, r.OutVal, r.CloseQty, r.CloseVal }),
                new object?[] { "Tổng cộng", null, null, null, null, rows.Sum(r => r.OpenVal), null, rows.Sum(r => r.InVal), null, rows.Sum(r => r.OutVal), null, rows.Sum(r => r.CloseVal) });
        }
        ViewBag.From = f; ViewBag.To = t; ViewBag.WarehouseId = warehouseId; ViewBag.Type = type; ViewBag.Q = q;
        ViewBag.Whs = await WarehousesAsync(warehouseId);
        if (!warehouseId.HasValue && !type.HasValue && string.IsNullOrWhiteSpace(q) && User.SeesFinance()) ViewBag.GlBalance = await _svc.StockGlBalanceAsync(t);
        return View(rows);
    }
}
