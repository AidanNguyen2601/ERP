using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Xác thực khóa API (header X-Api-Key) và quyền (scope); lỗi nghiệp vụ trả 400 JSON, giao dịch được rollback</summary>
public class ApiKeyAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    private readonly string _scope;
    public ApiKeyAttribute(string scope) => _scope = scope;
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ErpDbContext>();
        var key = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
        var client = key.Length < 12 ? null : await db.ApiClients.FirstOrDefaultAsync(c => c.KeyPrefix == key.Substring(0, 10) && c.IsActive);
        if (client == null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromHexString(client.KeyHash), Convert.FromHexString(SettingsController.Hash(key))))
        { ctx.Result = new UnauthorizedObjectResult(new { error = "Khóa API không hợp lệ hoặc đã bị thu hồi." }); return; }
        if (!client.Scopes.Split(',').Contains(_scope)) { ctx.Result = new ObjectResult(new { error = $"Khóa API không có quyền {_scope}." }) { StatusCode = 403 }; return; }
        await db.ApiClients.Where(c => c.Id == client.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CallCount, c => c.CallCount + 1).SetProperty(c => c.LastUsedAt, DateTime.Now));
        ctx.HttpContext.Items["ApiClient"] = client.Name;
        var done = await next();
        if (done.Exception is BusinessException bx && !done.ExceptionHandled) { done.Result = new BadRequestObjectResult(new { error = bx.Message }); done.ExceptionHandled = true; }
    }
}

/// <summary>REST API cho hệ thống bên ngoài: website / sàn TMĐT, ngân hàng, máy chấm công, thiết bị cân, tra cứu hóa đơn điện tử</summary>
[ApiController, AllowAnonymous, IgnoreAntiforgeryToken, Route("api/v1")]
public class ApiV1Controller : ControllerBase
{
    private readonly ErpDbContext _db;
    public ApiV1Controller(ErpDbContext db) => _db = db;

    [HttpGet("partners"), ApiKey("catalog.read")]
    public async Task<IActionResult> Partners(string? q, int take = 100) =>
        Ok(await _db.Partners.Where(p => p.IsActive && (q == null || p.Code.Contains(q) || p.Name.Contains(q) || p.TaxCode == q)).OrderBy(p => p.Code).Take(Math.Clamp(take, 1, 1000))
            .Select(p => new { p.Code, p.Name, p.TaxCode, p.Address, p.Phone, p.Email, p.IsCustomer, p.IsVendor }).ToListAsync());

    [HttpGet("items"), ApiKey("catalog.read")]
    public async Task<IActionResult> Items(string? q, int take = 100) =>
        Ok(await _db.Items.Where(i => i.IsActive && (q == null || i.Code.Contains(q) || i.Name.Contains(q))).OrderBy(i => i.Code).Take(Math.Clamp(take, 1, 1000))
            .Select(i => new { i.Code, i.Name, i.Unit, Type = i.Type.ToString(), i.SalePrice, i.VatRate }).ToListAsync());

    [HttpGet("stock"), ApiKey("catalog.read")]
    public async Task<IActionResult> Stock(string? itemCode) =>
        Ok(await _db.StockLevels.Where(b => itemCode == null || b.Item!.Code == itemCode).Select(b => new { Item = b.Item!.Code, Warehouse = b.Warehouse!.Code, b.Quantity }).ToListAsync());

    [HttpGet("invoices/{lookupCode}"), ApiKey("invoice.read")]
    public async Task<IActionResult> Invoice(string lookupCode)
    {
        var i = await _db.Invoices.Include(x => x.Partner).Where(x => x.EInvoiceLookupCode == lookupCode || x.Number == lookupCode).FirstOrDefaultAsync();
        if (i == null) return NotFound(new { error = "Không tìm thấy hóa đơn." });
        return Ok(new { i.Number, i.Series, i.InvoiceNo, i.Date, Partner = i.Partner?.Name, i.Partner?.TaxCode, i.Subtotal, i.VatAmount, i.Total, Status = i.Status.ToString(), EInvoice = i.EInvoiceStatus.ToString(), i.EInvoiceLookupCode, i.EInvoiceTaxCode });
    }

