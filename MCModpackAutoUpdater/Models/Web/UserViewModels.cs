using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using MCModpackAutoUpdater.Security;

namespace MCModpackAutoUpdater.Models.Web;

public sealed class UsersIndexViewModel
{
    public required IReadOnlyList<UserRowViewModel> Users { get; init; }
}

public sealed class UserRowViewModel
{
    public required string Id { get; init; }

    public required string UserName { get; init; }

    public string? Email { get; init; }

    public required string Roles { get; init; }
}

public sealed class UserEditViewModel
{
    [BindNever]
    [ValidateNever]
    public required string Id { get; init; }

    [BindNever]
    [ValidateNever]
    public required string UserName { get; init; }

    [EmailAddress]
    public string? Email { get; set; }

    [Required]
    public string Role { get; set; } = UpdaterRoles.Viewer;

    [BindNever]
    [ValidateNever]
    public required IReadOnlyList<string> AvailableRoles { get; init; }

    public bool IsCurrentUser { get; init; }

    public bool IsLastAdministrator { get; init; }
}

public sealed class UserPasswordResetViewModel
{
    [Required]
    [DataType(DataType.Password)]
    [MinLength(10)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword))]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class CreateUserViewModel
{
    [Required]
    public string UserName { get; set; } = string.Empty;

    [EmailAddress]
    public string? Email { get; set; }

    [Required]
    [DataType(DataType.Password)]
    [MinLength(10)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(Password))]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = "Viewer";
}
