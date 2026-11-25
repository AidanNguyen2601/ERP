using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services.Reporting;

internal static class Rb
{
    public static readonly Field[] TimeFields =
    {
        new("month", "Tháng", FieldKind.Text), new("quarter", "Quý", FieldKind.Text), new("year", "Năm", FieldKind.Text)
    };
    public static Row Time(Row r, string key, DateTime d)
    {
        r[key] = d.Date; r["month"] = d.ToString("yyyy-MM"); r["quarter"] = $"{d.Year}-Q{(d.Month - 1) / 3 + 1}"; r["year"] = d.Year.ToString();
        return r;
    }
    public static string TypeName(ItemType? t) => t?.GetDisplayName() ?? "Khác / dịch vụ trực tiếp";
    public static string YesNo(bool b) => b ? "Có" : "Không";
    public static Field[] With(params Field[] fields) => fields.Concat(TimeFields).ToArray();
}

/// <summary>Chi tiết hóa đơn bán ra: doanh thu thuần = hóa đơn bán − hàng bán trả lại − giảm giá (dòng điều chỉnh mang dấu âm)</summary>
public class SalesDataset : Dataset
{
    public override string Key => "sales";
    public override string Title => "Doanh thu bán hàng (chi tiết hóa đơn)";
    public override string Group => "Bán hàng";
    public override string Description => "Từng dòng hóa đơn bán ra đã phát hành; hàng bán trả lại / giảm giá hiện thành dòng âm nên tổng Thành tiền chính là doanh thu thuần.";
    public override string Roles => AppRoles.FinanceView + "," + AppRoles.Sales;
    public override string DateLabel => "Ngày hóa đơn";
    private static readonly Field[] F =
    {
        new("date", "Ngày hóa đơn", FieldKind.Date), new("docType", "Loại chứng từ", FieldKind.Text), new("invoiceNo", "Số hóa đơn", FieldKind.Text), new("salesOrder", "Đơn bán", FieldKind.Text),
        new("customerCode", "Mã khách hàng", FieldKind.Text), new("customer", "Khách hàng", FieldKind.Text), new("salesperson", "Nhân viên kinh doanh", FieldKind.Text),
        new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text), new("itemType", "Loại hàng", FieldKind.Text), new("unit", "ĐVT", FieldKind.Text),
        new("qty", "Số lượng", FieldKind.Qty), new("price", "Đơn giá", FieldKind.Money), new("amount", "Thành tiền (chưa thuế)", FieldKind.Money),
        new("vat", "Thuế GTGT", FieldKind.Money), new("total", "Tổng thanh toán", FieldKind.Money), new("vatRate", "Thuế suất (%)", FieldKind.Number),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.InvoiceLines.AsNoTracking().Where(l => l.Invoice!.Direction == InvoiceDirection.Sales && l.Invoice.Status == InvoiceStatus.Issued && l.Invoice.Series != "DK");
        if (from.HasValue) q = q.Where(l => l.Invoice!.Date >= from.Value);
        if (to.HasValue) q = q.Where(l => l.Invoice!.Date <= to.Value);
        var lines = await q.Select(l => new
        {
            l.Invoice!.Date, l.Invoice.InvoiceNo, l.Invoice.Number, SO = l.Invoice.SalesOrder != null ? l.Invoice.SalesOrder.Number : null,
            CustCode = l.Invoice.Partner!.Code, Cust = l.Invoice.Partner.Name, Sp = l.Invoice.SalesOrder != null && l.Invoice.SalesOrder.CreatedBy != null ? l.Invoice.SalesOrder.CreatedBy.FullName : null,
            ItemCode = l.Item != null ? l.Item.Code : null, ItemName = l.Item != null ? l.Item.Name : null, Type = l.Item != null ? (ItemType?)l.Item.Type : null,
            l.Description, l.Unit, l.Quantity, l.UnitPrice, l.Amount, l.VatAmount, l.VatRate
        }).ToListAsync();
        var rows = lines.Select(l => Rb.Time(new Row
        {
            ["docType"] = "Hóa đơn bán", ["invoiceNo"] = l.InvoiceNo ?? l.Number, ["salesOrder"] = l.SO, ["customerCode"] = l.CustCode, ["customer"] = l.Cust, ["salesperson"] = l.Sp,
            ["itemCode"] = l.ItemCode, ["item"] = l.ItemName ?? l.Description, ["itemType"] = Rb.TypeName(l.Type), ["unit"] = l.Unit,
            ["qty"] = l.Quantity, ["price"] = l.UnitPrice, ["amount"] = l.Amount, ["vat"] = l.VatAmount, ["total"] = l.Amount + l.VatAmount, ["vatRate"] = (decimal)l.VatRate
        }, "date", l.Date)).ToList();

