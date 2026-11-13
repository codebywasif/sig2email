using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Sig2Email.Data;

/// <summary>
/// Start-up wiring only: where the database, cache and files live. Everything an administrator would want to change
/// (public URL, Microsoft Entra app, SMTP, certificates, alert mail) is stored in the database and edited in the portal.
/// </summary>
public class Sig2EmailOptions
{
    public const string Section = "Sig2Email";

    /// <summary>Folder for local state (SQLite file, file blob store, the installation key).</summary>
    public string? DataDir { get; set; }
    public string DatabaseProvider { get; set; } = "sqlite";
    public string? ConnectionString { get; set; }
    public bool AutoMigrate { get; set; } = true;
    /// <summary>Development only: create the demo organisation and demo accounts.</summary>
    public bool SeedDemoData { get; set; }
    /// <summary>StackExchange.Redis connection string; empty → in-process cache.</summary>
    public string? Redis { get; set; }
    public string BlobProvider { get; set; } = "file";
    public string? BlobConnectionString { get; set; }
    public string BlobContainer { get; set; } = "sig2email";
    /// <summary>Folder with the built Outlook add-in; found automatically when empty.</summary>
    public string? AddinDir { get; set; }
    /// <summary>Optional override of the installation key (32+ characters). Normally generated and kept in the data directory.</summary>
    public string? SigningKey { get; set; }

    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(DataDir)) return Directory.CreateDirectory(DataDir).FullName;
        return Directory.CreateDirectory(Path.Combine(SolutionRoot() ?? Directory.GetCurrentDirectory(), ".data")).FullName;
    }

    public static string? SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Sig2Email.sln"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>
    /// The installation's master key. Generated on first start and stored in the data directory, which all processes
    /// share. Keep that file with your backups: it signs links in emails already sent and decrypts stored settings.
    /// </summary>
    public byte[] ResolveSigningKey()
    {
        if (!string.IsNullOrWhiteSpace(SigningKey))
        {
            if (SigningKey.Length < 32) throw new InvalidOperationException("Sig2Email:SigningKey must be at least 32 characters.");
            return Encoding.UTF8.GetBytes(SigningKey);
        }
        var file = Path.Combine(ResolveDataDir(), "installation.key");
        if (!File.Exists(file))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
            }
            catch (IOException) { /* another process created it first */ }
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = File.ReadAllText(file).Trim();
                if (text.Length >= 32) return Encoding.UTF8.GetBytes(text);
            }
            catch (IOException) when (attempt < 20) { /* still being written by another process */ }
            if (attempt >= 20) throw new InvalidOperationException($"The installation key file {file} is empty or unreadable.");
            Thread.Sleep(100);
        }
    }
}

// ---------- cache ----------

public interface ICacheStore
{
    /// <summary>True when all nodes see the same cache (Redis); false for the per-process fallback.</summary>
    bool IsShared { get; }
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, TimeSpan ttl);
    Task RemoveAsync(IEnumerable<string> keys);
}

public sealed class MemoryCacheStore(IMemoryCache cache) : ICacheStore
{
    public bool IsShared => false;
    public Task<string?> GetAsync(string key) => Task.FromResult(cache.Get<string>(key));
    public Task SetAsync(string key, string value, TimeSpan ttl) { cache.Set(key, value, ttl); return Task.CompletedTask; }
    public Task RemoveAsync(IEnumerable<string> keys) { foreach (var k in keys) cache.Remove(k); return Task.CompletedTask; }
}

public sealed class RedisCacheStore(IConnectionMultiplexer redis) : ICacheStore
{
    private const string Prefix = "s2e:";
    public bool IsShared => true;

    public async Task<string?> GetAsync(string key)
    {
        var v = await redis.GetDatabase().StringGetAsync(Prefix + key);
        return v.HasValue ? v.ToString() : null;
    }

    public Task SetAsync(string key, string value, TimeSpan ttl) => redis.GetDatabase().StringSetAsync(Prefix + key, value, ttl);

    public Task RemoveAsync(IEnumerable<string> keys) =>
        redis.GetDatabase().KeyDeleteAsync(keys.Select(k => (RedisKey)(Prefix + k)).ToArray());
}

// ---------- blobs ----------

public sealed record BlobData(byte[] Bytes, string ContentType);

public interface IBlobStore
{
    Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default);
    Task<BlobData?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public static class BlobKeys
{
    public static string Asset(Guid tenantId, Guid assetId) => $"assets/{tenantId:N}/{assetId:N}";
    public static string Photo(Guid tenantId, string userId) => $"photos/{tenantId:N}/{Safe(userId)}";
    private static string Safe(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}

public sealed class FileBlobStore(string root) : IBlobStore
{
    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid blob key.");
        return full;
    }

    public async Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);
        await File.WriteAllTextAsync(path + ".ct", contentType, ct);
    }

    public async Task<BlobData?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var type = File.Exists(path + ".ct") ? await File.ReadAllTextAsync(path + ".ct", ct) : "application/octet-stream";
        return new BlobData(await File.ReadAllBytesAsync(path, ct), type);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        File.Delete(path);
        File.Delete(path + ".ct");
        return Task.CompletedTask;
    }
}

public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly ConcurrentDictionary<int, Task> _init = new();

    public AzureBlobStore(string connectionString, string container) => _container = new BlobContainerClient(connectionString, container);

    private Task Ready() => _init.GetOrAdd(0, _ => _container.CreateIfNotExistsAsync());

    public async Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
    {
        await Ready();
        await _container.GetBlobClient(key).UploadAsync(new BinaryData(data),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<BlobData?> GetAsync(string key, CancellationToken ct = default)
    {
        await Ready();
        try
        {
            var r = await _container.GetBlobClient(key).DownloadContentAsync(ct);
            return new BlobData(r.Value.Content.ToArray(), r.Value.Details.ContentType ?? "application/octet-stream");
        }
        catch (Azure.RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await Ready();
        await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
    }
}
