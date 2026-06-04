using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Lyrictified.Server;

public sealed class AdminAuthService
{
    private const int TokenBytes = 32;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly string? _passwordVerifier;
    private readonly ILogger<AdminAuthService> _logger;

    public AdminAuthService(LyrictifiedSettings settings, ILogger<AdminAuthService> logger)
    {
        _logger = logger;
        _passwordVerifier = ResolvePasswordVerifier(settings);
    }

    public bool VerifyPassword(string password)
    {
        if (_passwordVerifier is null || password.Length > 1024)
        {
            return false;
        }

        if (AdminPasswordHasher.IsPasswordHash(_passwordVerifier))
        {
            return AdminPasswordHasher.Verify(password, _passwordVerifier);
        }

        _logger.LogWarning("AdminPassword is using legacy plaintext verification. Replace it with Lyrictified:AdminPasswordHash.");
        return FixedTimeEquals(password, _passwordVerifier);
    }

    public string CreateSessionToken(DateTimeOffset expiresAt)
    {
        Span<byte> tokenBytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Base64UrlEncode(tokenBytes);
        _sessions[HashToken(token)] = expiresAt;
        RemoveExpiredSessions(DateTimeOffset.UtcNow);
        return token;
    }

    public bool IsValidSessionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashToken(token);
        if (!_sessions.TryGetValue(tokenHash, out var expiresAt))
        {
            return false;
        }

        if (expiresAt <= now)
        {
            _sessions.TryRemove(tokenHash, out _);
            return false;
        }

        return true;
    }

    public void RevokeSessionToken(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _sessions.TryRemove(HashToken(token), out _);
        }
    }

    private string? ResolvePasswordVerifier(LyrictifiedSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AdminPasswordHash))
        {
            if (!AdminPasswordHasher.IsPasswordHash(settings.AdminPasswordHash))
            {
                throw new InvalidOperationException("Lyrictified:AdminPasswordHash is not a supported password hash.");
            }

            return settings.AdminPasswordHash;
        }

        if (AdminPasswordHasher.IsPasswordHash(settings.AdminPassword))
        {
            _logger.LogWarning("Lyrictified:AdminPassword contains a password hash. Move it to Lyrictified:AdminPasswordHash.");
            return settings.AdminPassword;
        }

        if (string.IsNullOrWhiteSpace(settings.AdminPassword) || settings.AdminPassword == "change-me")
        {
            _logger.LogWarning(
                "Admin login is disabled. Set Lyrictified:AdminPasswordHash to enable it. Generate one with: dotnet run -- --hash-admin-password");
            return null;
        }

        _logger.LogWarning(
            "Lyrictified:AdminPassword is plaintext. Generate a hash with 'dotnet run -- --hash-admin-password' and store it in Lyrictified:AdminPasswordHash.");
        return settings.AdminPassword;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void RemoveExpiredSessions(DateTimeOffset now)
    {
        foreach (var session in _sessions)
        {
            if (session.Value <= now)
            {
                _sessions.TryRemove(session.Key, out _);
            }
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public static class AdminPasswordHasher
{
    private const string Prefix = "lyrictified-pbkdf2-sha256";
    private const int SaltBytes = 32;
    private const int KeyBytes = 32;
    private const int CurrentIterations = 600_000;
    private const int MinimumIterations = 310_000;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        Span<byte> salt = stackalloc byte[SaltBytes];
        RandomNumberGenerator.Fill(salt);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            CurrentIterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

        return string.Join('$', Prefix, CurrentIterations, Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    public static bool Verify(string password, string verifier)
    {
        if (!TryParse(verifier, out var iterations, out var salt, out var expected)
            || iterations < MinimumIterations)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool IsPasswordHash(string? value)
    {
        return TryParse(value, out var iterations, out _, out _) && iterations >= MinimumIterations;
    }

    private static bool TryParse(
        string? value,
        out int iterations,
        out byte[] salt,
        out byte[] key)
    {
        iterations = 0;
        salt = [];
        key = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out iterations))
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 16 && key.Length >= 32;
    }
}