        var cq = db.SalesCreditLines.AsNoTracking().Where(c => c.SalesCreditNote!.Status == DocStatus.Posted);
        if (from.HasValue) cq = cq.Where(c => c.SalesCreditNote!.Date >= from.Value);
        if (to.HasValue) cq = cq.Where(c => c.SalesCreditNote!.Date <= to.Value);
        var credits = await cq.Select(c => new
        {
            c.SalesCreditNote!.Date, c.SalesCreditNote.Number, c.SalesCreditNote.Kind, InvNo = c.SalesCreditNote.Invoice!.InvoiceNo,
            SO = c.SalesCreditNote.Invoice.SalesOrder != null ? c.SalesCreditNote.Invoice.SalesOrder.Number : null,
            CustCode = c.SalesCreditNote.Customer!.Code, Cust = c.SalesCreditNote.Customer.Name,
            Sp = c.SalesCreditNote.Invoice.SalesOrder != null && c.SalesCreditNote.Invoice.SalesOrder.CreatedBy != null ? c.SalesCreditNote.Invoice.SalesOrder.CreatedBy.FullName : null,
            ItemCode = c.Item != null ? c.Item.Code : null, ItemName = c.Item != null ? c.Item.Name : null, Type = c.Item != null ? (ItemType?)c.Item.Type : null,
            c.Description, c.Quantity, c.UnitPrice, c.Amount, c.VatAmount, c.VatRate
        }).ToListAsync();
        rows.AddRange(credits.Select(c => Rb.Time(new Row
        {
            ["docType"] = c.Kind.GetDisplayName(), ["invoiceNo"] = c.Number, ["salesOrder"] = c.SO, ["customerCode"] = c.CustCode, ["customer"] = c.Cust, ["salesperson"] = c.Sp,
            ["itemCode"] = c.ItemCode, ["item"] = c.ItemName ?? c.Description, ["itemType"] = Rb.TypeName(c.Type), ["unit"] = null,
            ["qty"] = -c.Quantity, ["price"] = c.UnitPrice, ["amount"] = -c.Amount, ["vat"] = -c.VatAmount, ["total"] = -(c.Amount + c.VatAmount), ["vatRate"] = (decimal)c.VatRate
        }, "date", c.Date)));
        return rows;
    }
}

/// <summary>Lãi gộp theo đơn bán × mặt hàng: doanh thu (hóa đơn − điều chỉnh giảm) so với giá vốn (xuất bán − hàng trả lại nhập kho)</summary>
public class MarginDataset : Dataset
{
    public override string Key => "sales-margin";
    public override string Title => "Lãi gộp bán hàng (đơn bán × mặt hàng)";
    public override string Group => "Bán hàng";
    public override string Description => "Doanh thu thuần và giá vốn theo từng đơn bán × mặt hàng. Thời gian lấy theo ngày đơn bán.";
    public override string Roles => AppRoles.FinanceView;
    public override string DateLabel => "Ngày đơn bán";
    private static readonly Field[] F =
    {
        new("date", "Ngày đơn bán", FieldKind.Date), new("salesOrder", "Đơn bán", FieldKind.Text), new("customerCode", "Mã khách hàng", FieldKind.Text), new("customer", "Khách hàng", FieldKind.Text),
        new("salesperson", "Nhân viên kinh doanh", FieldKind.Text), new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text), new("itemType", "Loại hàng", FieldKind.Text),
        new("qty", "Số lượng bán (thuần)", FieldKind.Qty), new("revenue", "Doanh thu thuần", FieldKind.Money), new("cost", "Giá vốn", FieldKind.Money), new("margin", "Lãi gộp", FieldKind.Money),
        new("marginPct", "Tỷ lệ lãi gộp (%)", FieldKind.Percent),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var sq = db.SalesOrders.AsNoTracking().Where(s => s.Status != SoStatus.Draft && s.Status != SoStatus.Cancelled);
        if (from.HasValue) sq = sq.Where(s => s.Date >= from.Value);
        if (to.HasValue) sq = sq.Where(s => s.Date <= to.Value);
        var sos = (await sq.Select(s => new { s.Id, s.Number, s.Date, CustCode = s.Customer!.Code, Cust = s.Customer.Name, Sp = s.CreatedBy != null ? s.CreatedBy.FullName : null }).ToListAsync()).ToDictionary(s => s.Id);
        var ids = sos.Keys.ToList();
        if (ids.Count == 0) return new();