    public record OrderLineDto(string ItemCode, decimal Quantity, decimal? UnitPrice, decimal? DiscountPct);
    public record OrderDto(string CustomerCode, string? WarehouseCode, DateTime? DeliveryDate, string? Note, List<OrderLineDto> Lines);

    /// <summary>Tạo đơn bán nháp (VD đơn từ website / sàn TMĐT) – nhân viên kinh doanh kiểm tra rồi xác nhận trên ERP</summary>
    [HttpPost("sales-orders"), ApiKey("order.write")]
    public async Task<IActionResult> SalesOrder([FromBody] OrderDto dto)
    {
        var c = await _db.Partners.FirstOrDefaultAsync(p => p.Code == dto.CustomerCode && p.IsCustomer) ?? throw new BusinessException($"Không có khách hàng mã {dto.CustomerCode}.");
        var wh = dto.WarehouseCode != null ? await _db.Warehouses.FirstOrDefaultAsync(w => w.Code == dto.WarehouseCode) : await _db.Warehouses.Where(w => w.IsActive).OrderBy(w => w.Id).FirstOrDefaultAsync();
        if (wh == null) throw new BusinessException("Không tìm thấy kho.");
        if (dto.Lines == null || dto.Lines.Count == 0) throw new BusinessException("Đơn không có dòng hàng.");
        var so = new SalesOrder { CustomerId = c.Id, WarehouseId = wh.Id, Date = DateTime.Today, DeliveryDate = (dto.DeliveryDate ?? DateTime.Today.AddDays(3)).Date, Note = $"[API {HttpContext.Items["ApiClient"]}] {dto.Note}".Trim(),
            Number = await DocNumbers.NextAsync(_db.SalesOrders.Select(x => x.Number), "DBH", DateTime.Today) };
        foreach (var l in dto.Lines)
        {
            var it = await _db.Items.FirstOrDefaultAsync(i => i.Code == l.ItemCode && i.IsActive) ?? throw new BusinessException($"Không có hàng hóa mã {l.ItemCode}.");
            if (l.Quantity <= 0) throw new BusinessException($"Số lượng {l.ItemCode} phải > 0.");
            so.Lines.Add(new SalesOrderLine { ItemId = it.Id, Quantity = l.Quantity, UnitPrice = l.UnitPrice ?? it.SalePrice, DiscountPct = l.DiscountPct ?? 0, VatRate = it.VatRate });
        }
        SalesService.Recalc(so);
        _db.SalesOrders.Add(so);
        await _db.SaveChangesAsync();
        return Ok(new { so.Number, so.Total, Status = "Draft" });
    }

    public record BankRowDto(DateTime Date, string Description, string? Reference, decimal Amount);
    public record BankStatementDto(string AccountNo, decimal OpeningBalance, decimal? ClosingBalance, List<BankRowDto> Transactions);

    [HttpPost("bank-statements"), ApiKey("bank.write")]
    public async Task<IActionResult> BankStatement([FromBody] BankStatementDto dto, [FromServices] BankService bank)
    {
        var no = new string((dto.AccountNo ?? "").Where(char.IsLetterOrDigit).ToArray());
        var cas = await _db.CashAccounts.Where(c => c.Type == CashAccountType.Bank && c.BankAccountNo != null).ToListAsync();
        var ca = cas.FirstOrDefault(c => new string(c.BankAccountNo!.Where(char.IsLetterOrDigit).ToArray()) == no) ?? throw new BusinessException($"Không có tài khoản ngân hàng số {dto.AccountNo}.");
        var st = await bank.ImportAsync(ca.Id, "", dto.OpeningBalance, dto.ClosingBalance, dto.Transactions.Select(t => new BankService.StatementRow(t.Date, t.Description ?? "", t.Reference, t.Amount)).ToList(), $"API {HttpContext.Items["ApiClient"]}");
        return Ok(new { st.Id, Lines = st.Lines.Count, Matched = st.Lines.Count(l => l.CashVoucherId != null) });
    }

    public record AttendanceDto(string EmployeeCode, int Year, int Month, decimal WorkDays, decimal? OvertimeHours, decimal? LeaveDays);

