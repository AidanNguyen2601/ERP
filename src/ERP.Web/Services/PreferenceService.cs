using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ERP.Web.Data;
using ERP.Web.Models.Entities;

namespace ERP.Web.Services;

/// <summary>Cấu hình giao diện theo người dùng</summary>
public class UiSettings
{
    public static readonly (string Key, string Name, string Css)[] Fonts =
    {
        ("segoe", "Segoe UI (mặc định)", "\"Segoe UI\", Roboto, Arial, sans-serif"),
        ("roboto", "Roboto", "Roboto, \"Segoe UI\", Arial, sans-serif"),
        ("arial", "Arial", "Arial, Helvetica, sans-serif"),
        ("tahoma", "Tahoma", "Tahoma, Verdana, sans-serif"),
        ("verdana", "Verdana", "Verdana, Tahoma, sans-serif"),
        ("times", "Times New Roman", "\"Times New Roman\", Times, serif"),
        ("mono", "Consolas (đơn cách)", "Consolas, \"Courier New\", monospace"),
    };
    public static readonly (string Hex, string Name)[] Colors =
    {
        ("#1d5fd1", "Xanh dương"), ("#0d9488", "Xanh ngọc"), ("#15803d", "Xanh lá"), ("#7c3aed", "Tím"),
        ("#db2777", "Hồng"), ("#dc2626", "Đỏ"), ("#ea580c", "Cam"), ("#475569", "Xám đá"),
    };
    public static readonly (string Key, string Name, string Icon)[] MenuPositions =
    {
        ("left", "Bên trái", "bi-layout-sidebar"), ("right", "Bên phải", "bi-layout-sidebar-reverse"),
        ("top", "Phía trên", "bi-window-dock"), ("bottom", "Phía dưới", "bi-window-desktop"),
    };
    public static readonly (string Key, string Name)[] Sidebars = { ("dark", "Tối"), ("light", "Sáng"), ("accent", "Theo màu chủ đạo") };

    public string Font { get; set; } = "segoe";
    public int FontSize { get; set; } = 14;
    public string PrimaryColor { get; set; } = "#1d5fd1";
    public string MenuPosition { get; set; } = "left";
    public string SidebarStyle { get; set; } = "dark";
    public bool MenuCollapsed { get; set; }
    public bool CompactTables { get; set; }

    [JsonIgnore, ValidateNever] public string FontCss => Fonts.FirstOrDefault(f => f.Key == Font).Css ?? Fonts[0].Css;
    [JsonIgnore, ValidateNever] public bool IsVertical => MenuPosition is "left" or "right";

    /// <summary>Chuẩn hóa giá trị người dùng gửi lên (tránh chèn CSS tùy ý)</summary>
    public UiSettings Normalize()
    {
        if (Fonts.All(f => f.Key != Font)) Font = Fonts[0].Key;
        FontSize = Math.Clamp(FontSize, 12, 18);
        if (!IsHex(PrimaryColor)) PrimaryColor = Colors[0].Hex;
        PrimaryColor = PrimaryColor!.ToLowerInvariant();
        if (MenuPositions.All(m => m.Key != MenuPosition)) MenuPosition = "left";
        if (Sidebars.All(s => s.Key != SidebarStyle)) SidebarStyle = "dark";
        return this;
    }

    private static bool IsHex(string? s) => s != null && Regex.IsMatch(s, @"\A#[0-9a-fA-F]{6}\z");

    /// <summary>Màu đậm hơn (hover) – giảm độ sáng 18%</summary>
    [JsonIgnore, ValidateNever] public string PrimaryDark => Shade(PrimaryColor, 0.82);
    [JsonIgnore, ValidateNever] public string PrimarySoft => Mix(PrimaryColor, 0.14);

    private static (int R, int G, int B) Rgb(string hex) => !IsHex(hex) ? (0x1d, 0x5f, 0xd1) :
        (int.Parse(hex[1..3], NumberStyles.HexNumber), int.Parse(hex[3..5], NumberStyles.HexNumber), int.Parse(hex[5..7], NumberStyles.HexNumber));
    private static string Shade(string hex, double f)
    {
        var (r, g, b) = Rgb(hex);
        return $"#{(int)(r * f):x2}{(int)(g * f):x2}{(int)(b * f):x2}";
    }
    /// <summary>Pha màu với trắng: alpha = tỷ lệ màu gốc</summary>
    private static string Mix(string hex, double a)
    {
        var (r, g, b) = Rgb(hex);
        int M(int c) => (int)Math.Round(255 - (255 - c) * a);
        return $"#{M(r):x2}{M(g):x2}{M(b):x2}";
    }
}

/// <summary>Một trang được đánh dấu yêu thích</summary>
public class FavoriteLink
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "bi-star";
}

/// <summary>Đọc / ghi tùy chọn người dùng (bảng UserPreferences, giá trị JSON). Cache trong phạm vi request.</summary>
public class PreferenceService
{
    public const string UiKey = "ui";
    public const string FavoritesKey = "favorites";
    public const string GridPrefix = "grid:";
    public const int MaxFavorites = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ErpDbContext _db;
    private readonly CurrentUser _user;
    private Dictionary<string, UserPreference>? _cache;

