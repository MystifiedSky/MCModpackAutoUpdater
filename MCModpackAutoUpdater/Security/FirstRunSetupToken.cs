using System.Security.Cryptography;

namespace MCModpackAutoUpdater.Security;

public sealed class FirstRunSetupToken
{
    private readonly object _lock = new();
    private string? _token;

    public string EnsureToken()
    {
        lock (_lock)
        {
            _token ??= GenerateToken();
            return _token;
        }
    }

    public bool Validate(string? token)
    {
        lock (_lock)
        {
            return !string.IsNullOrWhiteSpace(_token) &&
                   string.Equals(_token, token?.Trim(), StringComparison.Ordinal);
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _token = null;
        }
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        var text = Convert.ToHexString(bytes);
        return string.Join('-', text.Chunk(6).Select(static chunk => new string(chunk)));
    }
}