        var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
        var rev = (await db.InvoiceLines.AsNoTracking().Where(l => l.Invoice!.Direction == InvoiceDirection.Sales && l.Invoice.Status == InvoiceStatus.Issued && l.Invoice.SalesOrderId != null && l.ItemId != null && ids.Contains(l.Invoice.SalesOrderId.Value))
            .Select(l => new { SO = l.Invoice!.SalesOrderId!.Value, Item = l.ItemId!.Value, l.Amount }).ToListAsync()).GroupBy(x => (x.SO, x.Item)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var cred = await db.SalesCreditLines.AsNoTracking().Where(c => c.SalesCreditNote!.Status == DocStatus.Posted && c.ItemId != null && c.SalesCreditNote.Invoice!.SalesOrderId != null && ids.Contains(c.SalesCreditNote.Invoice.SalesOrderId!.Value))
            .Select(c => new { SO = c.SalesCreditNote!.Invoice!.SalesOrderId!.Value, Item = c.ItemId!.Value, c.Amount, c.Quantity, Note = c.SalesCreditNote.Id }).ToListAsync();
        var noteSo = cred.GroupBy(c => c.Note).ToDictionary(g => g.Key, g => g.First().SO);
        var dq = (await db.InventoryDocLines.AsNoTracking().Where(l => l.InventoryDoc!.Origin == DocOrigin.Sales && l.InventoryDoc.Type == InvDocType.Issue && l.InventoryDoc.Status == DocStatus.Posted && l.InventoryDoc.OriginId != null && ids.Contains(l.InventoryDoc.OriginId.Value))
            .Select(l => new { SO = l.InventoryDoc!.OriginId!.Value, l.ItemId, l.Quantity, l.Amount }).ToListAsync()).GroupBy(x => (x.SO, x.ItemId)).ToDictionary(g => g.Key, g => (Q: g.Sum(x => x.Quantity), A: g.Sum(x => x.Amount)));
        var ret = (await db.InventoryDocLines.AsNoTracking().Where(l => l.InventoryDoc!.Origin == DocOrigin.SalesReturn && l.InventoryDoc.Type == InvDocType.Receipt && l.InventoryDoc.Status == DocStatus.Posted && l.InventoryDoc.OriginId != null)
            .Select(l => new { Note = l.InventoryDoc!.OriginId!.Value, l.ItemId, l.Quantity, l.Amount }).ToListAsync())
            .Where(x => noteSo.ContainsKey(x.Note)).GroupBy(x => (SO: noteSo[x.Note], x.ItemId)).ToDictionary(g => g.Key, g => (Q: g.Sum(x => x.Quantity), A: g.Sum(x => x.Amount)));
        var creditAmt = cred.GroupBy(c => (c.SO, c.Item)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var keys = rev.Keys.Union(dq.Keys).Union(creditAmt.Keys).Union(ret.Keys).Where(k => sos.ContainsKey(k.Item1)).ToList();
        var rows = new List<Row>();
        foreach (var k in keys)
        {
            var so = sos[k.Item1]; items.TryGetValue(k.Item2, out var it);
            var d = dq.GetValueOrDefault(k); var rt = ret.GetValueOrDefault(k);
            var revenue = rev.GetValueOrDefault(k) - creditAmt.GetValueOrDefault(k);
            var cost = d.A - rt.A; var qty = d.Q - rt.Q; var margin = revenue - cost;
            rows.Add(Rb.Time(new Row
            {
                ["salesOrder"] = so.Number, ["customerCode"] = so.CustCode, ["customer"] = so.Cust, ["salesperson"] = so.Sp, ["itemCode"] = it?.Code, ["item"] = it?.Name, ["itemType"] = Rb.TypeName(it?.Type),
                ["qty"] = qty, ["revenue"] = revenue, ["cost"] = cost, ["margin"] = margin, ["marginPct"] = revenue == 0 ? 0m : Math.Round(margin * 100m / revenue, 2)
            }, "date", so.Date));
        }
        return rows;
    }
}

/// <summary>Dòng đơn bán: đã giao / đã lập hóa đơn / còn phải giao</summary>
public class SalesOrderDataset : Dataset
{
    public override string Key => "sales-orders";
    public override string Title => "Đơn bán hàng (theo dòng hàng)";
    public override string Group => "Bán hàng";
    public override string Description => "Theo dõi tiến độ giao hàng và lập hóa đơn của từng dòng đơn bán.";
    public override string Roles => AppRoles.FinanceView + "," + AppRoles.Sales + "," + AppRoles.Warehouse;
    public override string DateLabel => "Ngày đơn bán";
    private static readonly Field[] F =
    {
        new("date", "Ngày đơn bán", FieldKind.Date), new("deliveryDate", "Ngày giao dự kiến", FieldKind.Date), new("salesOrder", "Đơn bán", FieldKind.Text), new("status", "Trạng thái", FieldKind.Text),
        new("customer", "Khách hàng", FieldKind.Text), new("salesperson", "Nhân viên kinh doanh", FieldKind.Text), new("warehouse", "Kho xuất", FieldKind.Text),
        new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text), new("itemType", "Loại hàng", FieldKind.Text),
        new("qtyOrdered", "SL đặt", FieldKind.Qty), new("qtyDelivered", "SL đã giao", FieldKind.Qty), new("qtyInvoiced", "SL đã lập hóa đơn", FieldKind.Qty), new("qtyPending", "SL còn phải giao", FieldKind.Qty),
        new("amount", "Giá trị đơn (chưa thuế)", FieldKind.Money), new("pendingAmount", "Giá trị còn phải giao", FieldKind.Money), new("lateDays", "Số ngày trễ giao", FieldKind.Number),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.SalesOrderLines.AsNoTracking().Where(l => l.SalesOrder!.Status != SoStatus.Draft);
        if (from.HasValue) q = q.Where(l => l.SalesOrder!.Date >= from.Value);
        if (to.HasValue) q = q.Where(l => l.SalesOrder!.Date <= to.Value);
        var ls = await q.Select(l => new
        {
            l.SalesOrder!.Date, l.SalesOrder.DeliveryDate, l.SalesOrder.Number, l.SalesOrder.Status, Cust = l.SalesOrder.Customer!.Name, Sp = l.SalesOrder.CreatedBy != null ? l.SalesOrder.CreatedBy.FullName : null,
            Wh = l.SalesOrder.Warehouse != null ? l.SalesOrder.Warehouse.Name : null, ItemCode = l.Item!.Code, ItemName = l.Item.Name, l.Item.Type,
            l.Quantity, l.DeliveredQty, l.InvoicedQty, l.UnitPrice, l.DiscountPct
        }).ToListAsync();
        var today = DateTime.Today;
        return ls.Select(l =>
        {
            var open = l.Status is not SoStatus.Cancelled && l.Type != ItemType.Service;
            var pending = open ? Math.Max(0, l.Quantity - l.DeliveredQty) : 0m;
            var net = l.UnitPrice * (100 - l.DiscountPct) / 100;
            return Rb.Time(new Row
            {
                ["deliveryDate"] = l.DeliveryDate.Date, ["salesOrder"] = l.Number, ["status"] = l.Status.GetDisplayName(), ["customer"] = l.Cust, ["salesperson"] = l.Sp, ["warehouse"] = l.Wh,
                ["itemCode"] = l.ItemCode, ["item"] = l.ItemName, ["itemType"] = Rb.TypeName(l.Type), ["qtyOrdered"] = l.Quantity, ["qtyDelivered"] = l.DeliveredQty, ["qtyInvoiced"] = l.InvoicedQty,
                ["qtyPending"] = pending, ["amount"] = Math.Round(l.Quantity * net, 0), ["pendingAmount"] = Math.Round(pending * net, 0),
                ["lateDays"] = pending > 0 && l.DeliveryDate.Date < today ? (decimal)(today - l.DeliveryDate.Date).Days : 0m
            }, "date", l.Date);
        }).ToList();
    }
}


