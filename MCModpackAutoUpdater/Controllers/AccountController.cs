using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Security;

namespace MCModpackAutoUpdater.Controllers;

public sealed class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UpdaterIdentityDbContext _dbContext;
    private readonly FirstRunSetupToken _setupToken;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        UpdaterIdentityDbContext dbContext,
        FirstRunSetupToken setupToken,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _dbContext = dbContext;
        _setupToken = setupToken;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet("/setup")]
    public async Task<IActionResult> Setup(CancellationToken cancellationToken)
    {
        if (await HasUsersAsync(cancellationToken))
        {
            return RedirectToAction(nameof(Login));
        }

        var token = _setupToken.EnsureToken();
        LogSetupToken(token);
        return View(new SetupViewModel());
    }

    [AllowAnonymous]
    [HttpPost("/setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup(SetupViewModel model, CancellationToken cancellationToken)
    {
        if (await HasUsersAsync(cancellationToken))
        {
            return RedirectToAction(nameof(Login));
        }

        if (!_setupToken.Validate(model.SetupToken))
        {
            ModelState.AddModelError(nameof(model.SetupToken), "Setup token is invalid.");
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

        await _userManager.AddToRoleAsync(user, UpdaterRoles.Admin);
        _setupToken.Invalidate();
        await _signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToAction("Index", "Dashboard");
    }

    [AllowAnonymous]
    [HttpGet("/login")]
    public async Task<IActionResult> Login(CancellationToken cancellationToken)
    {
        if (!await HasUsersAsync(cancellationToken))
        {
            return RedirectToAction(nameof(Setup));
        }

        return View(new LoginViewModel());
    }

    [AllowAnonymous]
    [HttpPost("/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!await HasUsersAsync(HttpContext.RequestAborted))
        {
            return RedirectToAction(nameof(Setup));
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _signInManager.PasswordSignInAsync(
            model.UserName.Trim(),
            model.Password,
            model.RememberMe,
            lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return RedirectToAction("Index", "Dashboard");
        }

        ModelState.AddModelError(string.Empty, "Invalid login.");
        return View(model);
    }

    [HttpPost("/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet("/denied")]
    public IActionResult Denied()
    {
        return View();
    }

    private Task<bool> HasUsersAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Users.AsNoTracking().AnyAsync(cancellationToken);
    }

    private void LogSetupToken(string token)
    {
        var message = $"MCModpackAutoUpdater first-run setup token: {token}";
        Console.WriteLine(message);
        Console.Out.Flush();
        _logger.LogWarning("{SetupTokenMessage}", message);
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }
}
