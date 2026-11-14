using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ERP.Web.Data;
using ERP.Web.Services;

namespace ERP.Web.Controllers;

/// <summary>Cấu hình giao diện, trang yêu thích và bố cục bảng của người dùng đang đăng nhập</summary>
public class PreferencesController : ErpControllerBase
{
    private readonly PreferenceService _prefs;
    public PreferencesController(ErpDbContext db, PreferenceService prefs) : base(db) => _prefs = prefs;

    public async Task<IActionResult> Index()
    {
        ViewBag.Favorites = await _prefs.GetFavoritesAsync();
        ViewBag.GridCount = (await _prefs.GetGridsAsync()).Count;
        return View(await _prefs.GetUiAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UiSettings model)
    {
        var cur = await _prefs.GetUiAsync();
        model.MenuCollapsed = cur.MenuCollapsed;
        await _prefs.SaveUiAsync(model);
        Success("Đã lưu cấu hình giao diện.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset()
    {
        await _prefs.SaveUiAsync(new UiSettings());
        Success("Đã khôi phục giao diện mặc định.");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------ Gọi từ JavaScript (fetch, kèm header RequestVerificationToken)
    public record CollapseInput(bool Collapsed);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Collapse([FromBody] CollapseInput input)
    {
        var ui = await _prefs.GetUiAsync();
        ui.MenuCollapsed = input?.Collapsed ?? false;
        await _prefs.SaveUiAsync(ui);
        return Ok(new { ok = true });
    }

    public record FavoriteInput(string Url, string? Title, string? Icon);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleFavorite([FromBody] FavoriteInput input)
    {
        try
        {
            if (input == null) return BadRequest(new { ok = false, message = "Dữ liệu không hợp lệ." });
            var on = await _prefs.ToggleFavoriteAsync(input.Url, input.Title ?? "", input.Icon);
            return Ok(new { ok = true, favorite = on, list = await _prefs.GetFavoritesAsync() });
        }
        catch (BusinessException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFavorites([FromBody] List<FavoriteLink> list)
    {
        await _prefs.SaveFavoritesAsync(list ?? new());
        return Ok(new { ok = true, list = await _prefs.GetFavoritesAsync() });
    }

    public record GridInput(string Key, JsonElement? State);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGrid([FromBody] GridInput input)
    {
        try
        {
            if (input == null) return BadRequest(new { ok = false, message = "Dữ liệu không hợp lệ." });
            var state = input.State is { ValueKind: JsonValueKind.Object } s ? s : (JsonElement?)null;
            await _prefs.SaveGridAsync(input.Key, state);
            return Ok(new { ok = true });
        }
        catch (BusinessException ex) { return BadRequest(new { ok = false, message = ex.Message }); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetGrids()
    {
        await _prefs.ResetGridsAsync();
        Success("Đã khôi phục bố cục mặc định cho mọi bảng.");
        return RedirectToAction(nameof(Index));
    }
}
