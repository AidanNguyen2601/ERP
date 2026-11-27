using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Thuế: tờ khai GTGT 01/GTGT, đối chiếu hóa đơn với dữ liệu cổng thuế (hoadondientu.gdt.gov.vn), thuế TNDN tạm tính / quyết toán</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Accountant + "," + AppRoles.Director)]
public class TaxController : ErpControllerBase
{
    private readonly TaxService _svc;
    public TaxController(ErpDbContext db, TaxService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Vat(int? year, int? period, decimal c37 = 0, decimal c38 = 0)
    {
        int y = year ?? DateTime.Today.AddMonths(-1).Year, p = period ?? DateTime.Today.AddMonths(-1).Month;
        ViewBag.Year = y; ViewBag.Period = p;
        ViewBag.Saved = await Db.VatDeclarations.OrderByDescending(d => d.Year).ThenByDescending(d => d.Period).Take(24).ToListAsync();
        ViewBag.Existing = await Db.VatDeclarations.FirstOrDefaultAsync(d => d.Year == y && d.Period == p);
        return View(await _svc.ComputeAsync(y, p, c37, c38));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> SaveVat(int year, int period, decimal c37, decimal c38, bool post)
    {
        var d = await _svc.SaveAsync(await _svc.ComputeAsync(year, period, c37, c38), post);
        Success($"Đã lưu tờ khai GTGT {TaxService.PeriodName(year, period)}" + (d.JournalEntryId != null ? " và hạch toán khấu trừ thuế." : "."));
        return RedirectToAction(nameof(Vat), new { year, period });
    }

    /// <summary>Xuất tờ khai dạng XML (cấu trúc tối giản theo mẫu 01/GTGT – TT80/2021) để nộp qua phần mềm HTKK / cổng thuế</summary>
    public async Task<IActionResult> VatXml(int year, int period, [FromServices] Microsoft.Extensions.Options.IOptions<ErpSettings> cfg)
    {
        var d = await Db.VatDeclarations.FirstOrDefaultAsync(x => x.Year == year && x.Period == period) ?? await _svc.ComputeAsync(year, period);
        var c = cfg.Value;
        var x = new System.Xml.Linq.XElement("HSoThueDTu",
            new System.Xml.Linq.XElement("TTinChung",
                new System.Xml.Linq.XElement("maTKhai", "842"), new System.Xml.Linq.XElement("tenTKhai", "TỜ KHAI THUẾ GIÁ TRỊ GIA TĂNG (Mẫu số 01/GTGT)"),
                new System.Xml.Linq.XElement("kieuKy", period <= 12 ? "M" : "Q"), new System.Xml.Linq.XElement("kyKKhai", period <= 12 ? $"{period:00}/{year}" : $"{period - 12}/{year}"),
                new System.Xml.Linq.XElement("mst", c.CompanyTaxCode), new System.Xml.Linq.XElement("tenNNT", c.CompanyName)),
            new System.Xml.Linq.XElement("CTieuTKhaiChinh",
                new[] { ("ct22", d.C22), ("ct23", d.C23), ("ct24", d.C24), ("ct25", d.C25), ("ct26", d.C26), ("ct29", d.C29), ("ct30", d.C30), ("ct31", d.C31), ("ct32", d.C32), ("ct33", d.C33),
                        ("ct34", d.C26 + d.C29 + d.C30 + d.C32), ("ct35", d.C31 + d.C33), ("ct36", d.C31 + d.C33 - d.C25), ("ct37", d.C37), ("ct38", d.C38), ("ct40a", d.C40a), ("ct41", d.C41), ("ct43", d.C43) }
                .Select(t => new System.Xml.Linq.XElement(t.Item1, t.Item2.ToString("0")))));
        return File(System.Text.Encoding.UTF8.GetBytes(new System.Xml.Linq.XDocument(new System.Xml.Linq.XDeclaration("1.0", "utf-8", null), x).ToString()), "application/xml", $"01GTGT_{year}_{period:00}.xml");
    }

    // ------------------------------------------------------------------ đối chiếu hóa đơn với cổng thuế
    public async Task<IActionResult> Match(DateTime? from, DateTime? to)
    {
        var (f, t) = Range(from, to);
        ViewBag.From = f; ViewBag.To = t;
        ViewBag.PortalCount = await Db.TaxPortalInvoices.CountAsync(p => p.Date >= f && p.Date <= t);
        return View(await _svc.MatchAsync(f, t));
    }

    /// <summary>Nhập bảng kê hóa đơn tải từ cổng hoadondientu.gdt.gov.vn (Excel): Ký hiệu, Số hóa đơn, Ngày lập, MST người bán / mua, Tên, Tổng tiền chưa thuế, Tổng tiền thuế</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting), RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> ImportPortal(InvoiceDirection direction, IFormFile? file, DateTime? from, DateTime? to)
    {
        if (file == null) { Error("Chọn file."); return RedirectToAction(nameof(Match), new { from, to }); }
        List<Dictionary<string, string>> rows;
        await using (var s = file.OpenReadStream()) rows = TabularReader.Read(s, file.FileName);
        int n = 0, skip = 0;
        foreach (var r in rows)
        {
            var no = TabularReader.Get(r, "Số hóa đơn", "Số HĐ", "So hoa don", "Số");
            var d = TabularReader.Date(TabularReader.Get(r, "Ngày lập", "Ngày hóa đơn", "Ngày HĐ", "Ngày"));
            if (no == null || d == null) { skip++; continue; }
            var tax = direction == InvoiceDirection.Purchase ? TabularReader.Get(r, "MST người bán", "Mã số thuế người bán", "MST NB", "MST") : TabularReader.Get(r, "MST người mua", "Mã số thuế người mua", "MST NM", "MST");
            var name = direction == InvoiceDirection.Purchase ? TabularReader.Get(r, "Tên người bán", "Người bán", "Tên") : TabularReader.Get(r, "Tên người mua", "Người mua", "Tên");
            var series = TabularReader.Get(r, "Ký hiệu hóa đơn", "Ký hiệu", "KHHĐ");
            var exist = await Db.TaxPortalInvoices.FirstOrDefaultAsync(p => p.Direction == direction && p.InvoiceNo == no && p.PartnerTaxCode == tax && p.Series == series);
            if (exist == null) Db.TaxPortalInvoices.Add(exist = new TaxPortalInvoice { Direction = direction, InvoiceNo = no, PartnerTaxCode = tax, Series = series });
            exist.Date = d.Value; exist.PartnerName = name;
            exist.Subtotal = TabularReader.Dec(TabularReader.Get(r, "Tổng tiền chưa thuế", "Tiền chưa thuế", "Thành tiền chưa thuế", "Chưa thuế"));
            exist.VatAmount = TabularReader.Dec(TabularReader.Get(r, "Tổng tiền thuế", "Tiền thuế", "Thuế GTGT", "Tiền thuế GTGT"));
            exist.ImportedAt = DateTime.Now; n++;
        }
        await Db.SaveChangesAsync();
        Success($"Đã nhập {n} hóa đơn từ dữ liệu cổng thuế" + (skip > 0 ? $" (bỏ qua {skip} dòng)." : "."));
        return RedirectToAction(nameof(Match), new { from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") });
    }

    // ------------------------------------------------------------------ thuế TNDN
    public async Task<IActionResult> Cit(int? year, int? quarter, decimal nonDeductible = 0, decimal exempt = 0, decimal loss = 0)
    {
        int y = year ?? DateTime.Today.Year, q = quarter ?? ((DateTime.Today.Month - 1) / 3 + 1);
        ViewBag.Year = y; ViewBag.Quarter = q;
        ViewBag.Saved = await Db.CitDeclarations.OrderByDescending(d => d.Year).ThenByDescending(d => d.Quarter).Take(20).ToListAsync();
        return View(await _svc.ComputeCitAsync(y, q, nonDeductible, exempt, loss));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Accounting)]
    public async Task<IActionResult> SaveCit(int year, int quarter, decimal nonDeductible, decimal exempt, decimal loss, bool post)
    {
        var d = await _svc.SaveCitAsync(await _svc.ComputeCitAsync(year, quarter, nonDeductible, exempt, loss), post);
        Success($"Đã lưu thuế TNDN {(quarter == 0 ? "năm" : "quý " + quarter)} {year}: {d.Tax:N0} đ" + (d.JournalEntryId != null ? ", đã hạch toán Nợ 821 / Có 3334." : "."));
        return RedirectToAction(nameof(Cit), new { year, quarter });
    }
}
