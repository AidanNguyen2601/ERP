using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Một liên kết trong bước kịch bản: chứng từ mẫu (đậm) hoặc lối tắt thao tác (nét đứt)</summary>
public record ScChip(string Text, string? Sub, string Url, bool IsAction = false);
/// <summary>Một bước: ai làm (vai trò → tài khoản nên đăng nhập), làm gì, mở chứng từ nào</summary>
public record ScStep(string Title, string Desc, AppRoleRef[] Roles, List<ScChip> Chips);
public record AppRoleRef(string Role, string? Username, string Label);
public record ScFlow(string Key, string Title, string Icon, string Intro, List<ScStep> Steps);
public record DemoAccount(string Username, string FullName, string Role, string Department);

/// <summary>Kịch bản nghiệp vụ: các luồng chính từng bước, gắn với chứng từ mẫu đang có trong hệ thống</summary>
public class ScenariosController : ErpControllerBase
{
    public const string SamplePassword = "Abc@123";
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly ErpSettings _cfg;
    private Dictionary<string, string> _userByRole = new();

    public ScenariosController(ErpDbContext db, IPasswordHasher<AppUser> hasher, IOptions<ErpSettings> cfg) : base(db)
    { _hasher = hasher; _cfg = cfg.Value; }

    public async Task<IActionResult> Index()
    {
        var users = await Db.Users.Where(u => u.IsActive).OrderBy(u => u.Id).ToListAsync();
        _userByRole = users.GroupBy(u => u.Role).ToDictionary(g => g.Key, g => g.First().Username);
        var flows = new List<ScFlow>
        {
            await PurchaseAsync(), await SalesAsync(), await ProductionAsync(), await QualityAsync(),
            await InventoryAsync(), await AssetsAsync(), await AccountingAsync(), await CashAsync(), CustomizeFlow()
        };
        ViewBag.Flows = flows;
        // Chỉ liệt kê tài khoản còn dùng mật khẩu mẫu (đã đổi mật khẩu thì không hiển thị)
        ViewBag.Demo = users.Where(u => _hasher.VerifyHashedPassword(u, u.PasswordHash, SamplePassword) != PasswordVerificationResult.Failed)
            .Select(u => new DemoAccount(u.Username, u.FullName, u.Role, u.Department)).ToList();
        ViewBag.SamplePassword = SamplePassword;
        return View();
    }

    // ------------------------------------------------------------------ Tiện ích dựng bước
    private AppRoleRef[] R(params string[] roles) => roles.Select(r => new AppRoleRef(r, _userByRole.GetValueOrDefault(r), AppRoles.DisplayName(r))).ToArray();
    private static ScChip A(string text, string? url) => new(text, null, url ?? "#", true);
    private string U(string action, string controller, object? route = null) => Url.Action(action, controller, route) ?? "#";
    private static List<ScChip> Chips(params IEnumerable<ScChip>[] groups) => groups.SelectMany(g => g).ToList();
    private ScChip Doc(string action, string controller, int id, string number, string? sub) => new(number, sub, U(action, controller, new { id }));

