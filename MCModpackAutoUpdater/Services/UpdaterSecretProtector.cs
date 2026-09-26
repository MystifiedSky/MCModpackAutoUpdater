using Microsoft.AspNetCore.DataProtection;

namespace MCModpackAutoUpdater.Services;

/// <summary>Protects stored credentials while accepting legacy plaintext during upgrades.</summary>
public sealed class UpdaterSecretProtector(IDataProtectionProvider provider)
{
    public const string Prefix = "protected:v1:";
    private readonly IDataProtector _protector = provider.CreateProtector("MCModpackAutoUpdater.Settings.v1");

    public string Protect(string value) => string.IsNullOrEmpty(value)
        ? value
        : Prefix + _protector.Protect(value);

    public string Unprotect(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? _protector.Unprotect(value[Prefix.Length..])
        : value;
}