/// <summary>Dòng đơn mua: đã nhận / đã trả / đã lập hóa đơn / còn phải nhận</summary>
public class PurchaseOrderDataset : Dataset
{
    public override string Key => "purchase";
    public override string Title => "Mua hàng (đơn mua theo dòng hàng)";
    public override string Group => "Mua hàng";
    public override string Description => "Giá trị đặt mua, nhận hàng, trả lại nhà cung cấp và tiến độ lập hóa đơn của từng dòng đơn mua.";
    public override string Roles => AppRoles.FinanceView + "," + AppRoles.Purchasing + "," + AppRoles.Warehouse;
    public override string DateLabel => "Ngày đặt mua";
    private static readonly Field[] F =
    {
        new("date", "Ngày đặt", FieldKind.Date), new("expectedDate", "Ngày giao dự kiến", FieldKind.Date), new("po", "Đơn mua", FieldKind.Text), new("status", "Trạng thái", FieldKind.Text),
        new("vendorCode", "Mã NCC", FieldKind.Text), new("vendor", "Nhà cung cấp", FieldKind.Text), new("warehouse", "Kho nhận", FieldKind.Text),
        new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text), new("itemType", "Loại hàng", FieldKind.Text), new("unitPrice", "Đơn giá mua", FieldKind.Money),
        new("qtyOrdered", "SL đặt", FieldKind.Qty), new("qtyReceived", "SL đã nhận", FieldKind.Qty), new("qtyReturned", "SL đã trả NCC", FieldKind.Qty), new("qtyInvoiced", "SL đã lập hóa đơn", FieldKind.Qty),
        new("qtyPending", "SL còn phải nhận", FieldKind.Qty), new("orderedAmount", "Giá trị đặt (chưa thuế)", FieldKind.Money), new("receivedAmount", "Giá trị đã nhận (chưa thuế)", FieldKind.Money),
        new("returnedAmount", "Giá trị đã trả NCC", FieldKind.Money), new("netReceivedAmount", "Giá trị nhận thuần", FieldKind.Money), new("lateDays", "Số ngày trễ giao", FieldKind.Number),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrder!.Status != PoStatus.Draft);
        if (from.HasValue) q = q.Where(l => l.PurchaseOrder!.Date >= from.Value);
        if (to.HasValue) q = q.Where(l => l.PurchaseOrder!.Date <= to.Value);
        var ls = await q.Select(l => new
        {
            l.Id, l.PurchaseOrder!.Date, l.PurchaseOrder.ExpectedDate, l.PurchaseOrder.Number, l.PurchaseOrder.Status, VCode = l.PurchaseOrder.Vendor!.Code, Vendor = l.PurchaseOrder.Vendor.Name,
            Wh = l.PurchaseOrder.Warehouse != null ? l.PurchaseOrder.Warehouse.Name : null, ItemCode = l.Item!.Code, ItemName = l.Item.Name, l.Item.Type,
            l.UnitPrice, l.Quantity, l.ReceivedQty, l.InvoicedQty
        }).ToListAsync();
        var returned = (await db.PurchaseReturns.AsNoTracking().GroupBy(r => r.PurchaseOrderLineId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity) }).ToListAsync()).ToDictionary(x => x.Key, x => x.Q);
        var today = DateTime.Today;
        return ls.Select(l =>
        {
            var ret = returned.GetValueOrDefault(l.Id);
            var pending = l.Status is PoStatus.Cancelled ? 0m : Math.Max(0, l.Quantity - l.ReceivedQty);
            return Rb.Time(new Row
            {
                ["expectedDate"] = l.ExpectedDate.Date, ["po"] = l.Number, ["status"] = l.Status.GetDisplayName(), ["vendorCode"] = l.VCode, ["vendor"] = l.Vendor, ["warehouse"] = l.Wh,
                ["itemCode"] = l.ItemCode, ["item"] = l.ItemName, ["itemType"] = Rb.TypeName(l.Type), ["unitPrice"] = l.UnitPrice,
                ["qtyOrdered"] = l.Quantity, ["qtyReceived"] = l.ReceivedQty, ["qtyReturned"] = ret, ["qtyInvoiced"] = l.InvoicedQty, ["qtyPending"] = pending,
                ["orderedAmount"] = Math.Round(l.Quantity * l.UnitPrice, 0), ["receivedAmount"] = Math.Round(l.ReceivedQty * l.UnitPrice, 0), ["returnedAmount"] = Math.Round(ret * l.UnitPrice, 0),
                ["netReceivedAmount"] = Math.Round((l.ReceivedQty - ret) * l.UnitPrice, 0),
                ["lateDays"] = pending > 0 && l.ExpectedDate.Date < today ? (decimal)(today - l.ExpectedDate.Date).Days : 0m
            }, "date", l.Date);
        }).ToList();
    }
}

