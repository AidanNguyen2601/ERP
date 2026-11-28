using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;
using Xunit;

namespace ERP.Tests;

/// <summary>Kiểm thử đơn vị: kết nối nhà cung cấp hóa đơn điện tử (máy chủ giả), bỏ dấu tên ngân hàng</summary>
public class EInvoiceProviderTests
{
    private static Invoice Sample()
    {
        var p = new Partner { Id = 5, Code = "KH01", Name = "Công ty TNHH A & B", TaxCode = "0101234567", Address = "1 Lê Lợi" };
        var inv = new Invoice { Id = 42, Number = "HDB2611-0042", Series = "C26TAA", InvoiceNo = "0000042", Date = new DateTime(2026, 11, 28), Partner = p, PartnerId = 5, Direction = InvoiceDirection.Sales,
            CurrencyCode = "VND", ExchangeRate = 1, Subtotal = 1_000_000, VatAmount = 100_000, Total = 1_100_000 };
        inv.Lines.Add(new InvoiceLine { Description = "Bàn làm việc", Unit = "Cái", Quantity = 2, UnitPrice = 500_000, Amount = 1_000_000, VatRate = 10 });
        return inv;
    }

    private static EInvoiceConfig Cfg(string url) => new(url, "user", "pass", "svcacc", "svcpass", "1/001", "0109999999", new HttpClient());

    [Fact]
    public async Task Viettel_Issue_SendsJsonAndReadsReservationCode()
    {
        using var fake = new FakeWebhook((path, body) => "{\"errorCode\":null,\"description\":null,\"result\":{\"supplierTaxCode\":\"0109999999\",\"invoiceNo\":\"C26TAA42\",\"reservationCode\":\"RSV123\",\"codeOfTax\":\"M1-26-ABC\"}}");
        var r = await new ViettelSInvoiceProvider(Cfg(fake.Url)).IssueAsync(Sample(), "");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("RSV123", r.LookupCode);
        Assert.Equal("M1-26-ABC", r.TaxCode);
        var body = Assert.Single(fake.Bodies);
        Assert.Contains("\"buyerTaxCode\":\"0101234567\"", body);
        Assert.Contains("\"adjustmentType\":\"1\"", body);
        Assert.Contains("\"totalAmountWithTax\":1100000", body);
    }

    [Fact]
    public async Task Viettel_Replacement_UsesAdjustmentType3_AndOriginalReference()
    {
        using var fake = new FakeWebhook((_, _) => "{\"errorCode\":null,\"result\":{\"invoiceNo\":\"C26TAA43\",\"reservationCode\":\"R2\"}}");
        var original = Sample(); var repl = Sample(); repl.Id = 43; repl.InvoiceNo = null; repl.ReplacesInvoice = original;
        var r = await new ViettelSInvoiceProvider(Cfg(fake.Url)).IssueAsync(repl, "");
        Assert.True(r.Ok, r.Message);
        var body = Assert.Single(fake.Bodies);
        Assert.Contains("\"adjustmentType\":\"3\"", body);
        Assert.Contains("\"originalInvoiceId\":\"C26TAA0000042\"", body);
    }

    [Fact]
    public async Task Viettel_ErrorCode_IsReportedAsFailure()
    {
        using var fake = new FakeWebhook((_, _) => "{\"errorCode\":\"INVOICE_SERIES_INVALID\",\"description\":\"Ký hiệu không hợp lệ\",\"result\":null}");
        var r = await new ViettelSInvoiceProvider(Cfg(fake.Url)).IssueAsync(Sample(), "");
        Assert.False(r.Ok);
        Assert.Contains("INVOICE_SERIES_INVALID", r.Message);
    }

    private static string Soap(string action, string result) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><{action}Response xmlns=\"http://tempuri.org/\"><{action}Result>{result}</{action}Result></{action}Response></soap:Body></soap:Envelope>";

