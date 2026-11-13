using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sig2Email.Core.Models;
using Sig2Email.Data;
using Sig2Email.Worker;

namespace Sig2Email.Tests;

/// <summary>Scripted stand-in for Microsoft Graph.</summary>
internal sealed class FakeGraph : IGraphClient
{
    public Dictionary<string, Func<string>> Gets { get; } = new();
    public Dictionary<string, byte[]> Photos { get; } = new();
    public List<(string Method, string Path, string Body)> Sent { get; } = new();
    public bool IsConfigured => true;

    public Task<JsonDocument> GetAsync(string entraTenantId, string urlOrPath, CancellationToken ct)
    {
        if (!Gets.TryGetValue(urlOrPath, out var response)) throw new GraphException(HttpStatusCode.NotFound, "No scripted response for " + urlOrPath);
        return Task.FromResult(JsonDocument.Parse(response()));
    }

    public Task<(byte[] Bytes, string ContentType)?> GetBytesAsync(string entraTenantId, string path, CancellationToken ct)
    {
        var id = path.Split('/')[2];
        return Task.FromResult<(byte[], string)?>(Photos.TryGetValue(id, out var bytes) ? (bytes, "image/jpeg") : null);
    }

    public Task<JsonDocument?> SendAsync(string entraTenantId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        Sent.Add((method.Method, path, body == null ? "" : JsonSerializer.Serialize(body)));
        return Task.FromResult(method == HttpMethod.Post ? JsonDocument.Parse("{\"id\":\"rule-1\"}") : null);
    }
}

public sealed class WorkerFixture : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s2e-worker-" + Guid.NewGuid().ToString("N"));
    public ServiceProvider Services { get; private set; } = null!;
    internal FakeGraph Graph { get; } = new();

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sig2Email:DataDir"] = _dir }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSig2EmailData(config, new DevEnvironment());
        services.AddSingleton<IGraphClient>(Graph);
        services.AddScoped<DirectorySyncer>();
        services.AddScoped<AutoReplyApplier>();
        Services = services.BuildServiceProvider();
        await Services.InitializeDatabaseAsync(schemaOwner: true);
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public async Task<T> ScopeAsync<T>(Func<IServiceProvider, AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        return await action(scope.ServiceProvider, db);
    }
}

public class WorkerTests(WorkerFixture fx) : IClassFixture<WorkerFixture>
{
    private const string Tid = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string UsersInitial = "/users/delta?$select=id,userPrincipalName,mail,displayName,givenName,surname,jobTitle,department,companyName,officeLocation,businessPhones,mobilePhone,faxNumber,streetAddress,city,state,postalCode,country,accountEnabled,proxyAddresses,onPremisesExtensionAttributes,userType";
    private const string GroupsInitial = "/groups/delta?$select=id,displayName,mail,mailEnabled,securityEnabled,groupTypes,members";
    private const string Managers = "/users?$select=id&$expand=manager($select=id)&$top=999";

    private static string User(string id, string name, string dept, string extra = "") =>
        $$"""{"id":"{{id}}","userPrincipalName":"{{name}}@fabrikam.example","mail":"{{name}}@fabrikam.example","displayName":"{{name}}","department":"{{dept}}","jobTitle":"Engineer","accountEnabled":true,"businessPhones":["+1 555 0100"],"userType":"Member"{{extra}}}""";

