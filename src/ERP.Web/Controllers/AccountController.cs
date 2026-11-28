using System.Security.Claims;
using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Web.Controllers;

public class AccountController : Controller
{
    private readonly ErpDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;

    public AccountController(ErpDbContext db, IPasswordHasher<AppUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == vm.Username);

        if (user == null || !user.IsActive ||
            _hasher.VerifyHashedPassword(user, user.PasswordHash, vm.Password) == PasswordVerificationResult.Failed)
        {
            _db.AuditUserName = vm.Username; _db.AuditIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _db.LogAsync("Đăng nhập thất bại", "AppUser", "Người dùng", user?.Id, vm.Username);
            ModelState.AddModelError(string.Empty, "Tên đăng nhập hoặc mật khẩu không đúng, hoặc tài khoản đã bị khóa.");
            return View(vm);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(ClaimsExtensions.FullNameClaim, user.FullName),
            new(ClaimsExtensions.DepartmentClaim, user.Department)
        };

        claims.AddRange(ERP.Web.Services.BranchScope.ClaimsFor(user));
        Response.Cookies.Append(ERP.Web.Infrastructure.Lang.Cookie, user.Language == "en" ? "en" : "vi", new CookieOptions { IsEssential = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.Now.AddYears(1) });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = vm.RememberMe });

        user.LastLoginAt = DateTime.Now;
        _db.AuditUserId = user.Id; _db.AuditUserName = user.FullName; _db.AuditIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _db.LogAsync("Đăng nhập", "AppUser", "Người dùng", user.Id, user.Username);
        _db.AuditUserId = null;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(vm.ReturnUrl) && Url.IsLocalUrl(vm.ReturnUrl))
            return Redirect(vm.ReturnUrl);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, vm.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(nameof(vm.CurrentPassword), "Mật khẩu hiện tại không đúng");
            return View(vm);
        }

        user.PasswordHash = _hasher.HashPassword(user, vm.NewPassword);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Đổi mật khẩu thành công.";
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    public IActionResult AccessDenied(string? reason) { ViewBag.Reason = reason; return View(); }
}
