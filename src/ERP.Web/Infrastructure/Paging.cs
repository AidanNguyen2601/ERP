using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Web.Infrastructure;

/// <summary>Phân trang phía máy chủ: đếm tổng, lấy đúng một trang; thông tin trang đặt vào ViewBag cho partial _PagerBag</summary>
public static class Paging
{
    public const int DefaultSize = 50;
    public static readonly int[] Sizes = { 25, 50, 100, 200 };

    public static int SizeFrom(HttpRequest req) => int.TryParse(req.Query["size"], out var s) && Sizes.Contains(s) ? s : DefaultSize;

    public static async Task<List<T>> PageAsync<T>(this IQueryable<T> query, Controller c, int page, int? size = null)
    {
        var sz = size ?? SizeFrom(c.Request);
        var total = await query.CountAsync();
        var pages = Math.Max(1, (total + sz - 1) / sz);
        page = Math.Clamp(page, 1, pages);
        c.ViewBag.PageNo = page; c.ViewBag.PageCount = pages; c.ViewBag.PageTotal = total; c.ViewBag.PageSize = sz;
        return await query.Skip((page - 1) * sz).Take(sz).ToListAsync();
    }
}
