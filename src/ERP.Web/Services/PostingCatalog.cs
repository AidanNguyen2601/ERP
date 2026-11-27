using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Một dòng định khoản trong mẫu bút toán của giao dịch</summary>
/// <param name="Key">Mã cấu hình (null = dòng cố định, chỉ để hiển thị)</param>
/// <param name="Side">"Nợ" / "Có" / "Nợ / Có"</param>
/// <param name="DefaultCode">Tài khoản mặc định khi chưa cấu hình</param>
/// <param name="Prefixes">Chỉ cho chọn tài khoản chi tiết có số hiệu bắt đầu bằng một trong các đầu số này
/// (giữ báo cáo công nợ, thuế, báo cáo tài chính luôn đúng)</param>
public record PostingLineDef(string? Key, string Side, string Title, string? DefaultCode = null, string[]? Prefixes = null, string? Note = null)
{
    public bool Configurable => Key != null;
}

/// <summary>Một loại giao dịch hạch toán tự động</summary>
public record PostingTxDef(string Code, string Title, string Icon, string Module, string Description, PostingLineDef[] Lines);

/// <summary>
/// Danh mục giao dịch hạch toán tự động và các tài khoản có thể cấu hình.
/// Tài khoản thực tế = cấu hình trong bảng PostingRules (nếu có, còn hiệu lực) hoặc tài khoản mặc định ở đây.
/// </summary>
public static class PostingCatalog
{
    // ---- Khóa cấu hình (dùng trong các service hạch toán)
    public const string InvMaterial = "Inv.Material", InvFinished = "Inv.FinishedGood", InvGoods = "Inv.Merchandise", InvSemi = "Inv.SemiFinished";
    public const string SiReceivable = "SI.Receivable", SiRevenueGoods = "SI.RevenueGoods", SiRevenueProducts = "SI.RevenueProducts",
        SiRevenueServices = "SI.RevenueServices", SiVatOut = "SI.VatOut";
    public const string PiExpense = "PI.Expense", PiVatIn = "PI.VatIn", PiPayable = "PI.Payable";
    public const string PrPayable = "PR.Payable";
    public const string SdCogs = "SD.Cogs";
    public const string CrReceivable = "CR.Receivable";
    public const string CpPayable = "CP.Payable";
    public const string PrdWip = "PRD.Wip", PrdLabor = "PRD.Labor", PrdOverhead = "PRD.Overhead";
    public const string ScSurplus = "SC.Surplus", ScShortage = "SC.Shortage";
    public const string AdLoss = "AD.Loss", AdIncome = "AD.Income";
    public const string PcSummary = "PC.Summary", PcRetained = "PC.Retained";
    public const string CnSalesReturn = "CN.SalesReturn", CnPriceReduction = "CN.PriceReduction";
    public const string QcScrap = "QC.Scrap", QcReturnPayable = "QC.ReturnPayable", QcReturnVariance = "QC.ReturnVariance";

    private const string Inventory = "TK hàng tồn kho theo loại hàng (cấu hình ở nhóm \"Hàng tồn kho\")";

