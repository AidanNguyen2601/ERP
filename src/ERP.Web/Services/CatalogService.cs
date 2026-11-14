using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Đọc danh mục dùng chung cho các ô chọn trên form</summary>
public class CatalogService
{
    private readonly ErpDbContext _db;
    public CatalogService(ErpDbContext db) => _db = db;

    /// <summary>Tên các giá trị đang dùng (kèm giá trị hiện tại của bản ghi nếu đã ngưng / chưa có trong danh mục)</summary>
    public async Task<List<string>> NamesAsync(CatalogType type, string? current = null)
    {
        var names = await _db.CatalogItems.Where(c => c.Type == type && (c.IsActive || c.Name == current))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => c.Name).ToListAsync();
        if (!string.IsNullOrWhiteSpace(current) && !names.Contains(current, StringComparer.OrdinalIgnoreCase)) names.Insert(0, current);
        return names;
    }

    public async Task<SelectList> SelectAsync(CatalogType type, string? current = null) =>
        new(await NamesAsync(type, current), current);

    public Task<bool> ExistsAsync(CatalogType type, string? name) =>
        _db.CatalogItems.AnyAsync(c => c.Type == type && c.Name == name && c.IsActive);
}
