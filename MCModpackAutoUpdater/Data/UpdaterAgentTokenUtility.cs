using System.Security.Cryptography;
using System.Text;

namespace MCModpackAutoUpdater.Data;

public static class UpdaterAgentTokenUtility
{
    public static string GenerateToken()
    {
        return $"mcagt_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}
