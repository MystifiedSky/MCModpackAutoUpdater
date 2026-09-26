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
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UpdaterIdentityDbContext _dbContext;

    public UsersController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        UpdaterIdentityDbContext dbContext)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _dbContext = dbContext;
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

    [HttpGet("/users/{id}")]
    public async Task<IActionResult> Edit(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            TempData["Message"] = "User was not found.";
            return RedirectToAction(nameof(Index));
        }

        return View(await BuildEditModelAsync(user));
    }

    [HttpPost("/users/{id}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string id, UserEditViewModel model, CancellationToken cancellationToken)
    {
        if (!UpdaterRoles.All.Contains(model.Role, StringComparer.Ordinal))
        {
            ModelState.AddModelError(nameof(model.Role), "Select a valid role.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            TempData["Message"] = "User was not found.";
            return RedirectToAction(nameof(Index));
        }

        var existingRoles = await _userManager.GetRolesAsync(user);
        var roleChanges = existingRoles.Count != 1 || !existingRoles.Contains(model.Role, StringComparer.Ordinal);
        var demotesAdministrator = existingRoles.Contains(UpdaterRoles.Admin, StringComparer.Ordinal) &&
                                   model.Role != UpdaterRoles.Admin;
        var isCurrentUser = string.Equals(_userManager.GetUserId(User), user.Id, StringComparison.Ordinal);
        if (demotesAdministrator && isCurrentUser)
        {
            ModelState.AddModelError(nameof(model.Role), "You cannot remove your own administrator role.");
        }

        if (demotesAdministrator && (await _userManager.GetUsersInRoleAsync(UpdaterRoles.Admin)).Count <= 1)
        {
            ModelState.AddModelError(nameof(model.Role), "The last administrator cannot be demoted.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Edit), await BuildEditModelAsync(user, model));
        }

        var normalizedEmail = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim();
        var emailChanged = !string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            var emailResult = await _userManager.SetEmailAsync(user, normalizedEmail);
            if (!emailResult.Succeeded)
            {
                AddErrors(emailResult);
                return View(nameof(Edit), await BuildEditModelAsync(user, model));
            }
        }

        if (roleChanges)
        {
            if (existingRoles.Count > 0)
            {
                var removeResult = await _userManager.RemoveFromRolesAsync(user, existingRoles);
                if (!removeResult.Succeeded)
                {
                    AddErrors(removeResult);
                    return View(nameof(Edit), await BuildEditModelAsync(user, model));
                }
            }

            var addResult = await _userManager.AddToRoleAsync(user, model.Role);
            if (!addResult.Succeeded)
            {
                AddErrors(addResult);
                return View(nameof(Edit), await BuildEditModelAsync(user, model));
            }

            var stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
            {
                AddErrors(stampResult);
                return View(nameof(Edit), await BuildEditModelAsync(user, model));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (emailChanged && !roleChanges && isCurrentUser)
        {
            await _signInManager.RefreshSignInAsync(user);
        }

        TempData["Message"] = $"User '{user.UserName}' updated.";
        return RedirectToAction(nameof(Edit), new { id = user.Id });
    }

    [HttpPost("/users/{id}/reset-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(
        string id,
        UserPasswordResetViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["Message"] = "Password was not reset. Enter a valid password and matching confirmation.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            TempData["Message"] = "User was not found.";
            return RedirectToAction(nameof(Index));
        }

        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await _userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);
        if (!resetResult.Succeeded)
        {
            TempData["Message"] = $"Password was not reset: {FormatErrors(resetResult)}";
            return RedirectToAction(nameof(Edit), new { id });
        }

        var stampResult = await _userManager.UpdateSecurityStampAsync(user);
        if (!stampResult.Succeeded)
        {
            TempData["Message"] = $"Password was not reset: {FormatErrors(stampResult)}";
            return RedirectToAction(nameof(Edit), new { id });
        }

        await transaction.CommitAsync(cancellationToken);
        TempData["Message"] = $"Password reset for '{user.UserName}'. Sign in again with the new password.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("/users/{id}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            TempData["Message"] = "User was not found.";
            return RedirectToAction(nameof(Index));
        }

        if (string.Equals(_userManager.GetUserId(User), user.Id, StringComparison.Ordinal))
        {
            TempData["Message"] = "You cannot delete the account you are currently using.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        var roles = await _userManager.GetRolesAsync(user);
        if (roles.Contains(UpdaterRoles.Admin, StringComparer.Ordinal) &&
            (await _userManager.GetUsersInRoleAsync(UpdaterRoles.Admin)).Count <= 1)
        {
            TempData["Message"] = "The last administrator cannot be deleted.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            TempData["Message"] = $"User was not deleted: {FormatErrors(result)}";
            return RedirectToAction(nameof(Edit), new { id });
        }

        await transaction.CommitAsync(cancellationToken);
        TempData["Message"] = $"User '{user.UserName}' deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/users/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserViewModel model, CancellationToken cancellationToken)
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
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await _userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(model);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, model.Role);
        if (!roleResult.Succeeded)
        {
            AddErrors(roleResult);
            return View(model);
        }

        await transaction.CommitAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    private async Task<UserEditViewModel> BuildEditModelAsync(
        ApplicationUser user,
        UserEditViewModel? postedValues = null)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var role = postedValues?.Role ?? UpdaterRoles.All.FirstOrDefault(roles.Contains) ?? UpdaterRoles.Viewer;
        var isLastAdministrator = roles.Contains(UpdaterRoles.Admin, StringComparer.Ordinal) &&
                                  (await _userManager.GetUsersInRoleAsync(UpdaterRoles.Admin)).Count <= 1;

        return new UserEditViewModel
        {
            Id = user.Id,
            UserName = user.UserName ?? "(unknown)",
            Email = postedValues is null ? user.Email : postedValues.Email,
            Role = role,
            AvailableRoles = UpdaterRoles.All,
            IsCurrentUser = string.Equals(_userManager.GetUserId(User), user.Id, StringComparison.Ordinal),
            IsLastAdministrator = isLastAdministrator
        };
    }

    private static string FormatErrors(IdentityResult result)
    {
        return string.Join(" ", result.Errors.Select(static error => error.Description));
    }
}
