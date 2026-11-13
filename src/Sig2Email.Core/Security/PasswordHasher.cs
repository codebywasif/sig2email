using System.Security.Cryptography;

namespace Sig2Email.Core.Security;

/// <summary>PBKDF2-HMAC-SHA256 password hashing. Stored as "pbkdf2-sha256$iterations$salt$hash" (base64).</summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    public const int Iterations = 600_000;
    public const int MinLength = 10;
    public const int MaxLength = 200;

    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Returns a user-facing problem with the proposed password, or null when it is acceptable.</summary>
    public static string? Problem(string? password, string? username = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"The password must be at least {MinLength} characters long.";
        if (password.Length > MaxLength) return $"The password must be at most {MaxLength} characters long.";
        if (password.Distinct().Count() < 5) return "The password is too repetitive.";
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "The password must not contain the user name.";
        return null;
    }
}