    [Fact]
    public async Task Vnpt_Issue_PostsSoapWithEscapedXml()
    {
        using var fake = new FakeWebhook((_, _) => Soap("ImportAndPublishInv", "OK:1/001;C26TAA-ERP00000042_15"));
        var r = await new VnptInvoiceProvider(Cfg(fake.Url)).IssueAsync(Sample(), "");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("ERP00000042", r.LookupCode);
        Assert.Contains("\"http://tempuri.org/ImportAndPublishInv\"", fake.Actions);
        var body = Assert.Single(fake.Bodies);
        Assert.Contains("<Account>svcacc</Account>", body);
        Assert.Contains("&lt;CusName&gt;Công ty TNHH A &amp;amp; B&lt;/CusName&gt;", body);   // XML hóa đơn được escape trong SOAP
        Assert.Contains("<pattern>1/001</pattern>", body);
    }

    [Fact]
    public async Task Vnpt_Error_IsExplained()
    {
        using var fake = new FakeWebhook((_, _) => Soap("ImportAndPublishInv", "ERR:20"));
        var r = await new VnptInvoiceProvider(Cfg(fake.Url)).IssueAsync(Sample(), "");
        Assert.False(r.Ok);
        Assert.Contains("mẫu số, ký hiệu", r.Message);
    }

    [Fact]
    public async Task Vnpt_Cancel_And_Replace_UseBusinessService()
    {
        using var fake = new FakeWebhook((path, body) => body.Contains("cancelInv") ? Soap("cancelInv", "OK:") : Soap("ReplaceInvoiceAction", "OK:1/001;C26TAA-ERP00000043_16"));
        var p = new VnptInvoiceProvider(Cfg(fake.Url));
        Assert.True((await p.CancelAsync(Sample(), "Sai tên")).Ok);
        var repl = Sample(); repl.Id = 43; repl.ReplacesInvoice = Sample();
        Assert.True((await p.IssueAsync(repl, "")).Ok);
        Assert.Contains("\"http://tempuri.org/cancelInv\"", fake.Actions);
        Assert.Contains("\"http://tempuri.org/ReplaceInvoiceAction\"", fake.Actions);
        Assert.Contains(fake.Bodies, b => b.Contains("<fkey>ERP00000042</fkey>"));
    }

    [Theory]
    [InlineData("Nguyễn Thị Hồng Đào", "Nguyen Thi Hong Dao")]
    [InlineData("Trần Văn Lợi", "Tran Van Loi")]
    public void BankFile_NamesAreUnaccented(string vi, string expected) => Assert.Equal(expected, ERP.Web.Controllers.ComplianceController.Unaccent(vi));
}

/// <summary>Kiểm thử tích hợp: tờ khai TNCN, D02-LT, chế độ BHXH, tạm ứng lương, tệp ngân hàng, hóa đơn thay thế, 04/SS, thuyết minh BCTC</summary>
[Collection("erp")]
public class ComplianceIntegrationTests
{
    private readonly ErpFixture _f;
    public ComplianceIntegrationTests(ErpFixture f) => _f = f;

