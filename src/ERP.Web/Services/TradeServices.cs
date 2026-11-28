using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

// =====================================================================
//  HÓA ĐƠN
// =====================================================================
public class InvoiceService
{
    private readonly ErpDbContext _db;
    private readonly AccountingService _acc;
    private readonly ErpSettings _cfg;
    private readonly CurrentUser _user;
    private readonly EInvoiceService _einv;
    public InvoiceService(ErpDbContext db, AccountingService acc, IOptions<ErpSettings> cfg, CurrentUser user, EInvoiceService einv) { _db = db; _acc = acc; _cfg = cfg.Value; _user = user; _einv = einv; }

    public static void Recalc(Invoice inv)
    {
        var fx = inv.IsForeign;
        foreach (var l in inv.Lines)
        {
            if (fx)
            {
                // Hóa đơn ngoại tệ: đơn giá nhập theo nguyên tệ, quy đổi VND theo tỷ giá hóa đơn
                l.FcAmount = Math.Round(l.Quantity * l.FcUnitPrice, 2);
                l.UnitPrice = Math.Round(l.FcUnitPrice * inv.ExchangeRate, 2);
                l.Amount = Math.Round(l.FcAmount * inv.ExchangeRate, 0);
            }
            else { l.Amount = Math.Round(l.Quantity * l.UnitPrice, 0); l.FcUnitPrice = 0; l.FcAmount = 0; }
            l.VatAmount = Math.Round(l.Amount * l.VatRate / 100m, 0);
        }
        inv.Subtotal = inv.Lines.Sum(l => l.Amount);
        inv.VatAmount = inv.Lines.Sum(l => l.VatAmount);
        inv.Total = inv.Subtotal + inv.VatAmount;
        if (fx)
        {
            inv.FcSubtotal = inv.Lines.Sum(l => l.FcAmount);
            inv.FcVatAmount = inv.Lines.Sum(l => Math.Round(l.FcAmount * l.VatRate / 100m, 2));
            inv.FcTotal = inv.FcSubtotal + inv.FcVatAmount;
        }
        else { inv.FcSubtotal = inv.FcVatAmount = inv.FcTotal = 0; inv.CurrencyCode = "VND"; inv.ExchangeRate = 1; }
    }

    public Task<string> NextNumberAsync(InvoiceDirection d, DateTime date) =>
        DocNumbers.NextAsync(_db.Invoices.Select(i => i.Number), d == InvoiceDirection.Sales ? "HDB" : "HDM", date);