/// <summary>Tồn kho theo mặt hàng × kho tại một ngày (tính từ thẻ kho nên khớp TK 152 / 155 / 156)</summary>
public class StockDataset : Dataset
{
    public override string Key => "stock";
    public override string Title => "Tồn kho (mặt hàng × kho)";
    public override string Group => "Kho";
    public override string Description => "Số lượng và giá trị tồn tại ngày chọn (mặc định hôm nay), tính từ thẻ kho nên khớp sổ cái các TK hàng tồn kho.";
    public override DateMode DateMode => DateMode.AsOf;
    public override string DateLabel => "Tồn đến ngày";
    private static readonly Field[] F =
    {
        new("warehouseCode", "Mã kho", FieldKind.Text), new("warehouse", "Kho", FieldKind.Text), new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text),
        new("itemType", "Loại hàng", FieldKind.Text), new("unit", "ĐVT", FieldKind.Text), new("qty", "Số lượng tồn", FieldKind.Qty), new("value", "Giá trị tồn", FieldKind.Money),
        new("avgCost", "Giá vốn bình quân hiện tại", FieldKind.Money), new("minStock", "Tồn tối thiểu", FieldKind.Qty), new("itemTotalQty", "Tổng tồn mặt hàng (mọi kho)", FieldKind.Qty), new("belowMin", "Dưới tồn tối thiểu", FieldKind.Text),
    };
    public override IReadOnlyList<Field> Fields { get; } = F;

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.Today).Date;
        var agg = await db.StockMoves.AsNoTracking().Where(m => m.Date <= t).GroupBy(m => new { m.ItemId, m.WarehouseId }).Select(g => new { g.Key.ItemId, g.Key.WarehouseId, Q = g.Sum(x => x.Quantity), V = g.Sum(x => x.Amount) }).ToListAsync();
        var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
        var whs = await db.Warehouses.AsNoTracking().ToDictionaryAsync(w => w.Id);
        var totals = agg.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Q));
        return agg.Where(a => a.Q != 0 || a.V != 0).Select(a =>
        {
            var it = items[a.ItemId]; var w = whs[a.WarehouseId]; var tot = totals[a.ItemId];
            return new Row
            {
                ["warehouseCode"] = w.Code, ["warehouse"] = w.Name, ["itemCode"] = it.Code, ["item"] = it.Name, ["itemType"] = Rb.TypeName(it.Type), ["unit"] = it.Unit, ["qty"] = a.Q, ["value"] = a.V,
                ["avgCost"] = it.AvgCost, ["minStock"] = it.MinStock, ["itemTotalQty"] = tot, ["belowMin"] = Rb.YesNo(it.MinStock > 0 && tot < it.MinStock)
            };
        }).ToList();
    }
}

/// <summary>Phát sinh nhập – xuất kho từ thẻ kho</summary>
public class StockMoveDataset : Dataset
{
    public override string Key => "stock-moves";
    public override string Title => "Nhập – xuất kho (thẻ kho)";
    public override string Group => "Kho";
    public override string Description => "Từng dòng nhập / xuất kho kèm loại phiếu và nguồn; phiếu hủy (đảo) hiện thành dòng ngược dấu.";
    public override string DateLabel => "Ngày phát sinh";
    private static readonly Field[] F =
    {
        new("date", "Ngày", FieldKind.Date), new("docNumber", "Số phiếu", FieldKind.Text), new("docType", "Loại phiếu", FieldKind.Text), new("origin", "Nguồn phát sinh", FieldKind.Text), new("reversal", "Phiếu hủy (đảo)", FieldKind.Text),
        new("warehouse", "Kho", FieldKind.Text), new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text), new("itemType", "Loại hàng", FieldKind.Text),
        new("qtyIn", "SL nhập", FieldKind.Qty), new("qtyOut", "SL xuất", FieldKind.Qty), new("netQty", "SL nhập − xuất", FieldKind.Qty),
        new("amountIn", "Giá trị nhập", FieldKind.Money), new("amountOut", "Giá trị xuất", FieldKind.Money), new("netAmount", "Giá trị nhập − xuất", FieldKind.Money),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.StockMoves.AsNoTracking().AsQueryable();
        if (from.HasValue) q = q.Where(m => m.Date >= from.Value);
        if (to.HasValue) q = q.Where(m => m.Date <= to.Value);
        var ms = await (from m in q join d in db.InventoryDocs on m.InventoryDocId equals d.Id
                        select new { m.Date, m.DocNumber, d.Type, d.Origin, Wh = m.Warehouse!.Name, ItemCode = m.Item!.Code, ItemName = m.Item.Name, ItemType = m.Item.Type, m.Quantity, m.Amount }).ToListAsync();
        return ms.Select(m => Rb.Time(new Row
        {
            ["docNumber"] = m.DocNumber, ["docType"] = m.Type.GetDisplayName(), ["origin"] = m.Origin.GetDisplayName(), ["reversal"] = Rb.YesNo(m.DocNumber.EndsWith("(hủy)")),
            ["warehouse"] = m.Wh, ["itemCode"] = m.ItemCode, ["item"] = m.ItemName, ["itemType"] = Rb.TypeName(m.ItemType),
            ["qtyIn"] = m.Quantity > 0 ? m.Quantity : 0m, ["qtyOut"] = m.Quantity < 0 ? -m.Quantity : 0m, ["netQty"] = m.Quantity,
            ["amountIn"] = m.Amount > 0 ? m.Amount : 0m, ["amountOut"] = m.Amount < 0 ? -m.Amount : 0m, ["netAmount"] = m.Amount
        }, "date", m.Date)).ToList();
    }
}

