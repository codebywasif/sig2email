using System.Security.Cryptography;
using System.Text;

namespace Sig2Email.Core.Security;

/// <summary>HMAC-SHA256 signing for public, unauthenticated links (survey ratings, tracked links, QR images).</summary>
public sealed class TokenSigner
{
    private readonly byte[] _key;

    public TokenSigner(byte[] key)
    {
        if (key.Length < 32) throw new ArgumentException("Signing key must be at least 32 bytes.", nameof(key));
        _key = key;
    }

    public string Mac(string data)
    {
        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data));
        return B64(hash.AsSpan(0, 16));
    }

    public bool Verify(string data, string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return false;
        var expected = Encoding.ASCII.GetBytes(Mac(data));
        var actual = Encoding.ASCII.GetBytes(mac);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Returns a self-contained signed token carrying <paramref name="payload"/>.</summary>
    public string Pack(string payload) => B64(Encoding.UTF8.GetBytes(payload)) + "." + Mac(payload);

    public string? Unpack(string token)
    {
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return null;
        try
        {
            var payload = Encoding.UTF8.GetString(UnB64(token[..dot]));
            return Verify(payload, token[(dot + 1)..]) ? payload : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string B64(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