    public PreferenceService(ErpDbContext db, CurrentUser user) { _db = db; _user = user; }

    private async Task<Dictionary<string, UserPreference>> AllAsync()
    {
        if (_cache != null) return _cache;
        if (_user.Id == null) return _cache = new();
        try { _cache = await _db.UserPreferences.Where(p => p.UserId == _user.Id.Value).ToDictionaryAsync(p => p.Key); }
        catch { _cache = new(); } // không để lỗi đọc tùy chọn làm hỏng trang
        return _cache;
    }

    private async Task<T?> GetAsync<T>(string key)
    {
        var all = await AllAsync();
        if (!all.TryGetValue(key, out var p)) return default;
        try { return JsonSerializer.Deserialize<T>(p.Value, Json); } catch { return default; }
    }

    private async Task SetAsync(string key, object? value)
    {
        if (_user.Id == null) return;
        var all = await AllAsync();
        if (value == null)
        {
            if (all.Remove(key, out var old)) _db.UserPreferences.Remove(old);
        }
        else
        {
            var json = value as string ?? JsonSerializer.Serialize(value, Json);
            if (!all.TryGetValue(key, out var p))
            {
                p = new UserPreference { UserId = _user.Id.Value, Key = key };
                _db.UserPreferences.Add(p); all[key] = p;
            }
            p.Value = json; p.UpdatedAt = DateTime.Now;
        }
        await _db.SaveChangesAsync();
    }

    // ---------------- Giao diện
    public async Task<UiSettings> GetUiAsync() => (await GetAsync<UiSettings>(UiKey) ?? new UiSettings()).Normalize();
    public Task SaveUiAsync(UiSettings s) => SetAsync(UiKey, s.Normalize());

    // ---------------- Yêu thích
    public async Task<List<FavoriteLink>> GetFavoritesAsync() => await GetAsync<List<FavoriteLink>>(FavoritesKey) ?? new();

    /// <summary>Chỉ nhận đường dẫn nội bộ ("/..."), không nhận URL ngoài</summary>
    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") && url.Length <= 500;

    public static string NormalizeIcon(string? icon) =>
        icon != null && Regex.IsMatch(icon, "^bi-[a-z0-9-]{1,40}$") ? icon : "bi-star";

    /// <summary>Bật / tắt yêu thích; trả về true nếu sau thao tác trang đang được yêu thích</summary>
    public async Task<bool> ToggleFavoriteAsync(string url, string title, string? icon)
    {
        if (!IsLocalUrl(url)) throw new BusinessException("Đường dẫn không hợp lệ.");
        var list = await GetFavoritesAsync();
        var found = list.FindIndex(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        if (found >= 0) list.RemoveAt(found);
        else
        {
            if (list.Count >= MaxFavorites) throw new BusinessException($"Tối đa {MaxFavorites} trang yêu thích – bỏ bớt trước khi thêm.");
            title = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
            list.Add(new FavoriteLink { Url = url, Title = title.Length > 100 ? title[..100] : title, Icon = NormalizeIcon(icon) });
        }
        await SetAsync(FavoritesKey, list);
        return found < 0;
    }

    public async Task SaveFavoritesAsync(List<FavoriteLink> list)
    {
        var clean = list.Where(f => IsLocalUrl(f.Url)).Take(MaxFavorites)
            .Select(f => new FavoriteLink { Url = f.Url, Title = string.IsNullOrWhiteSpace(f.Title) ? f.Url : f.Title.Trim()[..Math.Min(100, f.Title.Trim().Length)], Icon = NormalizeIcon(f.Icon) })
            .DistinctBy(f => f.Url.ToLowerInvariant()).ToList();
        await SetAsync(FavoritesKey, clean);
    }

    // ---------------- Bố cục bảng (ẩn / hiện, thứ tự, độ rộng cột)
    public async Task<Dictionary<string, JsonElement>> GetGridsAsync()
    {
        var all = await AllAsync();
        var res = new Dictionary<string, JsonElement>();
        foreach (var (k, p) in all.Where(x => x.Key.StartsWith(GridPrefix)))
            try { res[k[GridPrefix.Length..]] = JsonSerializer.Deserialize<JsonElement>(p.Value); } catch { }
        return res;
    }

    public Task SaveGridAsync(string gridKey, JsonElement? state)
    {
        if (string.IsNullOrWhiteSpace(gridKey) || gridKey.Length > 180) throw new BusinessException("Mã bảng không hợp lệ.");
        if (state is { } s && s.GetRawText().Length > 8000) throw new BusinessException("Cấu hình bảng quá lớn.");
        return SetAsync(GridPrefix + gridKey, state?.GetRawText());
    }

    public async Task ResetGridsAsync()
    {
        var all = await AllAsync();
        foreach (var k in all.Keys.Where(k => k.StartsWith(GridPrefix)).ToList())
        {
            _db.UserPreferences.Remove(all[k]); all.Remove(k);
        }
        await _db.SaveChangesAsync();
    }
}