    [Fact]
    public async Task Pit05_MatchesPostedPayroll_AndExportsXmlAndExcel()
    {
        if (!_f.Enabled) return;
        var run = await _f.WithDbAsync(db => db.PayrollRuns.Where(r => r.Status == DocStatus.Posted).OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).FirstOrDefaultAsync());
        if (run == null) return;
        var pit = await _f.WithDbAsync(db => db.PayrollLines.Where(l => l.PayrollRun!.Status == DocStatus.Posted && l.PayrollRun.Year == run.Year && l.PayrollRun.Month == run.Month).SumAsync(l => l.Pit));
        var c = await _f.LoginAsync("ketoan");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/Compliance/Pit05?year={run.Year}&period={run.Month}")).StatusCode);
        var xml = await c.GetStringAsync($"/Compliance/Pit05?year={run.Year}&period={run.Month}&format=xml");
        Assert.Contains($"<ct35>{pit:0}</ct35>", xml);
        Assert.Contains("05/KK-TNCN", xml);
        var x = await c.GetAsync($"/Compliance/Pit05?year={run.Year}&kind=Q&period={(run.Month - 1) / 3 + 1}&format=excel");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", x.Content.Headers.ContentType!.MediaType);
        var qtt = await c.GetStringAsync($"/Hr/PitFinalization?year={run.Year}&xml=true");
        Assert.Contains("05/QTT-TNCN", qtt);
    }

    [Fact]
    public async Task D02_ListsNewHireResignAndInsuranceChange()
    {
        if (!_f.Enabled) return;
        var today = DateTime.Today;
        var (newId, goneId) = await _f.WithDbAsync(async db =>
        {
            var tpl = await db.Employees.AsNoTracking().FirstAsync();
            var n = new Employee { Code = "NVT" + today.Ticks % 100000, FullName = "Nhân viên mới D02", Department = tpl.Department, Position = "Thợ", BaseSalary = 8_000_000, InsuranceSalary = 8_000_000,
                ExpenseAccount = tpl.ExpenseAccount, StartDate = new DateTime(today.Year, today.Month, 1), IsActive = true, InsuranceBookNo = "0123456789" };
            db.Employees.Add(n);
            var g = await db.Employees.Where(e => e.IsActive).OrderByDescending(e => e.Id).Skip(1).FirstAsync();
            g.ResignDate = new DateTime(today.Year, today.Month, 1); g.IsActive = false;
            await db.SaveChangesAsync();
            return (n.Id, g.Id);
        });
        try
        {
            var c = await _f.LoginAsync("ketoan");
            var html = WebUtility.HtmlDecode(await c.GetStringAsync($"/Compliance/D02?year={today.Year}&month={today.Month}"));
            Assert.Contains("Nhân viên mới D02", html);
            Assert.Matches(">TM<", html);
            Assert.Matches(">GH<", html);
            var x = await c.GetAsync($"/Compliance/D02?year={today.Year}&month={today.Month}&excel=true");
            Assert.Equal(HttpStatusCode.OK, x.StatusCode);
        }
        finally
        {
            await _f.WithDbAsync(async db => { var g = await db.Employees.FindAsync(goneId); g!.ResignDate = null; g.IsActive = true; db.Employees.Remove((await db.Employees.FindAsync(newId))!); await db.SaveChangesAsync(); return 0; });
        }
    }

    [Fact]
    public async Task SickLeave_IsUnpaidByCompany_AndBenefitIsComputed()
    {
        if (!_f.Enabled) return;
        var om = await _f.WithDbAsync(db => db.LeaveTypes.FirstAsync(t => t.Code == "OM"));
        Assert.Equal(BenefitKind.Sickness, om.BenefitKind);
        Assert.False(om.IsPaid);
        Assert.True(await _f.WithDbAsync(db => db.LeaveTypes.AnyAsync(t => t.Code == "TS" && t.BenefitKind == BenefitKind.Maternity)));
        var c = await _f.LoginAsync("admin");
        var emp = await _f.WithDbAsync(db => db.Employees.Where(e => e.IsActive).OrderBy(e => e.Id).Skip(4).FirstAsync());
        var day = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(4).AddDays(2); while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        await _f.PostAsync(c, "/Hr/Leave", "/Hr/RequestLeave", ("EmployeeId", emp.Id.ToString()), ("LeaveTypeId", om.Id.ToString()), ("Days", "2"), ("FromDate", day.ToString("yyyy-MM-dd")), ("ToDate", day.AddDays(1).ToString("yyyy-MM-dd")), ("Reason", "Sốt"));
        var req = await _f.WithDbAsync(db => db.LeaveRequests.OrderByDescending(l => l.Id).FirstAsync(l => l.EmployeeId == emp.Id && l.FromDate == day));
        await _f.PostAsync(c, "/Hr/Leave", "/Hr/DecideLeave", ("id", req.Id.ToString()), ("approve", "true"));
        var html = WebUtility.HtmlDecode(await c.GetStringAsync($"/Compliance/Benefits?year={day.Year}&month={day.Month}"));
        var ins = emp.InsuranceSalary > 0 ? emp.InsuranceSalary : emp.BaseSalary;
        Assert.Contains(Math.Round(ins / 24m * 2 * 75 / 100m, 0).ToMoney(), html);
    }

    [Fact]
    public async Task SalaryAdvance_PostsJournal_AndIsDeductedFromPayroll()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("ketoan");
        var target = DateTime.Today.AddMonths(5);
        var emp = await _f.WithDbAsync(db => db.Employees.Where(e => e.IsActive && e.BaseSalary >= 4_000_000).OrderBy(e => e.Id).FirstAsync());
        var cash = await _f.WithDbAsync(db => db.CashAccounts.Where(x => x.IsActive).Select(x => x.Id).FirstAsync());
        var r = await _f.PostAsync(c, "/Compliance/Advances", "/Compliance/SaveAdvance", ("EmployeeId", emp.Id.ToString()), ("Date", DateTime.Today.ToString("yyyy-MM-dd")), ("Amount", "1000000"),
            ("Year", target.Year.ToString()), ("Month", target.Month.ToString()), ("CashAccountId", cash.ToString()), ("Note", "test"), ("pay", "true"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var adv = await _f.WithDbAsync(db => db.SalaryAdvances.OrderByDescending(a => a.Id).FirstAsync());
        Assert.Equal(AdvanceStatus.Paid, adv.Status);
        var lines = await _f.WithDbAsync(db => db.JournalLines.Include(l => l.Account).Where(l => l.JournalEntryId == adv.JournalEntryId).ToListAsync());
        Assert.Equal(1_000_000, lines.Where(l => l.Account!.Code == "3341").Sum(l => l.Debit));
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        // vượt hạn mức 50% lương cơ bản
        await _f.PostAsync(c, "/Compliance/Advances", "/Compliance/SaveAdvance", ("EmployeeId", emp.Id.ToString()), ("Date", DateTime.Today.ToString("yyyy-MM-dd")), ("Amount", (emp.BaseSalary).ToString("0")),
            ("Year", target.Year.ToString()), ("Month", target.Month.ToString()), ("CashAccountId", cash.ToString()), ("pay", "true"));
        Assert.Equal(1, await _f.WithDbAsync(db => db.SalaryAdvances.CountAsync(a => a.EmployeeId == emp.Id && a.Year == target.Year && a.Month == target.Month)));
        // bảng lương kỳ đó trừ tạm ứng
        var admin = await _f.LoginAsync("admin");
        var pr = await _f.PostAsync(admin, "/Payroll", "/Payroll/Create", ("year", target.Year.ToString()), ("month", target.Month.ToString()));
        var runId = int.Parse(Regex.Match(pr.Headers.Location!.ToString(), @"/Payroll/Details/(\d+)").Groups[1].Value);
        var line = await _f.WithDbAsync(db => db.PayrollLines.FirstAsync(l => l.PayrollRunId == runId && l.EmployeeId == emp.Id));
        Assert.Equal(1_000_000, line.Advance);
        Assert.Equal(line.Gross - line.EmpTotal - line.Pit - line.OtherDeduction - line.Advance, line.Net);
        // tệp chi lương
        var xlsx = await admin.GetAsync($"/Compliance/BankFile?runId={runId}");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        var csv = await admin.GetStringAsync($"/Compliance/BankFile?runId={runId}&format=csv");
        Assert.Contains(ERP.Web.Controllers.ComplianceController.Unaccent(emp.FullName).ToUpperInvariant(), csv);
        Assert.Contains(line.Net.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), csv);
        // hủy tạm ứng khi chưa trừ lương: đảo bút toán
        await _f.PostAsync(c, "/Compliance/Advances", "/Compliance/CancelAdvance", ("id", adv.Id.ToString()), ("reason", "nhầm"));
        Assert.Equal(AdvanceStatus.Cancelled, (await _f.WithDbAsync(db => db.SalaryAdvances.FirstAsync(a => a.Id == adv.Id))).Status);
    }

    [Fact]
    public async Task ReplacementInvoice_CancelsOriginal_ReversesEntry_AndReferencesOriginalInXml()
    {
        if (!_f.Enabled) return;
        var admin = await _f.LoginAsync("admin");
        await _f.WithDbAsync(async db =>
        {
            foreach (var (k, v) in new[] { ("EInvoice.Enabled", "true"), ("EInvoice.Provider", "demo") })
            { var s = await db.SystemSettings.FindAsync(k); if (s == null) db.SystemSettings.Add(new SystemSetting { Key = k, Value = v }); else s.Value = v; }
            await db.SaveChangesAsync(); return 0;
        });
        try
        {
            var orig = await _f.WithDbAsync(db => db.Invoices.Include(i => i.Partner).Where(i => i.Direction == InvoiceDirection.Sales && i.Status == InvoiceStatus.Issued && i.PaidAmount == 0 && i.SalesOrderId != null)
                .OrderByDescending(i => i.Id).FirstOrDefaultAsync());
            if (orig == null) return;
            var r = await _f.PostAsync(admin, $"/Invoices/Details/{orig.Id}", "/Invoices/Replace", ("id", orig.Id.ToString()));
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            var replId = int.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Invoices/Details/(\d+)").Groups[1].Value);
            Assert.NotEqual(orig.Id, replId);
            r = await _f.PostAsync(admin, $"/Invoices/Details/{replId}", "/Invoices/Issue", ("id", replId.ToString()));
            var flash = await admin.GetStringAsync($"/Invoices/Details/{replId}");
            var (o2, rep) = await _f.WithDbAsync(async db => (await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == orig.Id), await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == replId)));
            Assert.True(rep.Status == InvoiceStatus.Issued, "Hóa đơn thay thế chưa phát hành: " + Regex.Match(WebUtility.HtmlDecode(flash), "alert alert-danger[^>]*>([^<]*)").Groups[1].Value);
            Assert.Equal(InvoiceStatus.Cancelled, o2.Status);
            Assert.Equal(orig.Id, rep.ReplacesInvoiceId);
            Assert.Contains("<TCHDon>1</TCHDon>", rep.EInvoiceXml);
            Assert.Contains($"<SHDCLQuan>{orig.InvoiceNo}</SHDCLQuan>", rep.EInvoiceXml);
            Assert.Equal(EInvoiceStatus.Issued, rep.EInvoiceStatus);
            // sổ cái vẫn cân và đối chiếu khớp
            Assert.Empty(await _f.WithDbAsync(db => db.JournalLines.GroupBy(l => l.JournalEntryId).Where(g => g.Sum(l => l.Debit) != g.Sum(l => l.Credit)).Select(g => g.Key).ToListAsync()));
            // hủy hóa đơn thay thế → lập thông báo 04/SS
            await _f.PostAsync(admin, $"/Invoices/Details/{replId}", "/Invoices/Cancel", ("id", replId.ToString()), ("reason", "Sai đơn giá"));
            var cancelled = await _f.WithDbAsync(db => db.Invoices.AsNoTracking().FirstAsync(i => i.Id == replId));
            Assert.Contains("<TCTBao>1</TCTBao>", cancelled.ErrorNoticeXml);
            Assert.Contains("Sai đơn giá", cancelled.ErrorNoticeXml);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Invoices/ErrorNoticeXml/{replId}")).StatusCode);
        }
        finally
        {
            await _f.WithDbAsync(async db => { var s = await db.SystemSettings.FindAsync("EInvoice.Enabled"); if (s != null) s.Value = "false"; await db.SaveChangesAsync(); return 0; });
        }
    }

    [Fact]
    public async Task FinancialNotes_B09_TiesToLedger()
    {
        if (!_f.Enabled) return;
        var c = await _f.LoginAsync("ketoan");
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/FinancialNotes?from=2000-01-01"));
        Assert.Contains("Tiền và các khoản tương đương tiền", html);
        var cash = await _f.WithDbAsync(db => db.JournalLines.Where(l => l.JournalEntry!.Status == DocStatus.Posted && l.JournalEntry.Date <= DateTime.Today && l.Account!.Code.StartsWith("11")).SumAsync(l => l.Debit - l.Credit));
        Assert.Contains(cash.ToMoney(), html);
        var x = await c.GetAsync("/FinancialNotes?excel=true");
        Assert.Equal(HttpStatusCode.OK, x.StatusCode);
    }
}
