using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sig2Email.Core.Models;

namespace Sig2Email.Data;

/// <summary>Keys derived from the installation's master key (generated on first start, kept in the data directory).</summary>
public sealed class AppKeys
{
    public byte[] Signing { get; }
    /// <summary>Signs portal session tokens.</summary>
    public byte[] Session { get; }
    /// <summary>Encrypts the settings row, so a copy of the database alone does not reveal stored secrets.</summary>
    public byte[] Settings { get; }

    public AppKeys(byte[] master)
    {
        Signing = master;
        Session = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("sig2email/session/v1"));
        Settings = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("sig2email/settings/v1"));
    }
}

/// <summary>Which environment the process runs in; development-only behaviour is refused elsewhere.</summary>
public sealed record RuntimeEnvironment(bool IsDevelopment);

/// <summary>
/// Everything an administrator configures in the portal (Settings → System). Stored encrypted in the database and
/// picked up by all processes within a few seconds; nothing here comes from configuration files or environment variables.
/// </summary>
public class SystemSettings
{
    /// <summary>Public origin of the service (survey pages, tracked links, hosted images, the Outlook add-in).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";

    // Microsoft Entra app registration (directory sync, auto-replies, add-in sign-in). The directory id lives on the tenant.
    public string? EntraClientId { get; set; }
    public string? EntraClientSecret { get; set; }

    // SMTP receiver
    /// <summary>Smart-host name Exchange Online connects to; must match the TLS certificate.</summary>
    public string SmtpHostName { get; set; } = "";
    /// <summary>PKCS#12 file as uploaded (base64) and its password.</summary>
    public string? SmtpCertificatePfx { get; set; }
    public string? SmtpCertificatePassword { get; set; }
    public bool SmtpRequireTls { get; set; } = true;
    public bool SmtpRequireClientCertificate { get; set; } = true;
    public List<string> SmtpAllowedClientNames { get; set; } = new() { "mail.protection.outlook.com" };
    public int SmtpMaxMessageMb { get; set; } = 40;

    // Handing mail back to Exchange Online
    public int RelayPort { get; set; } = 25;
    public bool RelayRequireTls { get; set; } = true;
    /// <summary>Development/testing only: send everything to this host instead of the organisation's Exchange endpoint.</summary>
    public string? RelayOverrideHost { get; set; }
    /// <summary>"Header" (X-MS-Exchange-CrossTenant-Id, stamped by Exchange Online) or "SenderDomain" (development only).</summary>
    public string TenantIdentification { get; set; } = "Header";

    // Alert emails for negative ratings
    public string? AlertMailHost { get; set; }
    public int AlertMailPort { get; set; } = 587;
    public string? AlertMailUser { get; set; }
    public string? AlertMailPassword { get; set; }
    public string? AlertMailFrom { get; set; }

    // Background jobs
    public int SyncIntervalMinutes { get; set; } = 15;
    public int AutoReplyIntervalSeconds { get; set; } = 60;

    [JsonIgnore] public string BaseUrl => PublicBaseUrl.TrimEnd('/');
    [JsonIgnore] public bool EntraConfigured => !string.IsNullOrWhiteSpace(EntraClientId) && !string.IsNullOrWhiteSpace(EntraClientSecret);
    [JsonIgnore] public bool SenderDomainIdentification => TenantIdentification.Equals("SenderDomain", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Loads and saves <see cref="SystemSettings"/>. <see cref="Current"/> is at most a few seconds old in every process.</summary>
public sealed class SettingsProvider(IServiceScopeFactory scopes, AppKeys keys, ILogger<SettingsProvider> log)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SystemSettings _current = new();
    private long _version = -1;
    private long _checkedAt;

    /// <summary>The last loaded snapshot. Treat as read-only.</summary>
    public SystemSettings Current => _current;
    /// <summary>Changes whenever the settings change; cheap way for consumers to notice.</summary>
    public long Version => Interlocked.Read(ref _version);

    public async ValueTask<SystemSettings> GetAsync(CancellationToken ct = default)
    {
        if (_version >= 0 && Environment.TickCount64 - Interlocked.Read(ref _checkedAt) < MaxAge.TotalMilliseconds) return _current;
        await RefreshAsync(ct);
        return _current;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var version = await db.SystemSettings.Where(r => r.Id == 1).Select(r => (long?)r.Version).FirstOrDefaultAsync(ct) ?? 0;
            if (version != _version)
            {
                var data = version == 0 ? null : await db.SystemSettings.Where(r => r.Id == 1).Select(r => r.Data).FirstOrDefaultAsync(ct);
                _current = string.IsNullOrEmpty(data) ? new SystemSettings() : Unprotect(data);
                Interlocked.Exchange(ref _version, version);
            }
            Interlocked.Exchange(ref _checkedAt, Environment.TickCount64);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a change to the latest stored settings and saves it.</summary>
    public async Task<SystemSettings> UpdateAsync(Action<SystemSettings> change, CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            // Work on a copy so readers of Current never see a half-applied change.
            var next = JsonSerializer.Deserialize<SystemSettings>(JsonSerializer.Serialize(_current, JsonDefaults.Options), JsonDefaults.Options)!;
            change(next);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SystemSettings.FirstOrDefaultAsync(r => r.Id == 1, ct);
            if (row == null) db.SystemSettings.Add(row = new SystemSettingsRow { Id = 1 });
            row.Data = Protect(next);
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _current = next;
            Interlocked.Exchange(ref _version, row.Version);
            Interlocked.Exchange(ref _checkedAt, Environment.TickCount64);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Protect(SystemSettings settings)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings, JsonDefaults.Options);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(keys.Settings, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private SystemSettings Unprotect(string data)
    {
        try
        {
            if (!data.StartsWith("v1:", StringComparison.Ordinal)) throw new CryptographicException("Unknown settings format.");
            var raw = Convert.FromBase64String(data[3..]);
            var plain = new byte[raw.Length - 28];
            using var aes = new AesGcm(keys.Settings, 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return JsonSerializer.Deserialize<SystemSettings>(plain, JsonDefaults.Options) ?? new SystemSettings();
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            // Happens when the database is restored without the data directory's key file.
            log.LogError("Stored system settings cannot be decrypted with this installation's key ({Reason}); defaults are in effect until they are saved again", e.Message);
            return new SystemSettings();
        }
    }
}

/// <summary>Keeps <see cref="SettingsProvider.Current"/> fresh so changes made in the portal reach every process.</summary>
public sealed class SettingsRefresher(SettingsProvider settings, ILogger<SettingsRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await settings.GetAsync(stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Could not refresh system settings: {Message}", e.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stop).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}

/// <summary>Reads an uploaded PKCS#12 file: the certificate with the private key plus any intermediates to present.</summary>
public static class Pfx
{
    public static (System.Security.Cryptography.X509Certificates.X509Certificate2 Leaf,
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection Chain) Load(byte[] bytes, string? password)
    {
        var all = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        all.Import(bytes, password, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.DefaultKeySet);
        var leaf = all.FirstOrDefault(c => c.HasPrivateKey) ?? throw new CryptographicException("The file contains no certificate with a private key.");
        var chain = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        foreach (var c in all)
            if (!ReferenceEquals(c, leaf)) chain.Add(c);
        return (leaf, chain);
    }
}