    public static readonly PostingTxDef[] Transactions =
    {
        new("inventory", "Hàng tồn kho theo loại hàng", "bi-boxes", "Kho",
            "Tài khoản kho dùng chung cho mọi nghiệp vụ nhập / xuất / kiểm kê, xác định theo loại của mặt hàng.", new PostingLineDef[]
        {
            new(InvMaterial, "Nợ / Có", "Nguyên vật liệu", "152", new[] { "152" }),
            new(InvFinished, "Nợ / Có", "Thành phẩm", "155", new[] { "155" }),
            new(InvSemi, "Nợ / Có", "Bán thành phẩm", "155", new[] { "155", "154" }),
            new(InvGoods, "Nợ / Có", "Hàng hóa", "156", new[] { "156" }),
        }),
        new("sales-invoice", "Phát hành hóa đơn bán ra", "bi-receipt-cutoff", "Hóa đơn",
            "Ghi nhận doanh thu, thuế GTGT đầu ra và công nợ phải thu khi phát hành hóa đơn bán. Dòng hóa đơn có chọn TK riêng sẽ dùng TK đó.", new PostingLineDef[]
        {
            new(SiReceivable, "Nợ", "Phải thu khách hàng (tổng thanh toán)", "131", new[] { "131" }),
            new(SiRevenueGoods, "Có", "Doanh thu bán hàng hóa / vật tư", "5111", new[] { "511" }),
            new(SiRevenueProducts, "Có", "Doanh thu bán thành phẩm", "5112", new[] { "511" }),
            new(SiRevenueServices, "Có", "Doanh thu cung cấp dịch vụ", "5113", new[] { "511" }),
            new(SiVatOut, "Có", "Thuế GTGT đầu ra", "33311", new[] { "3331" }),
        }),
        new("sales-credit", "Hàng bán bị trả lại / giảm giá hàng bán", "bi-receipt", "Hóa đơn",
            "Hóa đơn điều chỉnh giảm cho hóa đơn bán đã phát hành: giảm trừ doanh thu, hoàn thuế GTGT đầu ra, giảm công nợ phải thu (cấn trừ vào hóa đơn gốc). Hàng trả lại được nhập kho theo giá vốn lúc bán (Nợ 15x / Có TK giá vốn ở giao dịch \"Xuất kho giao hàng bán\").", new PostingLineDef[]
        {
            new(CnSalesReturn, "Nợ", "Hàng bán bị trả lại (giảm trừ doanh thu)", "521", new[] { "521" }),
            new(CnPriceReduction, "Nợ", "Giảm giá hàng bán (giảm trừ doanh thu)", "521", new[] { "521" }),
            new(null, "Nợ", "Thuế GTGT đầu ra được điều chỉnh giảm: TK cấu hình ở \"Phát hành hóa đơn bán ra\""),
            new(null, "Có", "Phải thu khách hàng: TK cấu hình ở \"Phát hành hóa đơn bán ra\""),
        }),
        new("purchase-invoice", "Ghi sổ hóa đơn mua vào", "bi-file-earmark-text", "Hóa đơn",
            "Hóa đơn mua dịch vụ / chi phí trực tiếp và phần thuế GTGT của hóa đơn mua hàng theo đơn mua (giá trị hàng đã hạch toán khi nhập kho).", new PostingLineDef[]
        {
            new(PiExpense, "Nợ", "Chi phí mặc định (dòng không chọn TK)", "6427", new[] { "6", "8", "241", "242", "138" }),
            new(PiVatIn, "Nợ", "Thuế GTGT được khấu trừ", "1331", new[] { "133" }),
            new(PiPayable, "Có", "Phải trả nhà cung cấp", "331", new[] { "331" }),
        }),
        new("purchase-receipt", "Nhập kho hàng mua", "bi-box-arrow-in-down", "Mua hàng",
            "Nhận hàng theo đơn mua: tăng hàng tồn kho theo giá mua chưa thuế, ghi nhận phải trả nhà cung cấp.", new PostingLineDef[]
        {
            new(null, "Nợ", Inventory),
            new(PrPayable, "Có", "Phải trả nhà cung cấp", "331", new[] { "331" }),
        }),
        new("sales-delivery", "Xuất kho giao hàng bán", "bi-truck", "Bán hàng",
            "Giao hàng theo đơn bán: ghi nhận giá vốn theo giá bình quân gia quyền di động.", new PostingLineDef[]
        {
            new(SdCogs, "Nợ", "Giá vốn hàng bán", "632", new[] { "632" }),
            new(null, "Có", Inventory),
        }),
        new("cash-receipt", "Phiếu thu tiền khách hàng", "bi-cash-coin", "Dòng tiền",
            "Thu tiền khách hàng, tự cấn trừ công nợ theo hóa đơn.", new PostingLineDef[]
        {
            new(null, "Nợ", "TK tiền của quỹ / tài khoản ngân hàng chọn trên phiếu"),
            new(CrReceivable, "Có", "Phải thu khách hàng", "131", new[] { "131" }),
        }),
        new("cash-payment", "Phiếu chi trả nhà cung cấp", "bi-cash-stack", "Dòng tiền",
            "Thanh toán cho nhà cung cấp, tự cấn trừ công nợ theo hóa đơn mua.", new PostingLineDef[]
        {
            new(CpPayable, "Nợ", "Phải trả nhà cung cấp", "331", new[] { "331" }),
            new(null, "Có", "TK tiền của quỹ / tài khoản ngân hàng chọn trên phiếu"),
        }),
        new("production", "Sản xuất: xuất NVL, kết chuyển chi phí, nhập thành phẩm", "bi-gear-wide-connected", "Sản xuất",
            "Xuất NVL: Nợ 154 / Có 152 · Hoàn thành: Nợ 154 / Có 622, 627 rồi nhập kho Nợ 155 / Có 154.", new PostingLineDef[]
        {
            new(PrdWip, "Nợ / Có", "Chi phí sản xuất kinh doanh dở dang", "154", new[] { "154" }),
            new(PrdLabor, "Có", "Chi phí nhân công trực tiếp kết chuyển", "622", new[] { "622", "334" }),
            new(PrdOverhead, "Có", "Chi phí sản xuất chung kết chuyển", "6278", new[] { "627" }),
        }),
        new("stock-count", "Kiểm kê kho", "bi-clipboard2-check", "Kho",
            "Chênh lệch thực tế − sổ sách được định giá theo giá vốn bình quân.", new PostingLineDef[]
        {
            new(null, "Nợ / Có", Inventory),
            new(ScSurplus, "Có", "Hàng thừa chờ xử lý", "3381", new[] { "338", "711", "632" }),
            new(ScShortage, "Nợ", "Hàng thiếu chờ xử lý", "1381", new[] { "138", "632", "811" }),
        }),
        new("qc-scrap", "Xuất hủy hàng không đạt chất lượng", "bi-trash3", "Quản lý chất lượng",
            "NCR quyết định hủy bỏ: xuất kho số lượng không phù hợp theo giá vốn bình quân.", new PostingLineDef[]
        {
            new(QcScrap, "Nợ", "Chi phí hàng hủy", "811", new[] { "811", "632", "138", "642", "154" }),
            new(null, "Có", Inventory),
        }),
        new("qc-return", "Trả lại hàng không đạt cho nhà cung cấp", "bi-arrow-return-left", "Quản lý chất lượng",
            "NCR quyết định trả lại nhà cung cấp: giảm hàng tồn kho theo giá vốn, giảm công nợ phải trả theo giá mua (kèm hoàn thuế GTGT đầu vào, cấn trừ vào hóa đơn mua).", new PostingLineDef[]
        {
            new(QcReturnPayable, "Nợ", "Giảm phải trả nhà cung cấp (theo giá vốn bình quân)", "331", new[] { "331", "1388" }),
            new(QcReturnVariance, "Nợ / Có", "Chênh lệch giá mua so với giá vốn bình quân khi trả hàng", "632", new[] { "632", "711", "811", "156" }, "Phần giá mua cao / thấp hơn giá vốn kho được ghi vào đây để công nợ giảm đúng bằng giá trên hóa đơn"),
            new(null, "Nợ / Có", "Thuế GTGT đầu vào được hoàn (phần đã lập hóa đơn): Nợ 331 / Có TK thuế đầu vào cấu hình ở \"Ghi sổ hóa đơn mua vào\""),
            new(null, "Có", Inventory),
        }),
        new("asset-disposal", "Thanh lý / nhượng bán TSCĐ", "bi-building-dash", "Tài sản",
            "Nợ hao mòn (TK của nhóm tài sản), Nợ giá trị còn lại / Có nguyên giá; tiền thu thanh lý ghi Nợ TK tiền.", new PostingLineDef[]
        {
            new(AdLoss, "Nợ", "Giá trị còn lại của tài sản thanh lý", "811", new[] { "811" }),
            new(AdIncome, "Có", "Thu nhập từ thanh lý", "711", new[] { "711" }),
        }),
        new("period-close", "Kết chuyển cuối kỳ", "bi-calendar2-check", "Kế toán tổng hợp",
            "Kết chuyển doanh thu, chi phí (đầu 5–8) sang xác định kết quả, kết quả sang lợi nhuận chưa phân phối.", new PostingLineDef[]
        {
            new(PcSummary, "Nợ / Có", "Xác định kết quả kinh doanh", "911", new[] { "911" }),
            new(PcRetained, "Nợ / Có", "Lợi nhuận sau thuế chưa phân phối năm nay", "4212", new[] { "421" }),
        }),
    };

    private static readonly Dictionary<string, PostingLineDef> ByKey =
        Transactions.SelectMany(t => t.Lines).Where(l => l.Key != null).ToDictionary(l => l.Key!);

    public static PostingLineDef? Find(string key) => ByKey.GetValueOrDefault(key);
    public static IEnumerable<PostingLineDef> AllRules => ByKey.Values;

    public static bool Allows(PostingLineDef def, string accountCode) =>
        def.Prefixes == null || def.Prefixes.Any(p => accountCode.StartsWith(p, StringComparison.Ordinal));

    public static string InventoryKey(ItemType t) => t switch
    {
        ItemType.Material => InvMaterial,
        ItemType.FinishedGood => InvFinished,
        ItemType.SemiFinished => InvSemi,
        _ => InvGoods
    };

    public static string RevenueKey(ItemType? t) => t switch
    {
ItemType.FinishedGood or ItemType.SemiFinished => SiRevenueProducts,
        ItemType.Merchandise or ItemType.Material => SiRevenueGoods,
        _ => SiRevenueServices
    };
}
