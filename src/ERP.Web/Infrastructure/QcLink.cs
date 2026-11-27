namespace ERP.Web.Infrastructure;

/// <summary>Nút "Kiểm tra chất lượng" trên chứng từ gốc (partial _QcButton)</summary>
/// <param name="Param">Tên tham số route của QcInspections/Create: poId | productionId | soId</param>
/// <param name="Id">Id chứng từ gốc</param>
/// <param name="Number">Số chứng từ (để xem các phiếu đã lập)</param>
/// <param name="Items">Mặt hàng có thể kiểm tra: (ItemId, nhãn)</param>
public record QcLink(string Param, int Id, string Number, IReadOnlyList<(int ItemId, string Label)> Items);