/// <summary>Công nợ theo hóa đơn còn dư nợ, tính tuổi nợ tại hôm nay</summary>
public class DebtDataset : Dataset
{
    public override string Key => "debts";
    public override string Title => "Công nợ phải thu / phải trả (hóa đơn còn dư nợ)";
    public override string Group => "Công nợ & tiền";
    public override string Description => "Các hóa đơn đã phát hành còn dư nợ tại hôm nay, kèm số ngày quá hạn và nhóm tuổi nợ.";
    public override string Roles => AppRoles.FinanceView;
    public override DateMode DateMode => DateMode.None;
    private static readonly Field[] F =
    {
        new("direction", "Loại công nợ", FieldKind.Text), new("partnerCode", "Mã đối tượng", FieldKind.Text), new("partner", "Đối tượng", FieldKind.Text), new("invoiceNo", "Số hóa đơn", FieldKind.Text),
        new("date", "Ngày hóa đơn", FieldKind.Date), new("dueDate", "Hạn thanh toán", FieldKind.Date), new("total", "Giá trị hóa đơn", FieldKind.Money), new("paid", "Đã thanh toán / cấn trừ", FieldKind.Money),
        new("outstanding", "Còn nợ", FieldKind.Money), new("overdueDays", "Số ngày quá hạn", FieldKind.Number), new("bucket", "Nhóm tuổi nợ", FieldKind.Text), new("overdueAmount", "Nợ quá hạn", FieldKind.Money),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var today = DateTime.Today;
        var inv = await db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Issued && i.Total > i.PaidAmount)
            .Select(i => new { i.Direction, PCode = i.Partner!.Code, P = i.Partner.Name, i.InvoiceNo, i.Number, i.Date, i.DueDate, i.Total, i.PaidAmount }).ToListAsync();
        return inv.Select(i =>
        {
            var od = Math.Max(0, (today - i.DueDate.Date).Days); var open = i.Total - i.PaidAmount;
            return Rb.Time(new Row
            {
                ["direction"] = i.Direction == InvoiceDirection.Sales ? "Phải thu" : "Phải trả", ["partnerCode"] = i.PCode, ["partner"] = i.P, ["invoiceNo"] = i.InvoiceNo ?? i.Number,
                ["dueDate"] = i.DueDate.Date, ["total"] = i.Total, ["paid"] = i.PaidAmount, ["outstanding"] = open, ["overdueDays"] = (decimal)od,
                ["bucket"] = od == 0 ? "0. Chưa đến hạn" : od <= 30 ? "1. Quá hạn 1–30 ngày" : od <= 60 ? "2. Quá hạn 31–60 ngày" : od <= 90 ? "3. Quá hạn 61–90 ngày" : "4. Quá hạn trên 90 ngày",
                ["overdueAmount"] = od > 0 ? open : 0m
            }, "date", i.Date);
        }).ToList();
    }
}

/// <summary>Dòng bút toán đã ghi sổ</summary>
public class JournalDataset : Dataset
{
    public override string Key => "journal";
    public override string Title => "Bút toán kế toán (chi tiết dòng định khoản)";
    public override string Group => "Kế toán";
    public override string Description => "Mọi dòng Nợ / Có của bút toán đã ghi sổ, kèm tài khoản, đối tượng, nguồn chứng từ.";
    public override string Roles => AppRoles.FinanceView;
    public override string DateLabel => "Ngày hạch toán";
    private static readonly Field[] F =
    {
        new("date", "Ngày hạch toán", FieldKind.Date), new("entryNo", "Số bút toán", FieldKind.Text), new("source", "Nguồn", FieldKind.Text), new("sourceRef", "Chứng từ gốc", FieldKind.Text),
        new("accountCode", "Số hiệu TK", FieldKind.Text), new("account", "Tên tài khoản", FieldKind.Text), new("accountGroup", "Nhóm TK (đầu số)", FieldKind.Text), new("partner", "Đối tượng", FieldKind.Text),
        new("description", "Diễn giải", FieldKind.Text), new("debit", "Phát sinh Nợ", FieldKind.Money), new("credit", "Phát sinh Có", FieldKind.Money), new("net", "Nợ − Có", FieldKind.Money),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.JournalLines.AsNoTracking().Where(l => l.JournalEntry!.Status == DocStatus.Posted);
        if (from.HasValue) q = q.Where(l => l.JournalEntry!.Date >= from.Value);
        if (to.HasValue) q = q.Where(l => l.JournalEntry!.Date <= to.Value);
        var ls = await q.Select(l => new
        {
            l.JournalEntry!.Date, l.JournalEntry.Number, l.JournalEntry.Source, l.JournalEntry.SourceRef, Code = l.Account!.Code, Acc = l.Account.Name,
            Partner = l.Partner != null ? l.Partner.Name : null, Desc = l.Description ?? l.JournalEntry.Description, l.Debit, l.Credit
        }).ToListAsync();
        return ls.Select(l => Rb.Time(new Row
        {
            ["entryNo"] = l.Number, ["source"] = l.Source.GetDisplayName(), ["sourceRef"] = l.SourceRef, ["accountCode"] = l.Code, ["account"] = l.Acc, ["accountGroup"] = l.Code[..1],
            ["partner"] = l.Partner, ["description"] = l.Desc, ["debit"] = l.Debit, ["credit"] = l.Credit, ["net"] = l.Debit - l.Credit
        }, "date", l.Date)).ToList();
    }
}

