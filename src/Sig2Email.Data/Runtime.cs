using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;

namespace Sig2Email.Data;

public sealed record RuntimeRule(Rule Rule, CompiledTemplate Template, ImageMode ImageMode);

/// <summary>Immutable, pre-compiled view of everything the mail path needs for one tenant.</summary>
public sealed class TenantRuntime
{
    public required Tenant Tenant { get; init; }
    public required IReadOnlySet<string> Domains { get; init; }
    /// <summary>Enabled rules with a published template, in evaluation order.</summary>
    public required IReadOnlyList<RuntimeRule> Rules { get; init; }
    public required IReadOnlyDictionary<Guid, Survey> Surveys { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Low-latency lookups for mail transit: compiled per-tenant rule sets (reloaded when the tenant's config
/// version changes) and directory users by SMTP address (cache-aside over Redis or process memory).
/// </summary>
public sealed class RuntimeProvider(IServiceScopeFactory scopes, ICacheStore cache, IMemoryCache local)
{
    private sealed record Entry(TenantRuntime Runtime, DateTime CheckedAt);

    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<Guid, Entry> _runtimes = new();

    private TimeSpan VersionTtl => cache.IsShared ? TimeSpan.FromMinutes(10) : RecheckInterval;
    private TimeSpan UserTtl => cache.IsShared ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30);

    private AppDbContext Db(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        return db;
    }

    public async Task<TenantRuntime?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        _runtimes.TryGetValue(tenantId, out var entry);
        if (entry != null && DateTime.UtcNow - entry.CheckedAt < RecheckInterval) return entry.Runtime;

        long version;
        var cached = await cache.GetAsync($"ver:{tenantId:N}");
        if (cached != null) version = long.Parse(cached);
        else
        {
            using var scope = scopes.CreateScope();
            var v = await Db(scope).Tenants.Where(t => t.Id == tenantId).Select(t => (long?)t.ConfigVersion).FirstOrDefaultAsync(ct);
            if (v == null) return null;
            version = v.Value;
            await cache.SetAsync($"ver:{tenantId:N}", version.ToString(), VersionTtl);
        }

        if (entry != null && entry.Runtime.Version == version)
        {
            _runtimes[tenantId] = entry with { CheckedAt = DateTime.UtcNow };
            return entry.Runtime;
        }

        var runtime = await BuildAsync(tenantId, ct);
        if (runtime == null) return null;
        _runtimes[tenantId] = new Entry(runtime, DateTime.UtcNow);
        return runtime;
    }

    private async Task<TenantRuntime?> BuildAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant == null) return null;

        var rules = await db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId && r.Enabled).OrderBy(r => r.Priority).ToListAsync(ct);
        var templateIds = rules.Select(r => r.TemplateId).Distinct().ToList();
        var published = await (
            from t in db.Templates.AsNoTracking()
            join v in db.TemplateVersions.AsNoTracking() on new { TemplateId = t.Id, Version = t.PublishedVersion } equals new { v.TemplateId, Version = (int?)v.Version }
            where t.TenantId == tenantId && templateIds.Contains(t.Id)
            select v).ToListAsync(ct);
        var byTemplate = published.ToDictionary(v => v.TemplateId);
        var surveys = await db.Surveys.AsNoTracking().Where(s => s.TenantId == tenantId && s.Enabled).ToListAsync(ct);

        return new TenantRuntime
        {
            Tenant = tenant,
            Domains = tenant.Domains.Select(d => d.ToLowerInvariant()).ToHashSet(),
            Rules = rules.Where(r => byTemplate.ContainsKey(r.TemplateId))
                .Select(r => new RuntimeRule(r, CompiledTemplate.Parse(byTemplate[r.TemplateId].Html), byTemplate[r.TemplateId].ImageMode))
                .ToList(),
            Surveys = surveys.ToDictionary(s => s.Id),
            Version = tenant.ConfigVersion,
        };
    }

    /// <summary>Call after any change that affects signatures or rules so data-plane nodes pick it up.</summary>
    public async Task BumpAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ConfigVersion, t => t.ConfigVersion + 1), ct);
        var v = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.ConfigVersion).FirstOrDefaultAsync(ct);
        await cache.SetAsync($"ver:{tenantId:N}", v.ToString(), VersionTtl);
        _runtimes.TryRemove(tenantId, out _);
    }

    public Task<Guid?> ResolveTenantByEntraIdAsync(string entraTenantId, CancellationToken ct = default) =>
        local.GetOrCreateAsync($"entra:{entraTenantId.ToLowerInvariant()}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            using var scope = scopes.CreateScope();
            var id = await Db(scope).Tenants.Where(t => t.EntraTenantId == entraTenantId).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
            return id;
        });

    public Task<Guid?> ResolveTenantByDomainAsync(string domain, CancellationToken ct = default)
    {
        domain = domain.ToLowerInvariant();
        return local.GetOrCreateAsync($"dom:{domain}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            using var scope = scopes.CreateScope();
            var id = await Db(scope).TenantDomains.Where(t => t.Domain == domain).Select(t => (Guid?)t.TenantId).FirstOrDefaultAsync(ct);
            return id;
        });
    }

    public async Task<DirectoryUser?> GetUserByAddressAsync(Guid tenantId, string address, CancellationToken ct = default)
    {
        address = address.Trim().ToLowerInvariant();
        var key = $"u:{tenantId:N}:{address}";
        var cached = await cache.GetAsync(key);
        if (cached == "-") return null;
        if (cached != null) return JsonSerializer.Deserialize<DirectoryUser>(cached, JsonDefaults.Options);

        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var user = await (
            from a in db.Addresses.AsNoTracking()
            join u in db.Users.AsNoTracking() on new { a.TenantId, Id = a.UserId } equals new { u.TenantId, u.Id }
            where a.TenantId == tenantId && a.Address == address
            select u).FirstOrDefaultAsync(ct);
        await cache.SetAsync(key, user == null ? "-" : JsonSerializer.Serialize(user, JsonDefaults.Options), UserTtl);
        return user;
    }

    public Task InvalidateUsersAsync(Guid tenantId, IEnumerable<string> addresses) =>
        cache.RemoveAsync(addresses.Select(a => $"u:{tenantId:N}:{a.Trim().ToLowerInvariant()}"));
}
