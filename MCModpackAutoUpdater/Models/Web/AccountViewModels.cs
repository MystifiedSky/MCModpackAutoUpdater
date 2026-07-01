using System.ComponentModel.DataAnnotations;

namespace MCModpackAutoUpdater.Models.Web;

public sealed class LoginViewModel
{
    [Required]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}

public sealed class SetupViewModel
{
    [Required]
    public string SetupToken { get; set; } = string.Empty;

    [Required]
    public string UserName { get; set; } = "admin";

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
}