    [HttpPost("attendance"), ApiKey("attendance.write")]
    public async Task<IActionResult> Attendance([FromBody] List<AttendanceDto> rows)
    {
        int n = 0; var missing = new List<string>();
        foreach (var r in rows)
        {
            var e = await _db.Employees.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Code == r.EmployeeCode);
            if (e == null || r.Month is < 1 or > 12 || r.WorkDays is < 0 or > 31) { missing.Add(r.EmployeeCode); continue; }
            var a = await _db.Attendances.FirstOrDefaultAsync(x => x.EmployeeId == e.Id && x.Year == r.Year && x.Month == r.Month);
            if (a == null) _db.Attendances.Add(a = new Attendance { EmployeeId = e.Id, Year = r.Year, Month = r.Month });
            a.WorkDays = r.WorkDays; a.OvertimeHours = r.OvertimeHours ?? a.OvertimeHours; a.LeaveDays = r.LeaveDays ?? a.LeaveDays; a.Source = $"Máy chấm công ({HttpContext.Items["ApiClient"]})";
            n++;
        }
        await _db.SaveChangesAsync();
        return Ok(new { Saved = n, Rejected = missing });
    }

    public record PunchDto(string EmployeeCode, DateTime PunchTime, string? Device);

    /// <summary>Máy chấm công đẩy lượt quẹt thô (vân tay / thẻ / khuôn mặt); hệ thống tự xử lý thành chấm công ngày theo ca</summary>
    [HttpPost("timeclock"), ApiKey("attendance.write")]
    public async Task<IActionResult> TimeClock([FromBody] List<PunchDto> rows)
    {
        if (rows == null || rows.Count == 0 || rows.Count > 5000) throw new BusinessException("Gửi từ 1 đến 5.000 lượt quẹt mỗi lần.");
        var codes = (await _db.Employees.IgnoreQueryFilters().Select(e => e.Code).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 0; var missing = new HashSet<string>();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.EmployeeCode) || !codes.Contains(r.EmployeeCode.Trim())) { missing.Add(r.EmployeeCode ?? ""); continue; }
            var code = r.EmployeeCode.Trim().ToUpperInvariant();
            if (await _db.TimeClockPunches.AnyAsync(p => p.EmployeeCode == code && p.PunchTime == r.PunchTime)) continue;
            _db.TimeClockPunches.Add(new TimeClockPunch { EmployeeCode = code, PunchTime = r.PunchTime, Device = r.Device ?? HttpContext.Items["ApiClient"]?.ToString() });
            n++;
        }
        await _db.SaveChangesAsync();
        return Ok(new { Saved = n, UnknownEmployees = missing });
    }

    public record ScaleDto(string TicketNo, DateTime? WeighedAt, string? VehicleNo, string? ItemCode, string? PartnerCode, string? Direction, decimal GrossKg, decimal TareKg, string? Reference, string? Device, string? Note);

    [HttpPost("scale-readings"), ApiKey("scale.write")]
    public async Task<IActionResult> Scale([FromBody] ScaleDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TicketNo) || dto.GrossKg <= 0 || dto.TareKg < 0 || dto.TareKg > dto.GrossKg) throw new BusinessException("Số phiếu cân, khối lượng tổng / bì không hợp lệ.");
        var t = await _db.ScaleTickets.FirstOrDefaultAsync(x => x.TicketNo == dto.TicketNo);
        if (t == null) _db.ScaleTickets.Add(t = new ScaleTicket { TicketNo = dto.TicketNo.Trim() });
        t.WeighedAt = dto.WeighedAt ?? DateTime.Now; t.VehicleNo = dto.VehicleNo; t.ItemCode = dto.ItemCode; t.PartnerCode = dto.PartnerCode; t.Direction = dto.Direction?.ToUpperInvariant() == "OUT" ? "OUT" : "IN";
        t.GrossKg = dto.GrossKg; t.TareKg = dto.TareKg; t.NetKg = dto.GrossKg - dto.TareKg; t.Reference = dto.Reference; t.Device = dto.Device ?? HttpContext.Items["ApiClient"]?.ToString(); t.Note = dto.Note;
        t.ItemId = dto.ItemCode == null ? null : (await _db.Items.FirstOrDefaultAsync(i => i.Code == dto.ItemCode))?.Id;
        t.ReceivedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(new { t.Id, t.TicketNo, t.NetKg });
    }
}