    /// <summary>Phát hành hóa đơn bán / ghi sổ hóa đơn mua</summary>
    public async Task IssueAsync(Invoice inv)
    {
        if (inv.Status != InvoiceStatus.Draft) throw new BusinessException("Chỉ phát hành hóa đơn đang ở trạng thái Nháp.");
        if (!inv.Lines.Any()) throw new BusinessException("Hóa đơn chưa có dòng hàng hóa / dịch vụ.");
        if (inv.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0)) throw new BusinessException("Số lượng phải > 0 và đơn giá không âm.");
        Recalc(inv);
        if (inv.Total <= 0) throw new BusinessException("Tổng tiền hóa đơn phải lớn hơn 0.");
        Invoice? replaced = null;
        if (inv.ReplacesInvoiceId != null)
        {
            // Hóa đơn thay thế: hủy hóa đơn gốc (đảo bút toán, trả lại số lượng đơn hàng) trong cùng giao dịch, rồi phát hành hóa đơn mới tham chiếu hóa đơn gốc
            replaced = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == inv.ReplacesInvoiceId) ?? throw new BusinessException("Không tìm thấy hóa đơn bị thay thế.");
            ValidateReplaceable(replaced);
            if (await _db.SalesCreditNotes.AnyAsync(n => n.InvoiceId == replaced.Id && n.Status == DocStatus.Posted)) throw new BusinessException("Hóa đơn gốc đã có hóa đơn điều chỉnh – không thay thế được.");
            await _acc.ReverseAsync(replaced.JournalEntryId, inv.Date, $"Bị thay thế bởi hóa đơn {inv.Number}");
            await ReleaseOrderQtyAsync(replaced);
            replaced.Status = InvoiceStatus.Cancelled;
            replaced.Note = $"{replaced.Note} [Bị thay thế bởi {inv.Number}]".Trim();
            await _db.SaveChangesAsync();
            inv.ReplacesInvoice = replaced;
        }
        var partner = await _db.Partners.FindAsync(inv.PartnerId) ?? throw new BusinessException("Không tìm thấy đối tượng.");
        var itemIds = inv.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).ToList();
        var items = (await _db.Items.Where(i => itemIds.Contains(i.Id)).ToListAsync()).ToDictionary(i => i.Id);
        var lines = new List<JLine>();

        if (inv.Direction == InvoiceDirection.Sales)
        {
            if (!partner.IsCustomer) throw new BusinessException("Đối tượng không phải khách hàng.");
            if (inv.SalesOrderId == null && inv.Lines.Any(l => l.ItemId.HasValue && items[l.ItemId.Value].IsStocked))
                throw new BusinessException("Hàng có quản lý tồn kho phải bán qua Đơn bán hàng (giao hàng → lập hóa đơn) để ghi nhận giá vốn. Hóa đơn bán trực tiếp chỉ dùng cho dịch vụ.");
            if (string.IsNullOrWhiteSpace(inv.Series)) inv.Series = _cfg.InvoiceSeries;
            if (string.IsNullOrWhiteSpace(inv.InvoiceNo))
            {
                var series = inv.Series;
                var used = await _db.Invoices.Where(i => i.Direction == InvoiceDirection.Sales && i.Series == series && i.InvoiceNo != null).Select(i => i.InvoiceNo!).ToListAsync();
                inv.InvoiceNo = (used.Select(n => int.TryParse(n, out var k) ? k : 0).DefaultIfEmpty(0).Max() + 1).ToString("0000000");
            }
            lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.SiReceivable), inv.Total, 0, partner.Id, $"Phải thu HĐ {inv.InvoiceNo}"));
            foreach (var l in inv.Lines)
            {
                var acc = l.AccountId ?? await _acc.RevenueRuleAsync(l.ItemId.HasValue ? items[l.ItemId.Value].Type : null);
                lines.Add(new JLine(acc, 0, l.Amount));
            }
            if (inv.VatAmount > 0) lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.SiVatOut), 0, inv.VatAmount));
        }
        else
        {
            if (!partner.IsVendor) throw new BusinessException("Đối tượng không phải nhà cung cấp.");
            if (string.IsNullOrWhiteSpace(inv.InvoiceNo)) throw new BusinessException("Nhập số hóa đơn của nhà cung cấp.");
            var dup = await _db.Invoices.AnyAsync(i => i.Id != inv.Id && i.Direction == InvoiceDirection.Purchase && i.PartnerId == inv.PartnerId
                && i.InvoiceNo == inv.InvoiceNo && i.Series == inv.Series && i.Status != InvoiceStatus.Cancelled);
            if (dup) throw new BusinessException($"Hóa đơn {inv.Series} {inv.InvoiceNo} của nhà cung cấp này đã được ghi nhận.");
            decimal credit = 0;
            foreach (var l in inv.Lines.Where(x => !x.GoodsPosted))
            {
                if (l.AccountId == null && l.ItemId.HasValue && items[l.ItemId.Value].IsStocked)
                    throw new BusinessException($"Hàng {items[l.ItemId.Value].Code} cần nhập kho qua đơn mua hàng; hóa đơn mua trực tiếp chỉ dùng cho dịch vụ / chi phí (chọn TK chi phí).");
                var acc = l.AccountId ?? await _acc.RuleAsync(PostingCatalog.PiExpense);
                lines.Add(new JLine(acc, l.Amount, 0, null, l.Description));
                credit += l.Amount;
            }
            if (inv.VatAmount > 0) { lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.PiVatIn), inv.VatAmount, 0)); credit += inv.VatAmount; }
            if (credit > 0) lines.Add(new JLine(await _acc.RuleAsync(PostingCatalog.PiPayable), 0, credit, partner.Id, $"Phải trả HĐ {inv.InvoiceNo}"));
        }

        if (inv.Id == 0) { _db.Invoices.Add(inv); await _db.SaveChangesAsync(); }
        if (lines.Count >= 2)
        {
            var je = await _acc.PostAsync(inv.Date, $"{inv.Direction.GetDisplayName()} {inv.Series} {inv.InvoiceNo} – {partner.Name}",
                inv.Direction == InvoiceDirection.Sales ? JournalSource.SalesInvoice : JournalSource.PurchaseInvoice, inv.Number, inv.Id, lines);
            inv.JournalEntryId = je.Id;
        }
        await _einv.IssueAsync(inv);   // hóa đơn điện tử: lỗi thì hủy toàn bộ việc phát hành
        if (replaced != null) _einv.MarkReplaced(replaced, inv);
        inv.Status = InvoiceStatus.Issued;
        await _db.SaveChangesAsync();
        await RefreshOrderStatusAsync(inv);
        if (replaced != null) await RefreshOrderStatusAsync(replaced);
    }

    /// <summary>Điều kiện lập hóa đơn thay thế: hóa đơn bán đã phát hành, chưa thu tiền</summary>
    public static void ValidateReplaceable(Invoice original)
    {
        if (original.Direction != InvoiceDirection.Sales) throw new BusinessException("Chỉ lập hóa đơn thay thế cho hóa đơn bán ra.");
        if (original.Status != InvoiceStatus.Issued) throw new BusinessException("Chỉ thay thế hóa đơn đã phát hành.");
        if (original.PaidAmount > 0) throw new BusinessException("Hóa đơn đã thu tiền một phần – hãy hủy phiếu thu trước hoặc dùng hóa đơn điều chỉnh.");
    }

    /// <summary>Hủy hóa đơn: đảo bút toán, trả lại số lượng chưa xuất hóa đơn cho đơn hàng</summary>
    public async Task CancelAsync(Invoice inv, string reason)
    {
        if (inv.Status == InvoiceStatus.Cancelled) throw new BusinessException("Hóa đơn đã hủy.");
        if (await _db.SalesCreditNotes.AnyAsync(n => n.InvoiceId == inv.Id && n.Status == DocStatus.Posted)) throw new BusinessException("Hóa đơn đã có hóa đơn điều chỉnh giảm (hàng trả lại / giảm giá): hủy các chứng từ điều chỉnh trước.");
        if (inv.Series == AssetService.AssetInvoiceSeries) throw new BusinessException("Công nợ này phát sinh từ ghi tăng TSCĐ: điều chỉnh hoặc thanh lý tài sản thay vì hủy chứng từ.");
        if (inv.PaidAmount > 0) throw new BusinessException("Hóa đơn đã thanh toán một phần, hãy hủy phiếu thu / chi trước.");
        if (inv.Status == InvoiceStatus.Issued) await _acc.ReverseAsync(inv.JournalEntryId, DateTime.Today, reason);
        await ReleaseOrderQtyAsync(inv);
        await _einv.CancelAsync(inv, reason);
        inv.Status = InvoiceStatus.Cancelled;
        inv.Note = string.IsNullOrWhiteSpace(reason) ? inv.Note : $"{inv.Note} [Hủy: {reason}]".Trim();
        await _db.SaveChangesAsync();
        await RefreshOrderStatusAsync(inv);
    }

    private async Task ReleaseOrderQtyAsync(Invoice inv)
    {
        var ids = inv.Lines.Where(l => l.SourceLineId != null).Select(l => l.SourceLineId!.Value).ToList();
        if (inv.SalesOrderId != null)
            foreach (var sl in await _db.SalesOrderLines.Where(x => ids.Contains(x.Id)).ToListAsync())
                sl.InvoicedQty -= inv.Lines.Where(l => l.SourceLineId == sl.Id).Sum(l => l.Quantity);
        if (inv.PurchaseOrderId != null)
            foreach (var pl in await _db.PurchaseOrderLines.Where(x => ids.Contains(x.Id)).ToListAsync())
                pl.InvoicedQty -= inv.Lines.Where(l => l.SourceLineId == pl.Id).Sum(l => l.Quantity);
    }

    private async Task RefreshOrderStatusAsync(Invoice inv)
    {
        if (inv.SalesOrderId != null)
        {
            var so = await _db.SalesOrders.Include(s => s.Lines).FirstAsync(s => s.Id == inv.SalesOrderId);
            SalesService.RefreshStatus(so);
        }
        if (inv.PurchaseOrderId != null)
        {
            var po = await _db.PurchaseOrders.Include(s => s.Lines).FirstAsync(s => s.Id == inv.PurchaseOrderId);
            PurchaseService.RefreshStatus(po);
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>Xóa hóa đơn nháp</summary>
    public async Task DeleteDraftAsync(Invoice inv)
    {
        if (inv.Status != InvoiceStatus.Draft) throw new BusinessException("Chỉ xóa được hóa đơn nháp.");
        await ReleaseOrderQtyAsync(inv);
        var so = inv.SalesOrderId; var po = inv.PurchaseOrderId;
        _db.InvoiceLines.RemoveRange(inv.Lines.ToList());
        _db.Invoices.Remove(inv);
        await _db.SaveChangesAsync();
        if (so != null) { var s = await _db.SalesOrders.Include(x => x.Lines).FirstAsync(x => x.Id == so); SalesService.RefreshStatus(s); }
        if (po != null) { var p = await _db.PurchaseOrders.Include(x => x.Lines).FirstAsync(x => x.Id == po); PurchaseService.RefreshStatus(p); }
        await _db.SaveChangesAsync();
    }
}

// =====================================================================
//  MUA HÀNG
// =====================================================================
public class PurchaseService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly InvoiceService _invoices;
    private readonly ErpSettings _cfg;
    private readonly CurrentUser _user;
    private readonly WorkflowService _wf;
    public PurchaseService(ErpDbContext db, InventoryService inv, InvoiceService invoices, IOptions<ErpSettings> cfg, CurrentUser user, WorkflowService wf)
    { _db = db; _inv = inv; _invoices = invoices; _cfg = cfg.Value; _user = user; _wf = wf; }

    public decimal ApprovalThreshold => _cfg.PoApprovalThreshold;

    public static void Recalc(PurchaseOrder po)
    {
        po.Subtotal = po.Lines.Sum(l => l.Amount);
        po.VatAmount = po.Lines.Sum(l => Math.Round(l.Amount * l.VatRate / 100m, 0));
        po.Total = po.Subtotal + po.VatAmount;
    }

    public static void RefreshStatus(PurchaseOrder po)
    {
        if (po.Status is PoStatus.Draft or PoStatus.PendingApproval or PoStatus.Cancelled) return;
        var received = po.Lines.All(l => l.ReceivedQty >= l.Quantity);
        var any = po.Lines.Any(l => l.ReceivedQty > 0);
        var invoiced = po.Lines.All(l => l.InvoicedQty + l.ReturnedQty >= l.Quantity);
        po.Status = received && invoiced ? PoStatus.Completed : received ? PoStatus.Received : any ? PoStatus.PartiallyReceived : PoStatus.Approved;
    }

    /// <summary>Gửi duyệt theo quy tắc phê duyệt (nhiều cấp, theo giá trị / chi nhánh); không có quy tắc nào áp dụng thì tự duyệt.
    /// Chưa khai báo quy tắc cho đơn mua thì dùng ngưỡng cấu hình cũ (từ ngưỡng → Ban Giám đốc duyệt).</summary>
    public async Task<string> SubmitAsync(PurchaseOrder po)
    {
        if (po.Status != PoStatus.Draft) throw new BusinessException("Chỉ gửi duyệt đơn nháp.");
        if (!po.Lines.Any()) throw new BusinessException("Đơn mua chưa có dòng hàng.");
        Recalc(po);
        if (await _db.Partners.AnyAsync(v => v.Id == po.VendorId && v.IsBlocked)) throw new BusinessException("Nhà cung cấp đang tạm ngừng giao dịch – không gửi duyệt đơn mua.");
        if (await _wf.HasRulesAsync(ApprovalDocType.PurchaseOrder))
        {
            var vendor = await _db.Partners.FindAsync(po.VendorId);
            var req = await _wf.StartAsync(ApprovalDocType.PurchaseOrder, po.Id, po.Number, po.Total, $"Đơn mua {po.Number} – {vendor?.Name}", po.BranchId);
            if (req != null) { po.Status = PoStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Đơn {po.Number} ({po.Total.ToMoney()}) đã gửi duyệt – {WorkflowService.Describe(req)}."; }
        }
        else if (po.Total >= _cfg.PoApprovalThreshold) { po.Status = PoStatus.PendingApproval; await _db.SaveChangesAsync(); return $"Đơn {po.Number} ({po.Total.ToMoney()}) vượt ngưỡng {_cfg.PoApprovalThreshold.ToMoney()}, đã chuyển Ban Giám đốc duyệt."; }
        po.Status = PoStatus.Approved; po.ApprovedById = _user.Id; po.ApprovedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return $"Đơn {po.Number} không cần duyệt theo quy tắc, đã được duyệt tự động.";
    }

    /// <summary>Duyệt / từ chối: theo luồng duyệt nếu đơn có yêu cầu duyệt, ngược lại duyệt một bước (đơn gửi duyệt trước khi có luồng)</summary>
    public async Task<string> ApproveAsync(PurchaseOrder po, bool approve, string? comment = null)
    {
        if (po.Status != PoStatus.PendingApproval) throw new BusinessException("Đơn không ở trạng thái chờ duyệt.");
        var req = await _wf.LatestAsync(ApprovalDocType.PurchaseOrder, po.Id);
        if (req is { Status: ApprovalStatus.Pending })
        {
            var st = await _wf.ActAsync(req, approve, comment);
            if (st == ApprovalStatus.Pending) return $"Đã duyệt cấp của bạn – {WorkflowService.Describe(req)}.";
            approve = st == ApprovalStatus.Approved;
        }
        else
        {
            var u = _user.Principal;
            if (u != null && !u.InAny(AppRoles.Approvers)) throw new BusinessException("Chỉ Ban Giám đốc duyệt đơn này.");
        }
        po.Status = approve ? PoStatus.Approved : PoStatus.Draft;
        if (approve) { po.ApprovedById = _user.Id; po.ApprovedAt = DateTime.Now; }
        await _db.SaveChangesAsync();
        return approve ? $"Đơn mua {po.Number} đã được duyệt hoàn tất." : $"Đã từ chối, đơn {po.Number} trở về nháp.";
    }

    /// <summary>Nhận hàng: lập phiếu nhập kho (Nợ 15x / Có 331)</summary>
    public async Task<InventoryDoc> ReceiveAsync(PurchaseOrder po, DateTime date, IDictionary<int, decimal> qtyByLine, IDictionary<int, (string? Lot, DateTime? Expiry)>? lotInfo = null)
    {
        if (po.Status is not (PoStatus.Approved or PoStatus.PartiallyReceived)) throw new BusinessException("Đơn mua chưa được duyệt hoặc đã nhận đủ.");
        var take = new List<(int, decimal, decimal)>();
        foreach (var l in po.Lines)
        {
            var q = qtyByLine.TryGetValue(l.Id, out var v) ? v : 0;
            if (q < 0 || q > l.Quantity - l.ReceivedQty) throw new BusinessException($"Số lượng nhận của dòng {l.Item?.Code} vượt số còn lại ({(l.Quantity - l.ReceivedQty).ToQty()}).");
            if (q > 0) take.Add((l.ItemId, q, l.UnitPrice));
        }
        if (!take.Any()) throw new BusinessException("Nhập số lượng thực nhận.");
        var doc = await _inv.CreateAndPostAsync(InvDocType.Receipt, date, po.WarehouseId!.Value, DocOrigin.Purchase, po.Number, po.Id, po.VendorId,
            $"Nhận hàng theo đơn mua {po.Number}", take, _user.Id, lotInfo);
        foreach (var l in po.Lines) if (qtyByLine.TryGetValue(l.Id, out var q)) l.ReceivedQty += q;
        RefreshStatus(po);
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Lập hóa đơn mua (nháp) cho phần đã nhận chưa có hóa đơn</summary>
    public async Task<Invoice> CreateInvoiceAsync(PurchaseOrder po, DateTime date, string? series, string? invoiceNo)
    {
        var lines = po.Lines.Where(l => l.BillableQty > 0).ToList();
        if (!lines.Any()) throw new BusinessException("Không còn hàng đã nhận chưa có hóa đơn.");
        var vendor = await _db.Partners.FindAsync(po.VendorId);
        var inv = new Invoice
        {
            Direction = InvoiceDirection.Purchase, Number = await _invoices.NextNumberAsync(InvoiceDirection.Purchase, date), Date = date,
            DueDate = date.AddDays(vendor?.PaymentTermDays ?? 30), PartnerId = po.VendorId, PurchaseOrderId = po.Id, ContractId = po.ContractId, BranchId = po.BranchId, Series = series, InvoiceNo = invoiceNo,
            CreatedById = _user.Id, Note = $"Theo đơn mua {po.Number}"
        };
        foreach (var l in lines)
        {
            var q = l.BillableQty;
            inv.Lines.Add(new InvoiceLine
            {
                ItemId = l.ItemId, Description = l.Item?.Name ?? "", Unit = l.Item?.Unit, Quantity = q, UnitPrice = l.UnitPrice,
                VatRate = l.VatRate, SourceLineId = l.Id, GoodsPosted = true
            });
            l.InvoicedQty += q;
        }
        InvoiceService.Recalc(inv);
        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync();
        return inv;
    }
}

// =====================================================================
//  BÁN HÀNG
// =====================================================================
public class SalesService
{
    private readonly ErpDbContext _db;
    private readonly InventoryService _inv;
    private readonly InvoiceService _invoices;
    private readonly AccountingService _acc;
    private readonly ErpSettings _cfg;
    private readonly CurrentUser _user;
    private readonly WorkflowService _wf;
    public SalesService(ErpDbContext db, InventoryService inv, InvoiceService invoices, AccountingService acc, IOptions<ErpSettings> cfg, CurrentUser user, WorkflowService wf)
    { _db = db; _inv = inv; _invoices = invoices; _acc = acc; _cfg = cfg.Value; _user = user; _wf = wf; }

    public static void Recalc(SalesOrder so)
    {
        so.Subtotal = so.Lines.Sum(l => l.Amount);
        so.VatAmount = so.Lines.Sum(l => Math.Round(l.Amount * l.VatRate / 100m, 0));
        so.Total = so.Subtotal + so.VatAmount;
    }

    public static void RefreshStatus(SalesOrder so)
    {
        if (so.Status is SoStatus.Draft or SoStatus.Cancelled) return;
        var delivered = so.Lines.All(l => l.DeliveredQty >= l.Quantity);
        var any = so.Lines.Any(l => l.DeliveredQty > 0);
        var invoiced = so.Lines.All(l => l.InvoicedQty >= l.Quantity);
        so.Status = delivered && invoiced ? SoStatus.Completed : delivered ? SoStatus.Delivered : any ? SoStatus.PartiallyDelivered : SoStatus.Confirmed;
    }

    /// <summary>Công nợ hiện tại + đơn đã xác nhận chưa xuất hóa đơn của khách</summary>
    public async Task<(decimal Balance, decimal OpenOrders)> ExposureAsync(int customerId, int excludeOrderId)
    {
        var balance = await _acc.BalanceAsync(AccountingService.Receivable, null, customerId);
        var open = await _db.SalesOrders.Include(s => s.Lines)
            .Where(s => s.CustomerId == customerId && s.Id != excludeOrderId && s.Status != SoStatus.Draft && s.Status != SoStatus.Cancelled && s.Status != SoStatus.Completed)
            .ToListAsync();
        var notInvoiced = open.Sum(s => s.Lines.Sum(l => Math.Round((l.Quantity - l.InvoicedQty) * l.NetPrice * (100 + l.VatRate) / 100, 0)));
        return (balance, notInvoiced);
    }

    /// <summary>Xác nhận đơn: kiểm tra hạn mức công nợ (Ban Giám đốc / Admin được duyệt vượt)</summary>
    public async Task<string?> ConfirmAsync(SalesOrder so, bool canOverride)
    {
        if (so.Status != SoStatus.Draft) throw new BusinessException("Chỉ xác nhận đơn nháp.");
        if (!so.Lines.Any()) throw new BusinessException("Đơn bán chưa có dòng hàng.");
        Recalc(so);
        var c = await _db.Partners.FindAsync(so.CustomerId) ?? throw new BusinessException("Không tìm thấy khách hàng.");
        if (c.IsBlocked) throw new BusinessException($"Khách hàng {c.Name} đang tạm ngừng giao dịch: {c.BlockReason}");
        string? warn = null;
        if (c.CreditLimit > 0)
        {
            var (bal, open) = await ExposureAsync(c.Id, so.Id);
            var exposure = bal + open + so.Total;
            if (exposure > c.CreditLimit)
            {
                var msg = $"Vượt hạn mức công nợ của {c.Name}: dư nợ {bal.ToMoney()} + đơn đang mở {open.ToMoney()} + đơn này {so.Total.ToMoney()} = {exposure.ToMoney()} > hạn mức {c.CreditLimit.ToMoney()}.";
                if (!canOverride) throw new BusinessException(msg + " Cần Ban Giám đốc xác nhận.");
                so.CreditApprovedById = _user.Id;
                warn = msg + " Đã được duyệt vượt hạn mức.";
            }
        }
        // Luồng duyệt đơn bán (theo giá trị): chưa được duyệt thì gửi duyệt, đơn giữ trạng thái nháp
        if (!await _wf.IsApprovedAsync(ApprovalDocType.SalesOrder, so.Id))
        {
            if (await _wf.IsPendingAsync(ApprovalDocType.SalesOrder, so.Id)) throw new BusinessException("Đơn đang chờ duyệt, chưa xác nhận được.");
            var req = await _wf.StartAsync(ApprovalDocType.SalesOrder, so.Id, so.Number, so.Total, $"Đơn bán {so.Number} – {c.Name}", so.BranchId);
            if (req != null) { await _db.SaveChangesAsync(); throw new PendingApprovalException($"Đơn {so.Number} ({so.Total.ToMoney()}) cần duyệt trước khi xác nhận – {WorkflowService.Describe(req)}."); }
        }
        so.Status = SoStatus.Confirmed;
        await _db.SaveChangesAsync();
        return warn;
    }

    /// <summary>Giao hàng: lập phiếu xuất kho (Nợ 632 / Có 15x). Dịch vụ không qua kho.</summary>
    public async Task<InventoryDoc?> DeliverAsync(SalesOrder so, DateTime date, IDictionary<int, decimal> qtyByLine)
    {
        if (so.Status is not (SoStatus.Confirmed or SoStatus.PartiallyDelivered)) throw new BusinessException("Đơn chưa xác nhận hoặc đã giao đủ.");
        var take = new List<(int, decimal, decimal)>();
        var any = false;
        foreach (var l in so.Lines)
        {
            var q = qtyByLine.TryGetValue(l.Id, out var v) ? v : 0;
            if (q < 0 || q > l.Quantity - l.DeliveredQty) throw new BusinessException($"Số lượng giao của {l.Item?.Code} vượt số còn lại ({(l.Quantity - l.DeliveredQty).ToQty()}).");
            if (q > 0) { any = true; if (l.Item!.IsStocked) take.Add((l.ItemId, q, 0)); }
        }
        if (!any) throw new BusinessException("Nhập số lượng giao.");
        InventoryDoc? doc = null;
        if (take.Any())
            doc = await _inv.CreateAndPostAsync(InvDocType.Issue, date, so.WarehouseId!.Value, DocOrigin.Sales, so.Number, so.Id, so.CustomerId,
                $"Xuất bán theo đơn {so.Number}", take, _user.Id);
        if (doc != null)
        {
            // Tự lập vận đơn gắn với phiếu xuất kho và đơn bán (cập nhật vận chuyển tại Giao hàng & vận chuyển)
            var cust = await _db.Partners.FindAsync(so.CustomerId);
            _db.Shipments.Add(new Shipment { InventoryDocId = doc.Id, SalesOrderId = so.Id, Address = cust?.Address, Receiver = cust?.ContactPerson, Number = await DocNumbers.NextAsync(_db.Shipments.Select(x => x.Number), "VD", date) });
        }
        foreach (var l in so.Lines) if (qtyByLine.TryGetValue(l.Id, out var q)) l.DeliveredQty += q;
        RefreshStatus(so);
        await _db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Lập hóa đơn bán (nháp) cho phần đã giao chưa xuất hóa đơn</summary>
    public async Task<Invoice> CreateInvoiceAsync(SalesOrder so, DateTime date)
    {
        var lines = so.Lines.Where(l => l.DeliveredQty - l.InvoicedQty > 0).ToList();
        if (!lines.Any()) throw new BusinessException("Không còn hàng đã giao chưa xuất hóa đơn.");
        var c = await _db.Partners.FindAsync(so.CustomerId);
        var inv = new Invoice
        {
            Direction = InvoiceDirection.Sales, Number = await _invoices.NextNumberAsync(InvoiceDirection.Sales, date), Date = date,
            DueDate = date.AddDays(c?.PaymentTermDays ?? 30), PartnerId = so.CustomerId, SalesOrderId = so.Id, ContractId = so.ContractId, BranchId = so.BranchId, Series = _cfg.InvoiceSeries,
            CreatedById = _user.Id, Note = $"Theo đơn bán {so.Number}"
        };
        foreach (var l in lines)
        {
            var q = l.DeliveredQty - l.InvoicedQty;
            inv.Lines.Add(new InvoiceLine
            {
                ItemId = l.ItemId, Description = l.Item?.Name ?? "", Unit = l.Item?.Unit, Quantity = q,
                UnitPrice = Math.Round(l.NetPrice, 0), VatRate = l.VatRate, SourceLineId = l.Id
            });
            l.InvoicedQty += q;
        }
        InvoiceService.Recalc(inv);
        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync();
        return inv;
    }
}
