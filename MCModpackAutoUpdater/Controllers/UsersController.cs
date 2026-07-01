using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Security;

namespace MCModpackAutoUpdater.Controllers;

[Authorize(Roles = UpdaterRoles.Admin)]
public sealed class UsersController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;

    public UsersController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpGet("/users")]
    public async Task<IActionResult> Index()
    {
        var users = await _userManager.Users
            .OrderBy(static user => user.UserName)
            .ToListAsync();
        var rows = new List<UserRowViewModel>();
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            rows.Add(new UserRowViewModel
            {
                Id = user.Id,
                UserName = user.UserName ?? "(unknown)",
                Email = user.Email,
                Roles = string.Join(", ", roles.OrderBy(static role => role))
            });
        }

        return View(new UsersIndexViewModel { Users = rows });
    }

    [HttpGet("/users/create")]
    public IActionResult Create()
    {
        ViewBag.Roles = UpdaterRoles.All;
        return View(new CreateUserViewModel());
    }

    [HttpPost("/users/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserViewModel model)
    {
        ViewBag.Roles = UpdaterRoles.All;
        if (!UpdaterRoles.All.Contains(model.Role, StringComparer.Ordinal))
        {
            ModelState.AddModelError(nameof(model.Role), "Invalid role.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.UserName.Trim(),
            Email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim()
        };
        var result = await _userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(model);
        }

        await _userManager.AddToRoleAsync(user, model.Role);
        return RedirectToAction(nameof(Index));
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }
}