    private async Task<List<ScChip>> PosAsync(params PoStatus[] st) =>
        (await Db.PurchaseOrders.Include(p => p.Vendor).Where(p => st.Contains(p.Status)).OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "PurchaseOrders", p.Id, p.Number, p.Vendor?.Name)).ToList();
    private async Task<List<ScChip>> SosAsync(params SoStatus[] st) =>
        (await Db.SalesOrders.Include(p => p.Customer).Where(p => st.Contains(p.Status)).OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "SalesOrders", p.Id, p.Number, p.Customer?.Name)).ToList();
    private async Task<List<ScChip>> MosAsync(params ProductionStatus[] st) =>
        (await Db.ProductionOrders.Include(p => p.Item).Where(p => st.Contains(p.Status)).OrderByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "Production", p.Id, p.Number, p.Item?.Name)).ToList();
    private async Task<List<ScChip>> InvoicesAsync(InvoiceDirection dir, params InvoiceStatus[] st) =>
        (await Db.Invoices.Include(p => p.Partner).Where(p => p.Direction == dir && st.Contains(p.Status)).OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "Invoices", p.Id, p.InvoiceNo ?? p.Number, p.Partner?.Name)).ToList();
    private async Task<List<ScChip>> OpenInvoicesAsync(InvoiceDirection dir) =>
        (await Db.Invoices.Include(p => p.Partner).Where(p => p.Direction == dir && p.Status == InvoiceStatus.Issued && p.Total > p.PaidAmount)
            .OrderBy(p => p.DueDate).Take(3).ToListAsync())
        .Select(p => Doc("Details", "Invoices", p.Id, p.InvoiceNo ?? p.Number, $"{p.Partner?.Name} · còn nợ {p.Outstanding.ToShortMoney()}")).ToList();
    private async Task<List<ScChip>> InvDocsAsync(InvDocType type, params DocOrigin[] origins) =>
        (await Db.InventoryDocs.Include(p => p.Warehouse).Where(p => p.Type == type && (origins.Length == 0 || origins.Contains(p.Origin)))
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "Inventory", p.Id, p.Number, p.Warehouse?.Name)).ToList();
    private async Task<List<ScChip>> InspectionsAsync(QcStage? stage, QcStatus status, params QcResult[] results) =>
        (await Db.QcInspections.Include(p => p.Item).Where(p => p.Status == status && (stage == null || p.Stage == stage) && (results.Length == 0 || results.Contains(p.Result)))
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "QcInspections", p.Id, p.Number, p.Item?.Name)).ToList();
    private async Task<List<ScChip>> NcrsAsync(NcrStatus status, params NcrDisposition[] disp) =>
        (await Db.QcNonconformances.Include(p => p.Item).Where(p => p.Status == status && (disp.Length == 0 || disp.Contains(p.Disposition)))
            .OrderBy(p => p.DueDate).Take(3).ToListAsync())
        .Select(p => Doc("Details", "QcNcrs", p.Id, p.Number, $"{p.Item?.Name} × {p.Quantity.ToQty()}")).ToList();
    private async Task<List<ScChip>> VouchersAsync(CashVoucherType type, CashPurpose? purpose = null) =>
        (await Db.CashVouchers.Include(p => p.Partner).Where(p => p.Type == type && p.Status == DocStatus.Posted && (purpose == null || p.Purpose == purpose)).OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(3).ToListAsync())
        .Select(p => Doc("Details", "Cash", p.Id, p.Number, p.Partner?.Name ?? p.Description)).ToList();

    // ------------------------------------------------------------------ 1. Mua hàng
    private async Task<ScFlow> PurchaseAsync() => new("purchase", "1. Mua hàng → nhập kho → hóa đơn → thanh toán", "bi-cart-plus",
        $"Đơn mua từ {_cfg.PoApprovalThreshold.ToMoney()} đ trở lên phải Ban Giám đốc duyệt, dưới ngưỡng tự duyệt. Hàng về kho được kiểm tra chất lượng đầu vào, sau đó lập hóa đơn mua theo số lượng đã nhận và thanh toán nhà cung cấp.",
        new List<ScStep>
        {
            new("Lập đơn mua", "Chọn nhà cung cấp, kho nhận, dòng hàng, đơn giá, VAT; gửi duyệt. Có thể tạo nhanh từ Hoạch định vật tư (MRP) khi thiếu NVL.", R(AppRoles.Purchasing),
                Chips(await PosAsync(PoStatus.Draft), new[] { A("Tạo đơn mua mới", U("Create", "PurchaseOrders")), A("Nhà cung cấp", U("Index", "Partners", new { type = "vendor" })), A("Hoạch định vật tư", U("Mrp", "Production")) })),
            new("Duyệt đơn vượt ngưỡng", "Ban Giám đốc xem đơn chờ duyệt và duyệt hoặc trả lại; người duyệt được ghi nhận trên đơn.", R(AppRoles.Director),
                Chips(await PosAsync(PoStatus.PendingApproval), new[] { A("Đơn chờ duyệt", U("Index", "PurchaseOrders", new { status = "PendingApproval" })) })),
            new("Nhận hàng & kiểm tra đầu vào", "Thủ kho nhận hàng nhiều đợt → tự lập phiếu nhập kho, hạch toán Nợ 15x / Có 331, cập nhật giá vốn bình quân. QC lập phiếu kiểm tra đầu vào theo kế hoạch của mặt hàng.", R(AppRoles.Warehouse, AppRoles.Quality),
                Chips(await PosAsync(PoStatus.Approved, PoStatus.PartiallyReceived), await InspectionsAsync(QcStage.Incoming, QcStatus.Draft), new[] { A("Phiếu kiểm tra đầu vào", U("Index", "QcInspections", new { stage = "Incoming" })) })),
            new("Hóa đơn mua", "Kế toán lập hóa đơn mua từ phần đã nhận; hệ thống chặn trùng số hóa đơn của cùng nhà cung cấp. Hạch toán Nợ 1331 / Có 331.", R(AppRoles.Accountant),
                Chips(await PosAsync(PoStatus.Received), await InvoicesAsync(InvoiceDirection.Purchase, InvoiceStatus.Draft), new[] { A("Hóa đơn mua vào", U("Index", "Invoices", new { direction = "Purchase" })) })),
            new("Thanh toán nhà cung cấp", "Lập phiếu chi: hệ thống tự cấn trừ công nợ theo hóa đơn (FIFO theo hạn), chặn chi vượt số dư quỹ.", R(AppRoles.Accountant),
                Chips(await OpenInvoicesAsync(InvoiceDirection.Purchase), await VouchersAsync(CashVoucherType.Payment, CashPurpose.VendorPayment), new[] { A("Công nợ phải trả", U("Index", "Debts", new { kind = "ap" })), A("Lập phiếu chi", U("Create", "Cash", new { type = "Payment" })) })),
        });

    // ------------------------------------------------------------------ 2. Bán hàng
    private async Task<ScFlow> SalesAsync() => new("sales", "2. Bán hàng → giao hàng → hóa đơn → thu tiền", "bi-bag-check",
        "Đơn bán kiểm tra hạn mức công nợ khi xác nhận (vượt hạn mức cần Ban Giám đốc duyệt), giao hàng nhiều đợt, hạch toán giá vốn khi xuất kho và doanh thu khi phát hành hóa đơn.",
        new List<ScStep>
        {
            new("Lập đơn bán", "Chọn khách hàng, kho xuất, mặt hàng, chiết khấu, VAT từng dòng.", R(AppRoles.Sales),
                Chips(await SosAsync(SoStatus.Draft), new[] { A("Tạo đơn bán mới", U("Create", "SalesOrders")), A("Khách hàng", U("Index", "Partners", new { type = "customer" })) })),
            new("Xác nhận & kiểm tra hạn mức công nợ", "Dư nợ + đơn đang mở + đơn này vượt hạn mức thì chỉ Ban Giám đốc xác nhận được; người duyệt vượt hạn mức được ghi trên đơn.", R(AppRoles.Sales, AppRoles.Director),
                Chips(await SosAsync(SoStatus.Draft), new[] { A("Danh sách đơn bán", U("Index", "SalesOrders")) })),
            new("Kiểm tra trước giao & xuất kho", "Thủ kho giao hàng theo từng đợt → phiếu xuất kho, hạch toán Nợ 632 / Có 15x theo giá bình quân; QC kiểm tra lô trước khi giao.", R(AppRoles.Warehouse, AppRoles.Quality),
                Chips(await SosAsync(SoStatus.Confirmed, SoStatus.PartiallyDelivered), await InvDocsAsync(InvDocType.Issue, DocOrigin.Sales), new[] { A("Kiểm tra trước giao", U("Index", "QcInspections", new { stage = "Outgoing" })) })),
            new("Hóa đơn bán", "Kế toán lập hóa đơn cho phần đã giao, phát hành tự cấp số theo ký hiệu. Hạch toán Nợ 131 / Có 511, 33311.", R(AppRoles.Accountant),
                Chips(await SosAsync(SoStatus.Delivered), await InvoicesAsync(InvoiceDirection.Sales, InvoiceStatus.Draft), new[] { A("Hóa đơn bán ra", U("Index", "Invoices", new { direction = "Sales" })) })),
            new("Thu tiền & theo dõi công nợ", "Lập phiếu thu, tự cấn trừ hóa đơn; theo dõi tuổi nợ phải thu.", R(AppRoles.Accountant),
                Chips(await OpenInvoicesAsync(InvoiceDirection.Sales), await VouchersAsync(CashVoucherType.Receipt, CashPurpose.CustomerReceipt), new[] { A("Công nợ phải thu", U("Index", "Debts", new { kind = "ar" })), A("Tuổi nợ", U("Aging", "Debts", new { kind = "ar" })) })),
        });

    // ------------------------------------------------------------------ 3. Sản xuất
    private async Task<ScFlow> ProductionAsync() => new("production", "3. Định mức → lệnh sản xuất → giá thành", "bi-gear-wide-connected",
        "Từ định mức NVL (BOM) lập lệnh sản xuất, xuất nguyên vật liệu, hoàn thành nhập kho thành phẩm theo giá thành thực tế (NVL + nhân công + sản xuất chung).",
        new List<ScStep>
        {
            new("Định mức nguyên vật liệu (BOM)", "Khai báo NVL, % hao hụt, nhân công và chi phí SXC định mức; hệ thống tính giá thành định mức và biên lãi gộp.", R(AppRoles.Production),
                Chips((await Db.Boms.Include(b => b.FinishedItem).Where(b => b.IsActive).OrderBy(b => b.Code).Take(3).ToListAsync()).Select(b => Doc("Details", "Boms", b.Id, b.Code, b.FinishedItem?.Name)), new[] { A("Danh sách định mức", U("Index", "Boms")) })),
            new("Hoạch định vật tư (MRP)", "Nhu cầu NVL − tồn kho − hàng đang mua = số cần mua; tạo đơn mua nháp một chạm.", R(AppRoles.Production, AppRoles.Purchasing),
                Chips(new[] { A("Hoạch định vật tư", U("Mrp", "Production")) })),
            new("Lập & phát lệnh sản xuất", "Chọn định mức, số lượng, kho NVL / kho thành phẩm; phát lệnh để cho phép xuất vật tư.", R(AppRoles.Production),
                Chips(await MosAsync(ProductionStatus.Draft, ProductionStatus.Released), new[] { A("Lập lệnh sản xuất", U("Create", "Production")) })),
            new("Xuất nguyên vật liệu & kiểm tra công đoạn", "Thủ kho xuất đủ NVL theo định mức (Nợ 154 / Có 152). QC kiểm tra giữa quy trình, ví dụ sau công đoạn sơn.", R(AppRoles.Warehouse, AppRoles.Quality),
                Chips(await MosAsync(ProductionStatus.InProgress), await InspectionsAsync(QcStage.InProcess, QcStatus.Completed))),
            new("Hoàn thành & kiểm tra thành phẩm", "Nhập số lượng hoàn thành, nhân công, SXC thực tế → kết chuyển vào 154, nhập kho thành phẩm Nợ 155 / Có 154, tính giá thành đơn vị.", R(AppRoles.Production, AppRoles.Quality),
                Chips(await MosAsync(ProductionStatus.Completed), await InspectionsAsync(QcStage.Final, QcStatus.Completed))),
        });

    // ------------------------------------------------------------------ 4. Chất lượng
    private async Task<ScFlow> QualityAsync() => new("quality", "4. Kiểm tra chất lượng → NCR → xử lý hàng lỗi", "bi-patch-check",
        "Mỗi công đoạn (đầu vào, trong sản xuất, thành phẩm, trước giao) có bộ tiêu chí riêng. Số lượng không đạt tự sinh báo cáo không phù hợp (NCR); hủy bỏ hoặc trả nhà cung cấp tự lập phiếu xuất kho và bút toán theo cấu hình.",
        new List<ScStep>
        {
            new("Khai báo kế hoạch & tiêu chí", "Tiêu chí đạt / không đạt hoặc đo lường min – max, đánh dấu trọng yếu; áp dụng theo mặt hàng, loại hàng hoặc chung.", R(AppRoles.Quality),
                Chips((await Db.QcPlans.Where(p => p.IsActive).OrderBy(p => p.Stage).ThenBy(p => p.Code).Take(3).ToListAsync()).Select(p => Doc("Details", "QcPlans", p.Id, p.Code, p.Name)), new[] { A("Kế hoạch kiểm tra", U("Index", "QcPlans")) })),
            new("Lập phiếu kiểm tra (KCS)", "Lập từ đơn mua, lệnh sản xuất, đơn bán hoặc thủ công; hệ thống chọn kế hoạch phù hợp, tự đánh giá giá trị đo và gợi ý kết luận.", R(AppRoles.Quality, AppRoles.Warehouse, AppRoles.Production),
                Chips(await InspectionsAsync(null, QcStatus.Draft), new[] { A("Phiếu kiểm tra", U("Index", "QcInspections")) })),
            new("Kết luận & tự sinh NCR", "Đạt / đạt có điều kiện / không đạt; số lượng lỗi tự lập NCR kèm mô tả tiêu chí không đạt.", R(AppRoles.Quality),
                Chips(await InspectionsAsync(null, QcStatus.Completed, QcResult.Fail, QcResult.Conditional))),
            new("Xử lý NCR", "Chọn hướng: trả nhà cung cấp, sửa chữa, hủy bỏ, chấp nhận nhân nhượng. Hủy / trả NCC lập phiếu xuất kho và bút toán; đóng NCR khi đã có hành động khắc phục.", R(AppRoles.Quality, AppRoles.Warehouse),
                Chips(await NcrsAsync(NcrStatus.Open), await NcrsAsync(NcrStatus.Closed), new[] { A("Danh sách NCR", U("Index", "QcNcrs")) })),
            new("Báo cáo chất lượng & đánh giá nhà cung cấp", "Tỷ lệ lô đạt, tỷ lệ lỗi theo tháng / công đoạn / mặt hàng; xếp loại nhà cung cấp A / B / C; tiêu chí hay lỗi nhất.", R(AppRoles.Quality, AppRoles.Director),
                Chips(new[] { A("Báo cáo chất lượng", U("Index", "QcReports")) })),
        });

    // ------------------------------------------------------------------ 5. Kho
    private async Task<ScFlow> InventoryAsync() => new("inventory", "5. Quản lý kho → kiểm kê → giá vốn", "bi-box-seam",
        "Tồn kho theo từng kho, giá vốn bình quân gia quyền di động, thẻ kho cho từng mặt hàng; kiểm kê điều chỉnh chênh lệch về tài khoản chờ xử lý.",
        new List<ScStep>
        {
            new("Theo dõi tồn kho & cảnh báo tồn tối thiểu", "Tồn theo kho, hàng đang về, đã cam kết giao, khả dụng; lọc mặt hàng dưới mức tối thiểu.", R(AppRoles.Warehouse),
                Chips(new[] { A("Tồn kho", U("Index", "Inventory")), A("Dưới tồn tối thiểu", U("Index", "Inventory", new { low = true })), A("Hàng hóa, vật tư", U("Index", "Items")) })),
            new("Nhập / xuất kho thủ công", "Chọn tài khoản đối ứng khi nhập / xuất ngoài nghiệp vụ mua bán, sản xuất.", R(AppRoles.Warehouse),
                Chips(await InvDocsAsync(InvDocType.Receipt, DocOrigin.Manual), await InvDocsAsync(InvDocType.Issue, DocOrigin.Manual), new[] { A("Lập phiếu nhập", U("Create", "Inventory", new { type = "Receipt" })) })),
            new("Chuyển kho", "Chuyển giữa các kho, không phát sinh bút toán, giữ nguyên giá vốn.", R(AppRoles.Warehouse),
                Chips(await InvDocsAsync(InvDocType.Transfer), new[] { A("Lập phiếu chuyển kho", U("Create", "Inventory", new { type = "Transfer" })) })),
            new("Kiểm kê kho", "Nhập số lượng thực tế; thừa Nợ 15x / Có 3381, thiếu Nợ 1381 / Có 15x theo giá vốn bình quân.", R(AppRoles.Warehouse, AppRoles.Accountant),
                Chips(await InvDocsAsync(InvDocType.Adjustment), new[] { A("Lập phiếu kiểm kê", U("Create", "Inventory", new { type = "Adjustment" })) })),
            new("Thẻ kho", "Xem nhập – xuất – tồn và giá vốn từng lần của mặt hàng.", R(AppRoles.Warehouse, AppRoles.Accountant),
                Chips((await Db.Items.Where(i => i.Type != ItemType.Service && i.IsActive).OrderBy(i => i.Code).Take(3).ToListAsync()).Select(i => new ScChip(i.Code, i.Name, U("StockCard", "Inventory", new { itemId = i.Id }))))),
        });

    // ------------------------------------------------------------------ 6. Tài sản cố định
    private async Task<ScFlow> AssetsAsync() => new("assets", "6. Tài sản cố định → khấu hao → thanh lý", "bi-building",
        "Ghi tăng tài sản theo nhóm (tài khoản nguyên giá, hao mòn, chi phí lấy từ nhóm), khấu hao đường thẳng chạy một lần mỗi tháng, thanh lý ghi nhận lãi lỗ.",
        new List<ScStep>
        {
            new("Khai báo nhóm tài sản", "Tài khoản nguyên giá, hao mòn, chi phí khấu hao và thời gian khấu hao mặc định cho từng nhóm.", R(AppRoles.Accountant),
                Chips(new[] { A("Nhóm tài sản cố định", U("Index", "AssetCategories")) })),
            new("Ghi tăng tài sản", "Ghi tăng có hạch toán (Nợ 211 / Có 331 hoặc tiền) hoặc nhập tài sản đầu kỳ không hạch toán.", R(AppRoles.Accountant),
                Chips((await Db.FixedAssets.Where(a => a.Status == AssetStatus.InUse).OrderByDescending(a => a.Id).Take(3).ToListAsync()).Select(a => Doc("Details", "Assets", a.Id, a.Code, a.Name)), new[] { A("Ghi tăng tài sản", U("Create", "Assets")) })),
            new("Khấu hao hằng tháng", "Chạy khấu hao một lần mỗi tháng: Nợ 642 / 641 / Có 2141; xem lịch sử các kỳ đã chạy.", R(AppRoles.Accountant),
                Chips(new[] { A("Sổ tài sản & khấu hao", U("Index", "Assets")) })),
            new("Thanh lý / nhượng bán", "Nợ 2141 hao mòn, Nợ 811 giá trị còn lại / Có 211; tiền thu thanh lý Nợ tiền / Có 711.", R(AppRoles.Accountant),
                Chips((await Db.FixedAssets.Where(a => a.Status == AssetStatus.Disposed).OrderByDescending(a => a.Id).Take(3).ToListAsync()).Select(a => Doc("Details", "Assets", a.Id, a.Code, a.Name)), new[] { A("Cấu hình bút toán thanh lý", U("Index", "PostingRules", new { module = "Tài sản" })) })),
        });

    // ------------------------------------------------------------------ 7. Kế toán tổng hợp
    private async Task<ScFlow> AccountingAsync() => new("accounting", "7. Kế toán tổng hợp → báo cáo → khóa sổ", "bi-journal-bookmark",
        "Mọi nghiệp vụ tự sinh bút toán vào sổ cái nên kho, công nợ và báo cáo luôn khớp. Tài khoản của từng giao dịch tự động có thể cấu hình; cuối kỳ kết chuyển và khóa sổ.",
        new List<ScStep>
        {
            new("Cấu hình bút toán theo giao dịch", "Chọn tài khoản hạch toán cho từng dòng định khoản của hóa đơn, nhập / xuất kho, sản xuất, xử lý hàng lỗi...; chỉ áp dụng cho chứng từ ghi sổ sau khi lưu.", R(AppRoles.Accountant),
                Chips(new[] { A("Cấu hình bút toán", U("Index", "PostingRules")), A("Hệ thống tài khoản", U("Index", "Accounts")) })),
            new("Chứng từ kế toán thủ công", "Lập bút toán nháp, kiểm tra cân Nợ / Có, ghi sổ; bút toán đảo khi cần hủy.", R(AppRoles.Accountant),
                Chips((await Db.JournalEntries.Where(j => j.Source == JournalSource.Manual).OrderByDescending(j => j.Date).ThenByDescending(j => j.Id).Take(3).ToListAsync()).Select(j => Doc("Details", "Journals", j.Id, j.Number, j.Description)),
                    new[] { A("Lập bút toán", U("Create", "Journals")), A("Mọi bút toán", U("Index", "Journals")) })),
            new("Báo cáo tài chính", "Bảng cân đối số phát sinh, sổ cái chi tiết, báo cáo kết quả kinh doanh và bảng cân đối kế toán.", R(AppRoles.Accountant, AppRoles.Director),
                Chips(new[] { A("Bảng cân đối phát sinh", U("TrialBalance", "Reports")), A("Kết quả kinh doanh", U("IncomeStatement", "Reports")), A("Cân đối kế toán", U("BalanceSheet", "Reports")), A("Sổ cái", U("Ledger", "Reports")) })),
            new("Kết chuyển & khóa sổ", "Kết chuyển 5xx–8xx sang 911 rồi 4212 và khóa kỳ; kỳ đã khóa chặn mọi chứng từ trong kỳ, chỉ Ban Giám đốc mở khóa.", R(AppRoles.Accountant, AppRoles.Director),
                Chips(new[] { A("Khóa sổ & kết chuyển", U("Periods", "Reports")) })),
        });

    // ------------------------------------------------------------------ 8. Dòng tiền
    private async Task<ScFlow> CashAsync() => new("cash", "8. Dòng tiền & công nợ", "bi-wallet2",
        "Quỹ tiền mặt và tài khoản ngân hàng, phiếu thu / chi / chuyển nội bộ, sổ quỹ, báo cáo dòng tiền và dự báo 8 tuần từ công nợ đến hạn.",
        new List<ScStep>
        {
            new("Phiếu thu / chi / chuyển nội bộ", "Thu khách hàng, trả nhà cung cấp (tự cấn trừ hóa đơn), thu chi khác chọn TK đối ứng, chuyển giữa các quỹ.", R(AppRoles.Accountant),
                Chips(await VouchersAsync(CashVoucherType.Receipt), await VouchersAsync(CashVoucherType.Transfer), new[] { A("Quỹ & ngân hàng", U("Index", "Cash")) })),
            new("Sổ quỹ & sổ tiền gửi", "Đối chiếu số dư đầu kỳ, phát sinh, cuối kỳ của từng quỹ / tài khoản.", R(AppRoles.Accountant),
                Chips((await Db.CashAccounts.Where(c => c.IsActive).OrderBy(c => c.Code).Take(3).ToListAsync()).Select(c => new ScChip(c.Code, c.Name, U("Book", "Cash", new { id = c.Id }))))),
            new("Công nợ & tuổi nợ", "Số dư theo khách hàng / nhà cung cấp, phân tích tuổi nợ, sổ chi tiết công nợ.", R(AppRoles.Accountant, AppRoles.Director),
                Chips(new[] { A("Công nợ phải thu", U("Index", "Debts", new { kind = "ar" })), A("Công nợ phải trả", U("Index", "Debts", new { kind = "ap" })), A("Tuổi nợ phải thu", U("Aging", "Debts", new { kind = "ar" })) })),
            new("Báo cáo & dự báo dòng tiền", "Thu chi theo tháng và dự báo 8 tuần từ hóa đơn đến hạn.", R(AppRoles.Accountant, AppRoles.Director),
                Chips(new[] { A("Báo cáo dòng tiền", U("Flow", "Cash")) })),
        });

    // ------------------------------------------------------------------ 9. Tùy biến
    private ScFlow CustomizeFlow() => new("customize", "9. Cá nhân hóa giao diện", "bi-palette",
        "Mỗi người dùng tự chỉnh giao diện làm việc; mọi thiết lập lưu theo tài khoản.",
        new List<ScStep>
        {
            new("Cấu hình giao diện", "Vị trí menu (trái / phải / trên / dưới), kiểu menu, màu chủ đạo, phông chữ, cỡ chữ, bảng gọn.", R(AppRoles.Accountant, AppRoles.Sales),
                Chips(new[] { A("Cấu hình giao diện", U("Index", "Preferences")) })),
            new("Trang yêu thích", "Bấm ★ cạnh tiêu đề trang hoặc chức năng trong menu để ghim; sắp xếp trên trang chủ.", R(AppRoles.Accountant, AppRoles.Sales),
                Chips(new[] { A("Trang chủ", U("Index", "Home")) })),
            new("Bố cục bảng dữ liệu", "Ẩn / hiện cột, kéo đổi thứ tự cột, kéo mép để chỉnh độ rộng trên mọi bảng danh sách.", R(AppRoles.Accountant, AppRoles.Warehouse),
                Chips(new[] { A("Hàng hóa, vật tư", U("Index", "Items")), A("Phiếu kiểm tra", U("Index", "QcInspections")) })),
        });
}