/// <summary>Phiếu thu / chi / chuyển tiền</summary>
public class CashDataset : Dataset
{
    public override string Key => "cash";
    public override string Title => "Thu chi tiền (phiếu thu / chi)";
    public override string Group => "Công nợ & tiền";
    public override string Description => "Phiếu thu, phiếu chi, chuyển tiền nội bộ đã ghi sổ theo quỹ / tài khoản ngân hàng.";
    public override string Roles => AppRoles.FinanceView;
    private static readonly Field[] F =
    {
        new("date", "Ngày", FieldKind.Date), new("number", "Số phiếu", FieldKind.Text), new("type", "Loại phiếu", FieldKind.Text), new("purpose", "Nội dung", FieldKind.Text),
        new("cashAccount", "Quỹ / tài khoản", FieldKind.Text), new("partner", "Đối tượng", FieldKind.Text), new("counterAccount", "TK đối ứng", FieldKind.Text), new("description", "Diễn giải", FieldKind.Text),
        new("receipt", "Thu", FieldKind.Money), new("payment", "Chi", FieldKind.Money), new("net", "Thu − Chi", FieldKind.Money),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.CashVouchers.AsNoTracking().Where(v => v.Status == DocStatus.Posted);
        if (from.HasValue) q = q.Where(v => v.Date >= from.Value);
        if (to.HasValue) q = q.Where(v => v.Date <= to.Value);
        var vs = await q.Select(v => new { v.Date, v.Number, v.Type, v.Purpose, Ca = v.CashAccount!.Name, P = v.Partner != null ? v.Partner.Name : null, Ctr = v.CounterAccount != null ? v.CounterAccount.Code : null, v.Description, v.Amount }).ToListAsync();
        return vs.Select(v => Rb.Time(new Row
        {
            ["number"] = v.Number, ["type"] = v.Type.GetDisplayName(), ["purpose"] = v.Purpose.GetDisplayName(), ["cashAccount"] = v.Ca, ["partner"] = v.P, ["counterAccount"] = v.Ctr, ["description"] = v.Description,
            ["receipt"] = v.Type == CashVoucherType.Receipt ? v.Amount : 0m, ["payment"] = v.Type == CashVoucherType.Payment ? v.Amount : 0m,
            ["net"] = v.Type == CashVoucherType.Receipt ? v.Amount : v.Type == CashVoucherType.Payment ? -v.Amount : 0m
        }, "date", v.Date)).ToList();
    }
}

/// <summary>Lệnh sản xuất và giá thành</summary>
public class ProductionDataset : Dataset
{
    public override string Key => "production";
    public override string Title => "Sản xuất (lệnh sản xuất và giá thành)";
    public override string Group => "Sản xuất";
    public override string Description => "Chi phí nguyên vật liệu, nhân công, sản xuất chung và giá thành đơn vị thực tế của từng lệnh sản xuất.";
    public override string Roles => AppRoles.FinanceView + "," + AppRoles.Production + "," + AppRoles.Warehouse;
    public override string DateLabel => "Ngày bắt đầu";
    private static readonly Field[] F =
    {
        new("date", "Ngày bắt đầu", FieldKind.Date), new("completedDate", "Ngày hoàn thành", FieldKind.Date), new("order", "Lệnh sản xuất", FieldKind.Text), new("status", "Trạng thái", FieldKind.Text),
        new("itemCode", "Mã thành phẩm", FieldKind.Text), new("item", "Thành phẩm", FieldKind.Text), new("bom", "Định mức", FieldKind.Text), new("salesOrder", "Đơn bán liên quan", FieldKind.Text),
        new("qtyPlanned", "SL kế hoạch", FieldKind.Qty), new("qtyCompleted", "SL hoàn thành", FieldKind.Qty), new("material", "Chi phí NVL", FieldKind.Money), new("labor", "Chi phí nhân công", FieldKind.Money),
        new("overhead", "Chi phí SXC", FieldKind.Money), new("totalCost", "Tổng giá thành", FieldKind.Money), new("unitCost", "Giá thành đơn vị", FieldKind.Money), new("leadDays", "Số ngày sản xuất", FieldKind.Number),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.ProductionOrders.AsNoTracking().Where(p => p.Status != ProductionStatus.Cancelled);
        if (from.HasValue) q = q.Where(p => p.StartDate >= from.Value);
        if (to.HasValue) q = q.Where(p => p.StartDate <= to.Value);
        var ps = await q.Select(p => new
        {
            p.StartDate, p.CompletedDate, p.Number, p.Status, ItemCode = p.Item!.Code, Item = p.Item.Name, Bom = p.Bom != null ? p.Bom.Code : null, SO = p.SalesOrder != null ? p.SalesOrder.Number : null,
            p.Quantity, p.CompletedQty, p.MaterialCost, p.LaborCost, p.OverheadCost, p.UnitCost
        }).ToListAsync();
        return ps.Select(p => Rb.Time(new Row
        {
            ["completedDate"] = p.CompletedDate?.Date, ["order"] = p.Number, ["status"] = p.Status.GetDisplayName(), ["itemCode"] = p.ItemCode, ["item"] = p.Item, ["bom"] = p.Bom, ["salesOrder"] = p.SO,
            ["qtyPlanned"] = p.Quantity, ["qtyCompleted"] = p.CompletedQty, ["material"] = p.MaterialCost, ["labor"] = p.LaborCost, ["overhead"] = p.OverheadCost,
            ["totalCost"] = p.MaterialCost + p.LaborCost + p.OverheadCost, ["unitCost"] = p.UnitCost, ["leadDays"] = p.CompletedDate.HasValue ? (decimal)(p.CompletedDate.Value.Date - p.StartDate.Date).Days : null
        }, "date", p.StartDate)).ToList();
    }
}

