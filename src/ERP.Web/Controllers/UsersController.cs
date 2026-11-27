using ERP.Web.Data;
using ERP.Web.Infrastructure;
using ERP.Web.Models.Entities;
using ERP.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace ERP.Web.Controllers;

/// <summary>Quản trị người dùng & phân quyền – chỉ Admin</summary>
[Authorize(Roles = AppRoles.Admin)]
public class UsersController : Controller
{
    private readonly ErpDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;

    public UsersController(ErpDbContext db, IPasswordHasher<AppUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _db.Users.OrderBy(u => u.Department).ThenBy(u => u.Username).ToListAsync());
    }

    public IActionResult Create()
    {
        LoadRoles();
        return View("Form", new UserFormViewModel());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null) return NotFound();
        LoadRoles();
        return View("Form", new UserFormViewModel
        {
            Id = u.Id, Username = u.Username, FullName = u.FullName, Email = u.Email, Department = u.Department, Role = u.Role, IsActive = u.IsActive, BranchId = u.BranchId, AllBranches = u.AllBranches, Language = u.Language
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserFormViewModel vm)
    {
        if (!AppRoles.All.Contains(vm.Role)) ModelState.AddModelError(nameof(vm.Role), "Vai trò không hợp lệ");
        if (await _db.Users.AnyAsync(u => u.Username == vm.Username && u.Id != vm.Id))
            ModelState.AddModelError(nameof(vm.Username), "Tên đăng nhập đã tồn tại");
        if (vm.Id == 0 && string.IsNullOrWhiteSpace(vm.Password))
            ModelState.AddModelError(nameof(vm.Password), "Vui lòng nhập mật khẩu");
        if (vm.Id == User.GetUserId() && (!vm.IsActive || vm.Role != AppRoles.Admin))
            ModelState.AddModelError(string.Empty, "Không thể tự khóa hoặc hạ quyền tài khoản đang đăng nhập.");
        if (!ModelState.IsValid)
        {
            LoadRoles();
            return View("Form", vm);
        }

        AppUser? user;
        if (vm.Id == 0) { user = new AppUser(); _db.Users.Add(user); }
        else
        {
            user = await _db.Users.FindAsync(vm.Id);
            if (user == null) return NotFound();
        }
        user.Username = vm.Username;
        user.FullName = vm.FullName;
        user.Email = vm.Email;
        user.Department = vm.Department.Trim();
        user.Role = vm.Role;
        user.IsActive = vm.IsActive;
        user.BranchId = vm.BranchId; user.AllBranches = vm.AllBranches || vm.BranchId == null; user.Language = vm.Language is "en" ? "en" : "vi";
        if (!string.IsNullOrWhiteSpace(vm.Password)) user.PasswordHash = _hasher.HashPassword(user, vm.Password);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu tài khoản {user.Username}.";
        return RedirectToAction(nameof(Index));
    }

    private void LoadRoles()
    {
        ViewBag.Roles = new SelectList(AppRoles.All.Select(r => new { Value = r, Text = AppRoles.DisplayName(r) }), "Value", "Text");
        ViewBag.Branches = new SelectList(_db.Branches.OrderBy(b => b.Code).Select(b => new { b.Id, Name = b.Code + " – " + b.Name }).ToList(), "Id", "Name");
    }
}
