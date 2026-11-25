namespace ERP.Web.Services.Reporting;

/// <summary>Báo cáo dựng sẵn của hệ thống (mẫu): người dùng mở xem, chỉnh sửa rồi lưu thành báo cáo riêng</summary>
public record ReportTemplate(string Slug, string Title, string Group, string Description, string Icon, ReportDef Def);

public static class ReportTemplates
{
    private static MeasureDef Sum(string f, string? title = null) => new() { Field = f, Agg = Agg.Sum, Title = title };
    private static MeasureDef Cnt(string f = "month", string? title = null) => new() { Field = f, Agg = Agg.Count, Title = title ?? "Số dòng" };
    private static MeasureDef Distinct(string f, string title) => new() { Field = f, Agg = Agg.CountDistinct, Title = title };
    private static MeasureDef Ratio(string a, string b, string title) => new() { Field = a, Field2 = b, Agg = Agg.Ratio, Title = title };
    private static FilterDef Flt(string f, string op, string? v = null) => new() { Field = f, Op = op, Value = v };

    private static ReportDef Grouped(string ds, string preset, string[] rows, MeasureDef[] measures, string? pivot = null, string chart = "none", int? top = null,
        (string Key, bool Desc)[]? sort = null, FilterDef[]? filters = null) => new()
    {
        Dataset = ds, DatePreset = preset, Mode = "grouped", Rows = rows.ToList(), PivotColumn = pivot, Measures = measures.ToList(), Chart = chart, Top = top,
        Sort = (sort ?? Array.Empty<(string, bool)>()).Select(s => new SortDef { Key = s.Key, Desc = s.Desc }).ToList(), Filters = (filters ?? Array.Empty<FilterDef>()).ToList()
    };

    private static ReportDef Detail(string ds, string preset, string[] columns, (string Key, bool Desc)[]? sort = null, FilterDef[]? filters = null) => new()
    {
        Dataset = ds, DatePreset = preset, Mode = "detail", Columns = columns.ToList(),
        Sort = (sort ?? Array.Empty<(string, bool)>()).Select(s => new SortDef { Key = s.Key, Desc = s.Desc }).ToList(), Filters = (filters ?? Array.Empty<FilterDef>()).ToList()
    };