/// <summary>Phiếu kiểm tra chất lượng đã kết luận</summary>
public class QcDataset : Dataset
{
    public override string Key => "qc";
    public override string Title => "Kiểm tra chất lượng (phiếu KCS)";
    public override string Group => "Chất lượng";
    public override string Description => "Phiếu kiểm tra chất lượng đã kết luận: công đoạn, kết quả, số lượng lô / không đạt, nhà cung cấp.";
    public override string Roles => AppRoles.FinanceView + "," + AppRoles.Quality + "," + AppRoles.Warehouse + "," + AppRoles.Production + "," + AppRoles.Purchasing;
    public override string DateLabel => "Ngày kiểm tra";
    private static readonly Field[] F =
    {
        new("date", "Ngày kiểm tra", FieldKind.Date), new("number", "Số phiếu", FieldKind.Text), new("stage", "Công đoạn", FieldKind.Text), new("result", "Kết luận", FieldKind.Text), new("origin", "Nguồn", FieldKind.Text),
        new("originRef", "Chứng từ gốc", FieldKind.Text), new("itemCode", "Mã hàng", FieldKind.Text), new("item", "Tên hàng", FieldKind.Text), new("partner", "Nhà cung cấp / khách hàng", FieldKind.Text),
        new("inspector", "Người kiểm", FieldKind.Text), new("lotQty", "SL lô", FieldKind.Qty), new("acceptedQty", "SL đạt", FieldKind.Qty), new("rejectedQty", "SL không đạt", FieldKind.Qty),
        new("defectRate", "Tỷ lệ lỗi (%)", FieldKind.Percent), new("lots", "Số lô", FieldKind.Number), new("failedLots", "Số lô không đạt", FieldKind.Number),
    };
    public override IReadOnlyList<Field> Fields { get; } = Rb.With(F);

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var q = db.QcInspections.AsNoTracking().Where(i => i.Status == QcStatus.Completed);
        if (from.HasValue) q = q.Where(i => i.Date >= from.Value);
        if (to.HasValue) q = q.Where(i => i.Date <= to.Value);
        var qs = await q.Select(i => new
        {
            i.Date, i.Number, i.Stage, i.Result, i.Origin, i.OriginRef, ItemCode = i.Item!.Code, Item = i.Item.Name, P = i.Partner != null ? i.Partner.Name : null,
            Insp = i.Inspector != null ? i.Inspector.FullName : null, i.LotQty, i.AcceptedQty, i.RejectedQty
        }).ToListAsync();
        return qs.Select(i => Rb.Time(new Row
        {
            ["number"] = i.Number, ["stage"] = i.Stage.GetDisplayName(), ["result"] = i.Result.GetDisplayName(), ["origin"] = i.Origin.GetDisplayName(), ["originRef"] = i.OriginRef, ["itemCode"] = i.ItemCode,
            ["item"] = i.Item, ["partner"] = i.P, ["inspector"] = i.Insp, ["lotQty"] = i.LotQty, ["acceptedQty"] = i.AcceptedQty, ["rejectedQty"] = i.RejectedQty,
            ["defectRate"] = i.LotQty > 0 ? Math.Round(i.RejectedQty * 100m / i.LotQty, 2) : 0m, ["lots"] = 1m, ["failedLots"] = i.Result == QcResult.Fail ? 1m : 0m
        }, "date", i.Date)).ToList();
    }
}

/// <summary>Sổ tài sản cố định</summary>
public class AssetDataset : Dataset
{
    public override string Key => "assets";
    public override string Title => "Tài sản cố định (nguyên giá, hao mòn, giá trị còn lại)";
    public override string Group => "Tài sản";
    public override string Description => "Sổ tài sản cố định hiện tại theo nhóm, bộ phận, tình trạng.";
    public override string Roles => AppRoles.FinanceView;
    public override DateMode DateMode => DateMode.None;
    private static readonly Field[] F =
    {
        new("code", "Mã tài sản", FieldKind.Text), new("name", "Tên tài sản", FieldKind.Text), new("category", "Nhóm tài sản", FieldKind.Text), new("department", "Bộ phận sử dụng", FieldKind.Text),
        new("status", "Trạng thái", FieldKind.Text), new("acquisitionDate", "Ngày mua", FieldKind.Date), new("lifeMonths", "Thời gian KH (tháng)", FieldKind.Number),
        new("cost", "Nguyên giá", FieldKind.Money), new("accumulated", "Hao mòn lũy kế", FieldKind.Money), new("nbv", "Giá trị còn lại", FieldKind.Money), new("monthly", "Khấu hao hằng tháng", FieldKind.Money),
        new("depPct", "Đã khấu hao (%)", FieldKind.Percent),
    };
    public override IReadOnlyList<Field> Fields { get; } = F;

    public override async Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to)
    {
        var assets = await db.FixedAssets.AsNoTracking().Include(a => a.Category).ToListAsync();
        return assets.Select(a => new Row
        {
            ["code"] = a.Code, ["name"] = a.Name, ["category"] = a.Category?.Name, ["department"] = a.Department, ["status"] = a.Status.GetDisplayName(), ["acquisitionDate"] = a.AcquisitionDate.Date,
            ["lifeMonths"] = (decimal)a.UsefulLifeMonths, ["cost"] = a.OriginalCost, ["accumulated"] = a.AccumulatedDepreciation, ["nbv"] = a.NetBookValue,
            ["monthly"] = a.Status == AssetStatus.InUse ? a.MonthlyDepreciation : 0m, ["depPct"] = a.OriginalCost == 0 ? 0m : Math.Round(a.AccumulatedDepreciation * 100m / a.OriginalCost, 2)
        }).ToList();
    }
}

public static class DatasetRegistry
{
    public static readonly IReadOnlyList<Dataset> All = new Dataset[]
    {
        new SalesDataset(), new MarginDataset(), new SalesOrderDataset(), new PurchaseOrderDataset(), new StockDataset(), new StockMoveDataset(),
        new DebtDataset(), new JournalDataset(), new CashDataset(), new ProductionDataset(), new QcDataset(), new AssetDataset()
    };
    public static Dataset? Find(string? key) => All.FirstOrDefault(d => d.Key == key);
    public static bool Allowed(Dataset d, System.Security.Claims.ClaimsPrincipal u) => string.IsNullOrEmpty(d.Roles) || u.InAny(d.Roles);
}
