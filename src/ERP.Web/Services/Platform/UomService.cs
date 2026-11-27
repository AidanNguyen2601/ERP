using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.ViewModels;

namespace ERP.Web.Services;

/// <summary>Quy đổi đơn vị tính khi nhập chứng từ: dòng chọn ĐVT quy đổi (VD Thùng) được đổi về ĐVT cơ bản của hàng (số lượng × hệ số, đơn giá ÷ hệ số)</summary>
public static class UomService
{
    public static async Task<Dictionary<int, List<(string Unit, decimal Factor)>>> LoadAsync(ErpDbContext db) =>
        (await db.UomConversions.OrderBy(u => u.Unit).ToListAsync()).GroupBy(u => u.ItemId).ToDictionary(g => g.Key, g => g.Select(u => (u.Unit, u.Factor)).ToList());

    public static async Task NormalizeAsync(ErpDbContext db, IEnumerable<LineInput> lines)
    {
        var picked = lines.Where(l => l.ItemId != null && !string.IsNullOrWhiteSpace(l.Unit)).ToList();
        if (picked.Count == 0) return;
        var ids = picked.Select(l => l.ItemId!.Value).Distinct().ToList();
        var convs = await db.UomConversions.Where(u => ids.Contains(u.ItemId)).ToListAsync();
        foreach (var l in picked)
        {
            var c = convs.FirstOrDefault(u => u.ItemId == l.ItemId && u.Unit == l.Unit!.Trim());
            if (c == null || c.Factor <= 0) continue;
            l.Quantity = Math.Round(l.Quantity * c.Factor, 3);
            if (l.CountedQty != null) l.CountedQty = Math.Round(l.CountedQty.Value * c.Factor, 3);
            if (l.UnitPrice != 0) l.UnitPrice = Math.Round(l.UnitPrice / c.Factor, 4);
            l.Unit = null;
        }
    }
}