    public static readonly IReadOnlyList<ReportTemplate> All = new List<ReportTemplate>
    {
        // ---------------- Bán hàng
        new("doanh-thu-khach-hang", "Doanh thu theo khách hàng", "Bán hàng", "Doanh thu thuần, thuế và số hóa đơn của từng khách hàng.", "bi-people",
            Grouped("sales", "thisYear", new[] { "customerCode", "customer" }, new[] { Sum("amount", "Doanh thu thuần"), Sum("vat", "Thuế GTGT"), Distinct("invoiceNo", "Số chứng từ") }, chart: "bar")),
        new("doanh-thu-mat-hang", "Doanh thu theo mặt hàng", "Bán hàng", "Số lượng bán và doanh thu thuần theo mặt hàng.", "bi-tags",
            Grouped("sales", "thisYear", new[] { "itemCode", "item" }, new[] { Sum("qty", "Số lượng bán"), Sum("amount", "Doanh thu thuần") }, chart: "bar")),
        new("doanh-thu-theo-thang", "Doanh thu theo tháng", "Bán hàng", "Xu hướng doanh thu thuần 12 tháng gần đây.", "bi-graph-up",
            Grouped("sales", "last365", new[] { "month" }, new[] { Sum("amount", "Doanh thu thuần"), Sum("vat", "Thuế GTGT"), Sum("total", "Tổng thanh toán") }, chart: "line")),
        new("doanh-thu-nhan-vien", "Doanh thu theo nhân viên kinh doanh", "Bán hàng", "Kết quả bán hàng theo người lập đơn bán.", "bi-person-check",
            Grouped("sales", "thisYear", new[] { "salesperson" }, new[] { Sum("amount", "Doanh thu thuần"), Distinct("invoiceNo", "Số chứng từ") }, chart: "bar")),
        new("doanh-thu-khach-thang", "Doanh thu khách hàng × tháng (bảng chéo)", "Bán hàng", "Doanh thu từng khách hàng theo từng tháng trong năm.", "bi-grid-3x3",
            Grouped("sales", "thisYear", new[] { "customer" }, new[] { Sum("amount", "Doanh thu thuần") }, pivot: "month")),
        new("doanh-thu-loai-hang-thang", "Doanh thu loại hàng × tháng (bảng chéo)", "Bán hàng", "Cơ cấu doanh thu theo loại hàng từng tháng.", "bi-grid",
            Grouped("sales", "last365", new[] { "month" }, new[] { Sum("amount", "Doanh thu thuần") }, pivot: "itemType")),
        new("so-chi-tiet-ban-hang", "Sổ chi tiết bán hàng", "Bán hàng", "Từng dòng hóa đơn bán, hàng trả lại và giảm giá.", "bi-journal-text",
            Detail("sales", "thisMonth", new[] { "date", "docType", "invoiceNo", "customer", "itemCode", "item", "qty", "price", "amount", "vat" }, new[] { ("date", true) })),
        // ---------------- Lãi gộp
        new("lai-gop-mat-hang", "Lãi gộp theo mặt hàng", "Bán hàng", "Doanh thu, giá vốn, lãi gộp và tỷ lệ lãi gộp của từng mặt hàng.", "bi-piggy-bank",
            Grouped("sales-margin", "thisYear", new[] { "itemCode", "item" }, new[] { Sum("qty", "Số lượng"), Sum("revenue", "Doanh thu thuần"), Sum("cost", "Giá vốn"), Sum("margin", "Lãi gộp"), Ratio("margin", "revenue", "Tỷ lệ lãi gộp (%)") },
                chart: "bar", sort: new[] { ("m3", true) })),
        new("lai-gop-khach-hang", "Lãi gộp theo khách hàng", "Bán hàng", "Khách hàng mang lại lãi gộp cao / thấp.", "bi-piggy-bank",
            Grouped("sales-margin", "thisYear", new[] { "customer" }, new[] { Sum("revenue", "Doanh thu thuần"), Sum("cost", "Giá vốn"), Sum("margin", "Lãi gộp"), Ratio("margin", "revenue", "Tỷ lệ lãi gộp (%)") },
                chart: "bar", sort: new[] { ("m2", true) })),
        new("lai-gop-theo-thang", "Lãi gộp theo tháng", "Bán hàng", "Doanh thu, giá vốn và lãi gộp theo tháng bán.", "bi-graph-up-arrow",
            Grouped("sales-margin", "last365", new[] { "month" }, new[] { Sum("revenue", "Doanh thu thuần"), Sum("cost", "Giá vốn"), Sum("margin", "Lãi gộp"), Ratio("margin", "revenue", "Tỷ lệ lãi gộp (%)") }, chart: "line")),
        // ---------------- Đơn bán
        new("don-ban-chua-giao", "Đơn bán chưa giao đủ", "Bán hàng", "Các dòng đơn bán còn phải giao, kèm số ngày trễ so với ngày giao dự kiến.", "bi-truck",
            Detail("sales-orders", "all", new[] { "salesOrder", "date", "deliveryDate", "customer", "itemCode", "item", "qtyOrdered", "qtyDelivered", "qtyPending", "pendingAmount", "lateDays" },
                new[] { ("lateDays", true) }, new[] { Flt("qtyPending", "gt", "0") })),
        new("don-ban-theo-trang-thai", "Đơn bán theo trạng thái", "Bán hàng", "Giá trị đơn bán nhóm theo trạng thái xử lý.", "bi-bag-check",
            Grouped("sales-orders", "thisYear", new[] { "status" }, new[] { Distinct("salesOrder", "Số đơn"), Sum("amount", "Giá trị đơn"), Sum("pendingAmount", "Giá trị còn phải giao") }, chart: "pie")),
        // ---------------- Mua hàng
        new("mua-hang-nha-cung-cap", "Mua hàng theo nhà cung cấp", "Mua hàng", "Giá trị nhận hàng, trả lại và nhận thuần theo nhà cung cấp.", "bi-shop",
            Grouped("purchase", "thisYear", new[] { "vendorCode", "vendor" }, new[] { Distinct("po", "Số đơn mua"), Sum("receivedAmount", "Giá trị đã nhận"), Sum("returnedAmount", "Giá trị đã trả NCC"), Sum("netReceivedAmount", "Giá trị nhận thuần") }, chart: "bar", sort: new[] { ("m3", true) })),
        new("mua-hang-mat-hang", "Mua hàng theo mặt hàng", "Mua hàng", "Số lượng và giá trị nhận thuần theo mặt hàng.", "bi-box-arrow-in-down",
            Grouped("purchase", "thisYear", new[] { "itemCode", "item" }, new[] { Sum("qtyReceived", "SL đã nhận"), Sum("qtyReturned", "SL đã trả NCC"), Sum("netReceivedAmount", "Giá trị nhận thuần") }, chart: "bar", sort: new[] { ("m2", true) })),
        new("mua-hang-theo-thang", "Mua hàng theo tháng", "Mua hàng", "Giá trị nhận hàng thuần 12 tháng gần đây.", "bi-graph-up",
            Grouped("purchase", "last365", new[] { "month" }, new[] { Sum("netReceivedAmount", "Giá trị nhận thuần") }, chart: "line")),
        new("don-mua-chua-nhan", "Đơn mua chưa nhận đủ", "Mua hàng", "Các dòng đơn mua còn phải nhận, kèm số ngày trễ so với ngày giao dự kiến.", "bi-hourglass-split",
            Detail("purchase", "all", new[] { "po", "date", "expectedDate", "vendor", "itemCode", "item", "qtyOrdered", "qtyReceived", "qtyPending", "lateDays" }, new[] { ("lateDays", true) }, new[] { Flt("qtyPending", "gt", "0") })),
        // ---------------- Kho
        new("ton-kho-theo-kho", "Giá trị tồn kho theo kho × loại hàng", "Kho", "Giá trị tồn của từng kho theo loại hàng (khớp TK 152 / 155 / 156).", "bi-boxes",
            Grouped("stock", "all", new[] { "warehouse" }, new[] { Sum("value", "Giá trị tồn") }, pivot: "itemType")),
        new("ton-kho-theo-loai", "Cơ cấu giá trị tồn theo loại hàng", "Kho", "Tỷ trọng giá trị tồn kho theo loại hàng.", "bi-pie-chart",
            Grouped("stock", "all", new[] { "itemType" }, new[] { Sum("value", "Giá trị tồn") }, chart: "pie")),
        new("hang-duoi-ton-toi-thieu", "Hàng dưới tồn tối thiểu", "Kho", "Mặt hàng có tổng tồn thấp hơn mức tồn tối thiểu cần bổ sung.", "bi-exclamation-triangle",
            Detail("stock", "all", new[] { "itemCode", "item", "warehouse", "qty", "itemTotalQty", "minStock", "unit" }, new[] { ("itemCode", false) }, new[] { Flt("belowMin", "eq", "Có") })),
        new("nhap-xuat-mat-hang", "Nhập – xuất theo mặt hàng", "Kho", "Số lượng và giá trị nhập, xuất của từng mặt hàng trong kỳ.", "bi-arrow-left-right",
            Grouped("stock-moves", "thisMonth", new[] { "itemCode", "item" }, new[] { Sum("qtyIn", "SL nhập"), Sum("qtyOut", "SL xuất"), Sum("amountIn", "Giá trị nhập"), Sum("amountOut", "Giá trị xuất") })),
        new("nhap-xuat-theo-thang", "Giá trị nhập – xuất kho theo tháng", "Kho", "Biến động giá trị nhập, xuất kho theo tháng.", "bi-bar-chart",
            Grouped("stock-moves", "last365", new[] { "month" }, new[] { Sum("amountIn", "Giá trị nhập"), Sum("amountOut", "Giá trị xuất"), Sum("netAmount", "Chênh lệch") }, chart: "bar")),
        // ---------------- Công nợ & tiền
        new("cong-no-tong-hop", "Tổng hợp công nợ phải thu / phải trả", "Công nợ & tiền", "Dư nợ và nợ quá hạn theo loại công nợ.", "bi-arrow-down-up",
            Grouped("debts", "all", new[] { "direction" }, new[] { Sum("outstanding", "Còn nợ"), Sum("overdueAmount", "Nợ quá hạn"), Distinct("invoiceNo", "Số hóa đơn") }, chart: "pie")),
        new("cong-no-qua-han", "Công nợ quá hạn theo đối tượng", "Công nợ & tiền", "Đối tượng có nợ quá hạn, xếp theo số tiền.", "bi-alarm",
            Grouped("debts", "all", new[] { "direction", "partner" }, new[] { Sum("overdueAmount", "Nợ quá hạn"), Sum("outstanding", "Tổng còn nợ"), Distinct("invoiceNo", "Số hóa đơn") },
                sort: new[] { ("m0", true) }, filters: new[] { Flt("overdueDays", "gt", "0") })),
        new("tuoi-no-doi-tuong", "Tuổi nợ theo đối tượng (bảng chéo)", "Công nợ & tiền", "Dư nợ từng đối tượng chia theo nhóm tuổi nợ.", "bi-hourglass",
            Grouped("debts", "all", new[] { "direction", "partner" }, new[] { Sum("outstanding", "Còn nợ") }, pivot: "bucket")),
        new("thu-chi-theo-thang", "Thu chi tiền theo tháng", "Công nợ & tiền", "Tổng thu, chi và chênh lệch theo tháng.", "bi-cash-stack",
            Grouped("cash", "last365", new[] { "month" }, new[] { Sum("receipt", "Thu"), Sum("payment", "Chi"), Sum("net", "Thu − Chi") }, chart: "bar")),
        new("thu-chi-doi-tuong", "Thu chi tiền theo đối tượng", "Công nợ & tiền", "Tổng thu, chi theo khách hàng / nhà cung cấp.", "bi-cash-coin",
            Grouped("cash", "thisYear", new[] { "partner" }, new[] { Sum("receipt", "Thu"), Sum("payment", "Chi") }, sort: new[] { ("m0", true) })),
        // ---------------- Sản xuất
        new("gia-thanh-lenh-san-xuat", "Giá thành theo lệnh sản xuất", "Sản xuất", "Chi phí NVL, nhân công, SXC và giá thành đơn vị từng lệnh đã hoàn thành.", "bi-gear-wide-connected",
            Detail("production", "thisYear", new[] { "order", "date", "completedDate", "item", "qtyCompleted", "material", "labor", "overhead", "totalCost", "unitCost", "leadDays" }, new[] { ("date", true) }, new[] { Flt("status", "eq", "Hoàn thành") })),
        new("chi-phi-san-xuat-thanh-pham", "Chi phí sản xuất theo thành phẩm", "Sản xuất", "Tổng hợp chi phí và giá thành bình quân theo thành phẩm.", "bi-diagram-3",
            Grouped("production", "thisYear", new[] { "itemCode", "item" }, new[] { Sum("qtyCompleted", "SL hoàn thành"), Sum("material", "Chi phí NVL"), Sum("labor", "Chi phí nhân công"), Sum("overhead", "Chi phí SXC"), Sum("totalCost", "Tổng giá thành") },
                filters: new[] { Flt("status", "eq", "Hoàn thành") })),
        // ---------------- Chất lượng
        new("loi-nha-cung-cap", "Tỷ lệ lỗi nhà cung cấp (kiểm tra đầu vào)", "Chất lượng", "Số lô, lô không đạt và tỷ lệ lỗi theo số lượng của từng nhà cung cấp.", "bi-patch-exclamation",
            Grouped("qc", "thisYear", new[] { "partner" }, new[] { Sum("lots", "Số lô"), Sum("failedLots", "Lô không đạt"), Sum("lotQty", "SL kiểm tra"), Sum("rejectedQty", "SL không đạt"), Ratio("rejectedQty", "lotQty", "Tỷ lệ lỗi (%)") },
                chart: "bar", sort: new[] { ("m4", true) }, filters: new[] { Flt("stage", "contains", "Đầu vào") })),
        new("ket-qua-kcs", "Kết quả kiểm tra theo công đoạn (bảng chéo)", "Chất lượng", "Số lô kiểm tra theo công đoạn và kết luận.", "bi-clipboard-check",
            Grouped("qc", "thisYear", new[] { "stage" }, new[] { Sum("lots", "Số lô") }, pivot: "result")),
        // ---------------- Tài sản
        new("tscd-theo-nhom", "Tài sản cố định theo nhóm", "Tài sản", "Nguyên giá, hao mòn và giá trị còn lại theo nhóm tài sản.", "bi-building",
            Grouped("assets", "all", new[] { "category" }, new[] { Distinct("code", "Số tài sản"), Sum("cost", "Nguyên giá"), Sum("accumulated", "Hao mòn lũy kế"), Sum("nbv", "Giá trị còn lại") }, chart: "pie")),
        new("tscd-chi-tiet", "Sổ tài sản cố định chi tiết", "Tài sản", "Danh sách tài sản cùng nguyên giá, hao mòn, giá trị còn lại.", "bi-card-list",
            Detail("assets", "all", new[] { "code", "name", "category", "department", "acquisitionDate", "cost", "accumulated", "nbv", "monthly", "status" }, new[] { ("code", false) })),
        // ---------------- Kế toán
        new("phat-sinh-tai-khoan", "Phát sinh theo tài khoản", "Kế toán", "Tổng phát sinh Nợ, Có của từng tài khoản trong kỳ.", "bi-list-columns-reverse",
            Grouped("journal", "thisMonth", new[] { "accountCode", "account" }, new[] { Sum("debit", "Phát sinh Nợ"), Sum("credit", "Phát sinh Có"), Sum("net", "Nợ − Có") }, sort: new[] { ("accountCode", false) })),
        new("chi-phi-theo-thang-tai-khoan", "Chi phí theo tài khoản × tháng (bảng chéo)", "Kế toán", "Phát sinh chi phí (TK 6, 8) từng tài khoản theo tháng.", "bi-receipt",
            Grouped("journal", "thisYear", new[] { "accountCode", "account" }, new[] { Sum("net", "Phát sinh Nợ − Có") }, pivot: "month", sort: new[] { ("accountCode", false) }, filters: new[] { Flt("accountGroup", "in", "6,8") })),
    };

    public static ReportTemplate? Find(string? slug) => All.FirstOrDefault(t => t.Slug == slug);
}
