using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sig2Email.Core.Models;
using Sig2Email.Data;

namespace Sig2Email.Worker;

/// <summary>
/// Mirrors a tenant's Entra directory using Microsoft Graph delta queries: the first run walks everything, later
/// runs only fetch changes. Afterwards transitive group membership and the SMTP-address index are rebuilt for
/// the affected users and data-plane caches are invalidated.
/// </summary>
public sealed class DirectorySyncer(AppDbContext db, IGraphClient graph, IBlobStore blobs, RuntimeProvider runtime, ILogger<DirectorySyncer> log)
{
    private const string UserSelect =
        "id,userPrincipalName,mail,displayName,givenName,surname,jobTitle,department,companyName,officeLocation,businessPhones," +
        "mobilePhone,faxNumber,streetAddress,city,state,postalCode,country,accountEnabled,proxyAddresses,onPremisesExtensionAttributes,userType";
    private const string UsersDelta = "/users/delta?$select=" + UserSelect;
    private const string GroupsDelta = "/groups/delta?$select=id,displayName,mail,mailEnabled,securityEnabled,groupTypes,members";
    private const int MaxPhotosPerRun = 500;

    public async Task SyncAsync(Guid tenantId, CancellationToken ct)
    {
        db.SystemScope = true;
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        var tid = tenant.EntraTenantId;
        var touched = new HashSet<string>();       // user ids changed in this run
        var staleAddresses = new HashSet<string>(); // cache entries to drop

        // 1. users
        var usersLink = await WalkDeltaAsync(tid, tenant.UsersDeltaLink, UsersDelta, async page =>
        {
            var ids = page.Select(e => e.GetProperty("id").GetString()!).ToList();
            var existing = await db.Users.Where(u => u.TenantId == tenantId && ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
            foreach (var e in page)
            {
                var id = e.GetProperty("id").GetString()!;
                existing.TryGetValue(id, out var user);
                var guest = e.TryGetProperty("userType", out var ut) && ut.GetString() == "Guest";
                if (e.TryGetProperty("@removed", out _) || guest)
                {
                    if (user != null)
                    {
                        foreach (var a in user.Addresses) staleAddresses.Add(a);
                        db.Users.Remove(user);
                        if (user.PhotoBlobKey != null) await blobs.DeleteAsync(user.PhotoBlobKey, ct);
                    }
                    touched.Add(id);
                    continue;
                }
                if (user == null) db.Users.Add(user = new DirectoryUser { TenantId = tenantId, Id = id });
                foreach (var a in user.Addresses) staleAddresses.Add(a);
                MapUser(e, user);
                foreach (var a in user.Addresses) staleAddresses.Add(a);
                touched.Add(id);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }, ct);

        // 2. groups (a group's members may be spread over several pages)
        var groupsChanged = false;
        var groupsLink = await WalkDeltaAsync(tid, tenant.GroupsDeltaLink, GroupsDelta, async page =>
        {
            var ids = page.Select(e => e.GetProperty("id").GetString()!).Distinct().ToList();
            var existing = await db.Groups.Where(g => g.TenantId == tenantId && ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
            foreach (var e in page)
            {
                groupsChanged = true;
                var id = e.GetProperty("id").GetString()!;
                existing.TryGetValue(id, out var group);
                if (e.TryGetProperty("@removed", out _))
                {
                    if (group != null) { db.Groups.Remove(group); existing.Remove(id); }
                    continue;
                }
                if (group == null) db.Groups.Add(existing[id] = group = new DirectoryGroup { TenantId = tenantId, Id = id });
                MapGroup(e, group);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }, ct);

        // 3. managers – not part of the user delta feed, refreshed periodically
        var managersSynced = tenant.ManagersSyncedAt;
        if (managersSynced == null || managersSynced < DateTime.UtcNow.AddHours(-6))
        {
            await SyncManagersAsync(tenantId, tid, ct);
            managersSynced = DateTime.UtcNow;
        }

        // 4. derived data
        if (touched.Count > 0 || groupsChanged) await RebuildMembershipAsync(tenantId, staleAddresses, ct);
        if (touched.Count > 0) await RebuildAddressesAsync(tenantId, touched, ct);
        await SyncPhotosAsync(tenantId, tid, touched, staleAddresses, ct);
        db.ChangeTracker.Clear();

        // 5. accepted domains + sync state (the tenant row is reloaded: the paging above detaches everything)
        var domains = await ReadDomainsAsync(tenantId, tid, ct);
        tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        if (domains.Count > 0 && !domains.SetEquals(tenant.Domains))
        {
            tenant.Domains = domains.OrderBy(d => d).ToList();
            var known = await db.TenantDomains.Where(d => d.TenantId == tenantId).ToListAsync(ct);
            db.TenantDomains.RemoveRange(known.Where(k => !domains.Contains(k.Domain)));
            foreach (var d in domains.Where(d => known.All(k => k.Domain != d)))
                if (!await db.TenantDomains.AnyAsync(x => x.Domain == d, ct)) db.TenantDomains.Add(new TenantDomain { Domain = d, TenantId = tenantId });
        }
        tenant.UsersDeltaLink = usersLink;
        tenant.GroupsDeltaLink = groupsLink;
        tenant.ManagersSyncedAt = managersSynced;
        tenant.LastSyncAt = DateTime.UtcNow;
        tenant.LastSyncError = null;
        tenant.SyncRequested = false;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        if (staleAddresses.Count > 0) await runtime.InvalidateUsersAsync(tenantId, staleAddresses);
        await runtime.BumpAsync(tenantId, ct);
        log.LogInformation("Directory sync for tenant {Tenant}: {Users} user change(s), groups changed: {Groups}", tenantId, touched.Count, groupsChanged);
    }

    /// <summary>Follows nextLinks to the end and returns the new deltaLink. An expired delta token restarts a full sync.</summary>
    private async Task<string?> WalkDeltaAsync(string tid, string? deltaLink, string initial, Func<List<JsonElement>, Task> onPage, CancellationToken ct)
    {
        var url = deltaLink ?? initial;
        while (true)
        {
            JsonDocument doc;
            try
            {
                doc = await graph.GetAsync(tid, url, ct);
            }
            catch (GraphException e) when (e.Status == HttpStatusCode.Gone && url != initial)
            {
                log.LogWarning("Delta token expired; restarting full sync");
                url = initial;
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("value", out var value) && value.GetArrayLength() > 0) await onPage(value.EnumerateArray().ToList());
                if (root.TryGetProperty("@odata.nextLink", out var next)) { url = next.GetString()!; continue; }
                return root.TryGetProperty("@odata.deltaLink", out var delta) ? delta.GetString() : deltaLink;
            }
        }
    }

    // Delta items may omit unchanged properties, so only properties present in the payload are applied.
    internal static void MapUser(JsonElement e, DirectoryUser u)
    {
        string? Str(string name, string? current) =>
            e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : null) : current;

        u.Upn = Str("userPrincipalName", u.Upn) ?? u.Upn;
        u.Mail = Str("mail", u.Mail);
        u.DisplayName = Str("displayName", u.DisplayName) ?? "";
        u.FirstName = Str("givenName", u.FirstName);
        u.LastName = Str("surname", u.LastName);
        u.JobTitle = Str("jobTitle", u.JobTitle);
        u.Department = Str("department", u.Department);
        u.Company = Str("companyName", u.Company);
        u.Office = Str("officeLocation", u.Office);
        u.MobilePhone = Str("mobilePhone", u.MobilePhone);
        u.Fax = Str("faxNumber", u.Fax);
        u.Street = Str("streetAddress", u.Street);
        u.City = Str("city", u.City);
        u.State = Str("state", u.State);
        u.PostalCode = Str("postalCode", u.PostalCode);
        u.Country = Str("country", u.Country);
        if (e.TryGetProperty("accountEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            u.AccountEnabled = enabled.GetBoolean();
        if (e.TryGetProperty("businessPhones", out var phones) && phones.ValueKind == JsonValueKind.Array)
            u.Phone = phones.EnumerateArray().Select(p => p.GetString()).FirstOrDefault();
        if (e.TryGetProperty("onPremisesExtensionAttributes", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            var attrs = new Dictionary<string, string>();
            foreach (var p in ext.EnumerateObject())
                if (p.Name.StartsWith("extensionAttribute") && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } val)
                    attrs[p.Name["extensionAttribute".Length..]] = val;
            u.CustomAttributes = attrs;
        }

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("proxyAddresses", out var proxies) && proxies.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in proxies.EnumerateArray())
                if (p.GetString() is { } s && s.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)) addresses.Add(s[5..].ToLowerInvariant());
        }
        else foreach (var a in u.Addresses) addresses.Add(a);
        if (!string.IsNullOrEmpty(u.Mail)) addresses.Add(u.Mail.ToLowerInvariant());
        if (u.Upn.Contains('@') && !u.Upn.Contains("#EXT#")) addresses.Add(u.Upn.ToLowerInvariant());
        u.Addresses = addresses.OrderBy(a => a).ToList();
        u.UpdatedAt = DateTime.UtcNow;
    }

    internal static void MapGroup(JsonElement e, DirectoryGroup g)
    {
        if (e.TryGetProperty("displayName", out var name)) g.DisplayName = name.GetString() ?? "";
        if (e.TryGetProperty("mail", out var mail)) g.Mail = mail.ValueKind == JsonValueKind.String ? mail.GetString() : null;
        var unified = e.TryGetProperty("groupTypes", out var types) && types.ValueKind == JsonValueKind.Array &&
                      types.EnumerateArray().Any(t => t.GetString() == "Unified");
        if (e.TryGetProperty("groupTypes", out _) || e.TryGetProperty("securityEnabled", out _))
        {
            var security = e.TryGetProperty("securityEnabled", out var sec) && sec.ValueKind == JsonValueKind.True;
            g.Kind = unified ? GroupKind.Microsoft365 : security ? GroupKind.Security : GroupKind.Distribution;
        }
        if (e.TryGetProperty("members@delta", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<string>(g.MemberIds);
            foreach (var m in members.EnumerateArray())
            {
                var id = m.GetProperty("id").GetString()!;
                if (m.TryGetProperty("@removed", out _)) set.Remove(id);
                else set.Add(id);
            }
            g.MemberIds = set.ToList();
        }
    }

    private async Task SyncManagersAsync(Guid tenantId, string tid, CancellationToken ct)
    {
        var managers = new Dictionary<string, string?>();
        var url = "/users?$select=id&$expand=manager($select=id)&$top=999";
        while (url != null)
        {
            using var doc = await graph.GetAsync(tid, url, ct);
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
                managers[e.GetProperty("id").GetString()!] =
                    e.TryGetProperty("manager", out var m) && m.ValueKind == JsonValueKind.Object ? m.GetProperty("id").GetString() : null;
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        var users = await db.Users.Where(u => u.TenantId == tenantId).ToListAsync(ct);
        foreach (var u in users)
            if (managers.TryGetValue(u.Id, out var managerId) && u.ManagerId != managerId) u.ManagerId = managerId;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<HashSet<string>> ReadDomainsAsync(Guid tenantId, string tid, CancellationToken ct)
    {
        var domains = new HashSet<string>();
        try
        {
            using var doc = await graph.GetAsync(tid, "/organization?$select=verifiedDomains", ct);
            foreach (var org in doc.RootElement.GetProperty("value").EnumerateArray())
                if (org.TryGetProperty("verifiedDomains", out var list))
                    foreach (var d in list.EnumerateArray())
                        if (d.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) domains.Add(name.ToLowerInvariant());
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.Forbidden)
        {
            // Organization.Read.All not granted: fall back to the domains seen on mailboxes.
            log.LogWarning("Cannot read /organization (Organization.Read.All missing); deriving domains from user addresses");
            var all = await db.Users.Where(u => u.TenantId == tenantId).Select(u => u.Addresses).ToListAsync(ct);
            foreach (var a in all.SelectMany(x => x))
            {
                var at = a.LastIndexOf('@');
                if (at > 0) domains.Add(a[(at + 1)..]);
            }
        }
        return domains;
    }

    /// <summary>Recomputes each user's transitive group membership (nested groups included).</summary>
    private async Task RebuildMembershipAsync(Guid tenantId, HashSet<string> staleAddresses, CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => g.TenantId == tenantId).ToListAsync(ct);
        var groupIds = groups.Select(g => g.Id).ToHashSet();
        var parents = new Dictionary<string, List<string>>(); // member id → groups that directly contain it
        foreach (var g in groups)
            foreach (var m in g.MemberIds)
            {
                if (!parents.TryGetValue(m, out var list)) parents[m] = list = new List<string>();
                list.Add(g.Id);
            }

        List<string> Closure(string userId)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>(parents.GetValueOrDefault(userId) ?? new());
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                if (!groupIds.Contains(g) || !seen.Add(g)) continue;
                if (parents.TryGetValue(g, out var up)) foreach (var p in up) stack.Push(p);
            }
            return seen.OrderBy(x => x).ToList();
        }

        var users = await db.Users.Where(u => u.TenantId == tenantId).ToListAsync(ct);
        foreach (var u in users)
        {
            var closure = Closure(u.Id);
            if (closure.SequenceEqual(u.GroupIds)) continue;
            u.GroupIds = closure;
            foreach (var a in u.Addresses) staleAddresses.Add(a);
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RebuildAddressesAsync(Guid tenantId, HashSet<string> userIds, CancellationToken ct)
    {
        foreach (var chunk in userIds.Chunk(500))
        {
            var ids = chunk.ToList();
            await db.Addresses.Where(a => a.TenantId == tenantId && ids.Contains(a.UserId)).ExecuteDeleteAsync(ct);
            var users = await db.Users.AsNoTracking().Where(u => u.TenantId == tenantId && ids.Contains(u.Id)).Select(u => new { u.Id, u.Addresses }).ToListAsync(ct);
            var wanted = users.SelectMany(u => u.Addresses.Select(a => (a, u.Id))).ToList();
            var addresses = wanted.Select(w => w.a).ToList();
            var taken = await db.Addresses.Where(a => a.TenantId == tenantId && addresses.Contains(a.Address)).Select(a => a.Address).ToListAsync(ct);
            foreach (var (address, userId) in wanted.Where(w => !taken.Contains(w.a)).DistinctBy(w => w.a))
                db.Addresses.Add(new DirectoryAddress { TenantId = tenantId, Address = address, UserId = userId });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SyncPhotosAsync(Guid tenantId, string tid, HashSet<string> touched, HashSet<string> staleAddresses, CancellationToken ct)
    {
        // Users changed in this run, plus a slice of those never checked (spreads the initial load over several runs).
        var touchedList = touched.Take(MaxPhotosPerRun).ToList();
        var users = await db.Users.Where(u => u.TenantId == tenantId && (touchedList.Contains(u.Id) || u.PhotoVersion == null))
            .OrderBy(u => u.Id).Take(MaxPhotosPerRun).ToListAsync(ct);
        foreach (var u in users)
        {
            try
            {
                var photo = await graph.GetBytesAsync(tid, $"/users/{u.Id}/photos/120x120/$value", ct);
                var before = u.PhotoVersion;
                if (photo == null)
                {
                    if (u.PhotoBlobKey != null) await blobs.DeleteAsync(u.PhotoBlobKey, ct);
                    u.PhotoBlobKey = null;
                    u.PhotoVersion = "none";
                }
                else
                {
                    u.PhotoBlobKey = BlobKeys.Photo(tenantId, u.Id);
                    await blobs.PutAsync(u.PhotoBlobKey, photo.Value.Bytes, photo.Value.ContentType, ct);
                    u.PhotoVersion = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(photo.Value.Bytes), 0, 6);
                }
                if (before != u.PhotoVersion) foreach (var a in u.Addresses) staleAddresses.Add(a);
            }
            catch (GraphException e)
            {
                log.LogDebug("Photo for {User} unavailable: {Message}", u.Id, e.Message);
                u.PhotoVersion ??= "none";
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