    [Fact]
    public async Task Directory_delta_sync_then_auto_reply_reconciliation()
    {
        var g = fx.Graph;
        var tenantId = await fx.ScopeAsync(async (_, db) =>
        {
            var t = new Tenant { EntraTenantId = Tid, Name = "Fabrikam", Region = "local", ConsentGranted = true };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            return t.Id;
        });

        // ---------- initial full sync (paged) ----------
        g.Gets[UsersInitial] = () => $$"""
            {"value":[
              {{User("u1", "ann", "Sales", ""","proxyAddresses":["SMTP:ann@fabrikam.example","smtp:Ann.Alias@fabrikam.example","SIP:ann@fabrikam.example"],"onPremisesExtensionAttributes":{"extensionAttribute1":"EMEA","extensionAttribute2":null}""")}},
              {{User("u2", "bob", "Support")}}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=p2"}
            """;
        g.Gets["https://graph.microsoft.com/v1.0/users/delta?$skiptoken=p2"] = () => $$"""
            {"value":[
              {"id":"u3","userPrincipalName":"guest_x.com#EXT#@fabrikam.example","displayName":"Guest","userType":"Guest"},
              {{User("u4", "dan", "Support")}}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=U1"}
            """;
        g.Gets[GroupsInitial] = () => """
            {"value":[
              {"id":"g1","displayName":"All Support","mail":"support@fabrikam.example","securityEnabled":false,"groupTypes":["Unified"],
               "members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u1"},{"@odata.type":"#microsoft.graph.group","id":"g2"}]},
              {"id":"g2","displayName":"Tier 2","mail":null,"securityEnabled":true,"groupTypes":[],
               "members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u2"}]}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1"}
            """;
        g.Gets[Managers] = () => """{"value":[{"id":"u1"},{"id":"u2","manager":{"id":"u1"}},{"id":"u4","manager":{"id":"u1"}}]}""";
        g.Gets["/organization?$select=verifiedDomains"] = () => """{"value":[{"verifiedDomains":[{"name":"Fabrikam.example","isDefault":true},{"name":"fabrikam.onmicrosoft.com"}]}]}""";
        g.Photos["u1"] = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2 };

        await fx.ScopeAsync(async (sp, _) => { await sp.GetRequiredService<DirectorySyncer>().SyncAsync(tenantId, default); return 0; });

        await fx.ScopeAsync(async (sp, db) =>
        {
            var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
            Assert.Equal(new[] { "u1", "u2", "u4" }, users.Select(u => u.Id)); // guest skipped
            var ann = users[0];
            Assert.Equal(new[] { "ann.alias@fabrikam.example", "ann@fabrikam.example" }, ann.Addresses);
            Assert.Equal("EMEA", ann.CustomAttributes["1"]);
            Assert.False(ann.CustomAttributes.ContainsKey("2"));
            Assert.Equal("+1 555 0100", ann.Phone);
            Assert.Equal(new[] { "g1" }, ann.GroupIds);
            Assert.Equal(new[] { "g1", "g2" }, users[1].GroupIds);            // nested membership is transitive
            Assert.Empty(users[2].GroupIds);
            Assert.Equal("u1", users[1].ManagerId);
            Assert.NotNull(ann.PhotoBlobKey);
            Assert.Equal("none", users[1].PhotoVersion);
            Assert.NotNull(await sp.GetRequiredService<IBlobStore>().GetAsync(ann.PhotoBlobKey!));

            var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
            Assert.Equal(new[] { "fabrikam.example", "fabrikam.onmicrosoft.com" }, tenant.Domains);
            Assert.EndsWith("deltatoken=U1", tenant.UsersDeltaLink);
            Assert.EndsWith("deltatoken=G1", tenant.GroupsDeltaLink);
            Assert.NotNull(tenant.LastSyncAt);
            Assert.True(tenant.ConfigVersion > 1);
            Assert.Equal(GroupKind.Microsoft365, (await db.Groups.FirstAsync(x => x.Id == "g1")).Kind);
            Assert.Equal(GroupKind.Security, (await db.Groups.FirstAsync(x => x.Id == "g2")).Kind);

            // Mail-path lookups: by alias, and tenant by verified domain.
            var runtime = sp.GetRequiredService<RuntimeProvider>();
            Assert.Equal("u1", (await runtime.GetUserByAddressAsync(tenantId, "ANN.alias@fabrikam.example"))!.Id);
            Assert.Equal(tenantId, await runtime.ResolveTenantByDomainAsync("fabrikam.example"));
            return 0;
        });

        // ---------- incremental sync: partial update, removal, membership change ----------
        g.Gets["https://graph.microsoft.com/v1.0/users/delta?$deltatoken=U1"] = () => """
            {"value":[{"id":"u2","jobTitle":"Team Lead"},{"id":"u4","@removed":{"reason":"deleted"}}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=U2"}
            """;
        g.Gets["https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1"] = () => """
            {"value":[{"id":"g1","members@delta":[{"@odata.type":"#microsoft.graph.group","id":"g2","@removed":{"reason":"deleted"}}]}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G2"}
            """;
        await fx.ScopeAsync(async (sp, _) => { await sp.GetRequiredService<DirectorySyncer>().SyncAsync(tenantId, default); return 0; });

        await fx.ScopeAsync(async (sp, db) =>
        {
            var bob = await db.Users.FirstAsync(u => u.Id == "u2");
            Assert.Equal("Team Lead", bob.JobTitle);
            Assert.Equal("Support", bob.Department);                 // untouched properties survive a partial delta
            Assert.Equal(new[] { "bob@fabrikam.example" }, bob.Addresses);
            Assert.Equal(new[] { "g2" }, bob.GroupIds);
            Assert.False(await db.Users.AnyAsync(u => u.Id == "u4"));
            Assert.False(await db.Addresses.AnyAsync(a => a.UserId == "u4"));
            Assert.Equal(new[] { "u1" }, (await db.Groups.FirstAsync(x => x.Id == "g1")).MemberIds);
            Assert.Null(await sp.GetRequiredService<RuntimeProvider>().GetUserByAddressAsync(tenantId, "dan@fabrikam.example"));
            Assert.EndsWith("deltatoken=U2", (await db.Tenants.FirstAsync(t => t.Id == tenantId)).UsersDeltaLink);
            return 0;
        });

        // ---------- expired delta token → transparent full resync ----------
        g.Gets["https://graph.microsoft.com/v1.0/users/delta?$deltatoken=U2"] = () => throw new GraphException(HttpStatusCode.Gone, "resync required");
        g.Gets["https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G2"] = () => """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G3"}""";
        await fx.ScopeAsync(async (sp, _) => { await sp.GetRequiredService<DirectorySyncer>().SyncAsync(tenantId, default); return 0; });
        Assert.EndsWith("deltatoken=U1", await fx.ScopeAsync((_, db) => db.Tenants.Where(t => t.Id == tenantId).Select(t => t.UsersDeltaLink!).FirstAsync()));

        // ---------- auto-replies ----------
        var policyId = await fx.ScopeAsync(async (_, db) =>
        {
            var p = new AutoReplyPolicy
            {
                TenantId = tenantId, Name = "Tier 2 offsite", GroupIds = { "g2" }, StartUtc = DateTime.UtcNow.AddHours(-1), EndUtc = DateTime.UtcNow.AddHours(5),
                InternalMessage = "<p>Offsite</p>", ExternalMessage = "<p>We are away</p>", ExternalAudience = ExternalAudience.ContactsOnly,
                ForwardTo = "ann@fabrikam.example",
            };
            db.AutoReplyPolicies.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        });
        Task Run() => fx.ScopeAsync(async (sp, _) => { await sp.GetRequiredService<AutoReplyApplier>().RunAsync(tenantId, Tid, default); return 0; });

        g.Sent.Clear();
        await Run();
        Assert.Equal(2, g.Sent.Count);
        Assert.Equal(("PATCH", "/users/u2/mailboxSettings"), (g.Sent[0].Method, g.Sent[0].Path));
        Assert.Contains("\"status\":\"scheduled\"", g.Sent[0].Body);
        Assert.Contains("\"externalAudience\":\"contactsOnly\"", g.Sent[0].Body);
        Assert.Contains("\"timeZone\":\"UTC\"", g.Sent[0].Body);
        Assert.Equal(("POST", "/users/u2/mailFolders/inbox/messageRules"), (g.Sent[1].Method, g.Sent[1].Path));
        Assert.Contains("ann@fabrikam.example", g.Sent[1].Body);

        await Run(); // nothing changed → no Graph traffic
        Assert.Equal(2, g.Sent.Count);

        // Deleting the policy switches the reply off, removes the forwarding rule and then the policy itself.
        await fx.ScopeAsync(async (_, db) =>
        {
            var p = await db.AutoReplyPolicies.FirstAsync(x => x.Id == policyId);
            p.Deleted = true;
            p.Dirty = true;
            return await db.SaveChangesAsync();
        });
        await Run();
        Assert.Contains(g.Sent, s => s.Method == "PATCH" && s.Body.Contains("\"status\":\"disabled\""));
        Assert.Contains(g.Sent, s => s is { Method: "DELETE", Path: "/users/u2/mailFolders/inbox/messageRules/rule-1" });
        Assert.False(await fx.ScopeAsync((_, db) => db.AutoReplyPolicies.AnyAsync(p => p.Id == policyId)));
        Assert.False(await fx.ScopeAsync((_, db) => db.AutoReplyStates.AnyAsync(s => s.PolicyId == policyId)));
    }
}
