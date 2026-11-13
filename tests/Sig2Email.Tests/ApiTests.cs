using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sig2Email.Api;
using Sig2Email.Core.Models;
using Sig2Email.Core.Security;
using Sig2Email.Data;

namespace Sig2Email.Tests;

public sealed class ApiFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s2e-api-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<CurrentUser> Factory { get; }

    // Demo people who have a portal account; everyone else signs in as a plain mailbox user (what the Outlook add-in does).
    private static readonly Dictionary<string, string> Usernames = new()
    {
        ["u-owner"] = "owner", ["u-rules"] = "rules", ["u-design"] = "designer", ["u-analyst"] = "analyst",
    };
    private readonly Dictionary<string, string> _tokens = new();

    public ApiFixture()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Sig2Email__DataDir", _dir);
        Environment.SetEnvironmentVariable("Sig2Email__SeedDemoData", "true");
        Factory = new WebApplicationFactory<CurrentUser>();
        _ = Factory.Server; // start now, while the environment variables above are in effect
    }

    public HttpClient Anonymous() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public HttpClient WithToken(string token)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<string> TokenAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>Signs in with user name and password.</summary>
    public async Task<HttpClient> LoginAsync(string username, string password = DemoSeeder.DemoPassword) =>
        WithToken(await TokenAsync(await Anonymous().PostAsJsonAsync("/api/auth/login", new { username, password })));

    public async Task<HttpClient> ClientAsync(string? userId)
    {
        if (userId == null) return Anonymous();
        if (!_tokens.TryGetValue(userId, out var token))
        {
            var res = Usernames.TryGetValue(userId, out var username)
                ? await Anonymous().PostAsJsonAsync("/api/auth/login", new { username, password = DemoSeeder.DemoPassword })
                : await Anonymous().PostAsJsonAsync("/api/dev/login", new { userId });
            _tokens[userId] = token = await TokenAsync(res);
        }
        return WithToken(token);
    }

    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        return await action(db);
    }

    public void Dispose()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public class ApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static readonly object Template = new { name = "T", description = "", html = "<p>{DisplayName} {if MobilePhone}M{/if}</p>", designJson = (string?)null, imageMode = "Hosted" };

    private static object RuleBody(Guid templateId, string name = "Test rule") => new
    {
        name, enabled = true, templateId, placement = "Bottom", channels = "Both", onMatch = "Continue", userSelectable = false, trackLinks = false,
        utmSource = (string?)null, utmMedium = (string?)null, utmCampaign = (string?)null,
        conditions = new { senders = new { everyone = false, userIds = new[] { "u-fin" } } },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected()
    {
        var anon = await fx.ClientAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/rules")).StatusCode);
        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJub25lIn0.eyJvaWQiOiJ1LW93bmVyIn0.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/config")).StatusCode);
    }

    [Fact]
    public async Task Role_based_access_matches_the_srs_matrix()
    {
        var owner = await fx.ClientAsync("u-owner");
        var rules = await fx.ClientAsync("u-rules");
        var designer = await fx.ClientAsync("u-design");
        var analyst = await fx.ClientAsync("u-analyst");
        var endUser = await fx.ClientAsync("u-sales2");
        var templateId = (await Json(await owner.GetAsync("/api/templates")))[0].GetProperty("id").GetGuid();

        // Template designer: designs, but cannot touch live mail flow (rules, publishing, tenant settings).
        var draft = await Json(await designer.PostAsJsonAsync("/api/templates", Template));
        var draftId = draft.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await designer.PostAsJsonAsync("/api/rules", RuleBody(templateId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await designer.PostAsync($"/api/templates/{draftId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await designer.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await designer.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await designer.GetAsync("/api/analytics/csat")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await designer.GetAsync("/api/rules")).StatusCode);

        // Rule administrator: rules and publishing, not template design or RBAC.
        Assert.Equal(HttpStatusCode.Forbidden, (await rules.PutAsJsonAsync($"/api/templates/{draftId}", Template)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rules.PostAsync($"/api/templates/{draftId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rules.PostAsJsonAsync("/api/accounts", new { username = "sneaky", role = "Owner", password = "a-long-enough-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rules.PutAsJsonAsync("/api/settings", new { publicBaseUrl = "https://evil.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rules.PostAsync("/api/settings/test-entra", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rules.GetAsync("/api/tenant/provisioning-script")).StatusCode);

        // Analytics viewer: read-only dashboards and audit log.
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync("/api/analytics/csat")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/directory/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsJsonAsync("/api/rules", RuleBody(templateId))).StatusCode);

        // A user with no admin role gets nothing from the control plane.
        Assert.Equal(HttpStatusCode.Forbidden, (await endUser.GetAsync("/api/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await endUser.GetAsync("/api/audit")).StatusCode);

        // Owner: everything, and changes are audited.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/tenant/provisioning-script")).StatusCode);
        var audit = await Json(await owner.GetAsync("/api/audit?search=template.published"));
        Assert.True(audit.GetProperty("total").GetInt32() >= 1);
        // The last active owner can be neither demoted, disabled nor deleted.
        var accounts = await Json(await owner.GetAsync("/api/accounts"));
        var self = accounts.EnumerateArray().Single(a => a.GetProperty("username").GetString() == "owner").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/accounts/{self}", new { displayName = "Olivia", role = "Designer", directoryUserId = "u-owner", disabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/accounts/{self}", new { displayName = "Olivia", role = "Owner", directoryUserId = "u-owner", disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"/api/accounts/{self}")).StatusCode);
    }

    [Fact]
    public async Task Organisations_are_isolated_from_each_other()
    {
        var contoso = await fx.ClientAsync("u-owner");
        var contosoTemplate = (await Json(await contoso.GetAsync("/api/templates")))[0].GetProperty("id").GetGuid();
        var contosoRule = (await Json(await contoso.GetAsync("/api/rules")))[0].GetProperty("id").GetGuid();

        // A second organisation in the same database (the product is deployed for one, the data model still isolates).
        await fx.DbAsync(async db =>
        {
            if (await db.Accounts.AnyAsync(a => a.Username == "fabrikam-owner")) return 0;
            var tenant = new Tenant { Name = "Fabrikam" };
            db.Tenants.Add(tenant);
            db.Accounts.Add(new Account
            {
                TenantId = tenant.Id, Username = "fabrikam-owner", DisplayName = "Fabrikam Owner", Role = TenantRole.Owner,
                PasswordHash = PasswordHasher.Hash("fabrikam-password-1"),
            });
            return await db.SaveChangesAsync();
        });
        var admin = await fx.LoginAsync("fabrikam-owner", "fabrikam-password-1");

        // It starts empty and cannot see or modify Contoso's objects, even with their ids.
        Assert.Equal(0, (await Json(await admin.GetAsync("/api/rules"))).GetArrayLength());
        Assert.Single((await Json(await admin.GetAsync("/api/accounts"))).EnumerateArray());
        Assert.Equal(0, (await Json(await admin.GetAsync("/api/directory/users"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/templates/{contosoTemplate}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/templates/{contosoTemplate}", Template)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/rules/{contosoRule}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/rules", RuleBody(contosoTemplate))).StatusCode); // unknown template
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/rules/simulate", new { senderId = "u-owner", messageType = "New", channel = "Server" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await contoso.GetAsync($"/api/rules/{contosoRule}")).StatusCode);
    }

    [Fact]
    public async Task Draft_templates_do_not_affect_mail_flow_until_published()
    {
        var owner = await fx.ClientAsync("u-owner");
        var t = await Json(await owner.PostAsJsonAsync("/api/templates", Template));
        var id = t.GetProperty("id").GetGuid();
        var rule = await Json(await owner.PostAsJsonAsync("/api/rules", RuleBody(id, "Finance footer")));
        var simulate = new { senderId = "u-fin", recipients = new[] { "olivia.owner@contoso.example" }, messageType = "Reply", subject = "RE: x #nosig", body = "", channel = "Server" };

        var before = await Json(await owner.PostAsJsonAsync("/api/rules/simulate", simulate));
        Assert.DoesNotContain("George Hall", before.GetProperty("bottom").GetString());
        Assert.Contains("Finance footer", before.GetProperty("skippedUnpublished").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal(1, (await Json(await owner.PostAsync($"/api/templates/{id}/publish", null))).GetProperty("version").GetInt32());
        var after = await Json(await owner.PostAsJsonAsync("/api/rules/simulate", simulate));
        Assert.Equal("<p>George Hall </p>", after.GetProperty("bottom").GetString());

        // Editing the draft again leaves the published version live.
        await Json(await owner.PutAsJsonAsync($"/api/templates/{id}", new { name = "T", description = "", html = "<p>CHANGED</p>", designJson = (string?)null, imageMode = "Hosted" }));
        var stillOld = await Json(await owner.PostAsJsonAsync("/api/rules/simulate", simulate));
        Assert.Equal("<p>George Hall </p>", stillOld.GetProperty("bottom").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/templates/{id}")).StatusCode); // in use
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/rules/{rule.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/templates/{id}")).StatusCode);
    }

    [Fact]
    public async Task Addin_signature_one_click_csat_and_link_tracking_end_to_end()
    {
        var agent = await fx.ClientAsync("u-cs1");
        var owner = await fx.ClientAsync("u-owner");
        var anon = await fx.ClientAsync(null);

        var options = await Json(await agent.GetAsync("/api/addin/options"));
        Assert.True(options.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, options.GetProperty("alternatives").GetArrayLength());

        var composeKey = Guid.NewGuid().ToString();
        var request = new { recipients = new[] { "customer@fabrikam.example" }, messageType = "New", subject = "Ticket 42", composeKey };
        var sig = await Json(await agent.PostAsJsonAsync("/api/addin/signature", request));
        var html = sig.GetProperty("html").GetString()!;
        Assert.Contains("Ben Okafor", html);
        Assert.DoesNotContain("confidential", html); // the disclaimer is server-only and is added in transit
        Assert.Equal("X-Sig2Email-Client", sig.GetProperty("headerName").GetString());
        Assert.Matches("^rules=[0-9a-f]{32}$", sig.GetProperty("headerValue").GetString());

        // Re-rendering the same compose session (recipients changed) reuses the same survey dispatch.
        var again = await Json(await agent.PostAsJsonAsync("/api/addin/signature", request));
        var link = Regex.Match(html, @"http://localhost:5080(/r/([0-9a-f]{32})/1\?k=([\w-]+))");
        Assert.True(link.Success);
        Assert.Contains(link.Groups[2].Value, again.GetProperty("html").GetString());

        // Internal-only recipients → the survey rule no longer applies, the standard signature does.
        var internalSig = await Json(await agent.PostAsJsonAsync("/api/addin/signature", new { recipients = new[] { "olivia.owner@contoso.example" }, messageType = "New", subject = "x", composeKey }));
        Assert.DoesNotContain("/r/", internalSig.GetProperty("html").GetString());

        // Manual choice from the task pane.
        var shortRule = options.GetProperty("alternatives").EnumerateArray().First(a => a.GetProperty("name").GetString()!.StartsWith("Short")).GetProperty("id").GetGuid();
        var manual = await Json(await agent.PostAsJsonAsync("/api/addin/signature", new { recipients = Array.Empty<string>(), messageType = "New", ruleId = shortRule }));
        Assert.StartsWith("manual; rules=", manual.GetProperty("headerValue").GetString());
        Assert.Contains("<strong>Ben Okafor</strong>", manual.GetProperty("html").GetString());

        // --- recipient clicks the lowest rating (no login) ---
        var (path, dispatch, k) = (link.Groups[1].Value, link.Groups[2].Value, link.Groups[3].Value);
        HttpResponseMessage page = null!;
        for (var i = 0; i < 50 && (page = await anon.GetAsync(path)).StatusCode == HttpStatusCode.NotFound; i++) await Task.Delay(100); // background writer
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("How would you rate my reply?", await page.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'nonce-", page.Headers.GetValues("Content-Security-Policy").Single());

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/r/{dispatch}/1?k=AAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsJsonAsync($"/r/{dispatch}", new { score = 1, k = "forged" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"/r/{dispatch}", new { score = 9, k })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync($"/r/{dispatch}", new { score = 1, k })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync($"/r/{dispatch}", new { score = 2, comment = "Slow answer", k })).StatusCode);

        var negatives = await Json(await owner.GetAsync("/api/analytics/responses?negativeOnly=true&take=100"));
        var mine = negatives.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("subject").GetString() == "Ticket 42");
        Assert.Equal(2, mine.GetProperty("score").GetInt32()); // the later click replaced the first, one response per email
        Assert.Equal("Slow answer", mine.GetProperty("comment").GetString());
        Assert.Equal("ben.okafor@contoso.example", mine.GetProperty("senderEmail").GetString());
        Assert.Equal("customer@fabrikam.example", mine.GetProperty("recipients").GetString());

        // --- tracked link ---
        var tracked = Regex.Match(html, @"http://localhost:5080(/l/[\w.-]+)").Groups[1].Value;
        var clicksBefore = (await Json(await owner.GetAsync("/api/analytics/clicks"))).GetProperty("total").GetInt32();
        var redirect = await anon.GetAsync(tracked);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://www.contoso.example/?utm_source=email-signature&utm_medium=email&utm_campaign=cs-signature", redirect.Headers.Location!.ToString());
        Assert.Equal(clicksBefore + 1, (await Json(await owner.GetAsync("/api/analytics/clicks"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(tracked[..^2] + "zz")).StatusCode); // no open redirect
    }

    [Fact]
    public async Task Managers_can_only_set_auto_replies_for_their_direct_reports()
    {
        var manager = await fx.ClientAsync("u-sales1"); // Sam: no admin role, manages u-sales2 and u-sales3
        var staff = await fx.ClientAsync("u-sales2");
        var owner = await fx.ClientAsync("u-owner");
        object Policy(string[] users, string[]? groups = null) => new
        {
            name = "Annual leave", enabled = true, userIds = users, groupIds = groups ?? Array.Empty<string>(),
            startUtc = DateTime.UtcNow.AddDays(1), endUtc = DateTime.UtcNow.AddDays(8),
            internalMessage = "<p>Away</p>", externalMessage = "<p>Away</p>", externalAudience = "All", forwardTo = "sam.carter@contoso.example",
        };

        var targets = await Json(await manager.GetAsync("/api/auto-replies/targets"));
        Assert.Equal(new[] { "u-sales2", "u-sales3" }, targets.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("id").GetString()).Order());

        var created = await Json(await manager.PostAsJsonAsync("/api/auto-replies", Policy(new[] { "u-sales2" })));
        Assert.True(created.GetProperty("managerDelegated").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/auto-replies", Policy(new[] { "u-owner" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/auto-replies", Policy(new[] { "u-sales2" }, new[] { "g-all" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/auto-replies", Policy(new[] { "u-sales3" }))).StatusCode);

        Assert.Equal(1, (await Json(await manager.GetAsync("/api/auto-replies"))).GetArrayLength());
        Assert.Equal(0, (await Json(await staff.GetAsync("/api/auto-replies"))).GetArrayLength());       // others' policies are invisible
        Assert.Equal(1, (await Json(await owner.GetAsync("/api/auto-replies"))).GetArrayLength());       // admins see all
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/auto-replies/{created.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/auto-replies", new
        {
            name = "Bad", enabled = true, userIds = new[] { "u-fin" }, groupIds = Array.Empty<string>(), startUtc = DateTime.UtcNow, endUtc = DateTime.UtcNow.AddDays(-1),
            internalMessage = "", externalMessage = "", externalAudience = "None",
        })).StatusCode);
    }

    [Fact]
    public async Task Sign_in_needs_the_right_password_and_locks_after_repeated_failures()
    {
        var anon = fx.Anonymous();
        var owner = await fx.ClientAsync("u-owner");
        var created = await Json(await owner.PostAsJsonAsync("/api/accounts", new { username = "Lock.Test", displayName = "Lock Test", role = "AnalyticsViewer", password = "correct-horse-battery" }));
        Assert.Equal("lock.test", created.GetProperty("username").GetString());
        Assert.True(created.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(created.TryGetProperty("passwordHash", out _));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "nobody-here", password = "correct-horse-battery" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "lock.test", password = "" })).StatusCode);
        // The dev shortcut for mailbox users must not hand out tokens for arbitrary ids.
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/dev/login", new { userId = "no-such-user" })).StatusCode);

        var good = await fx.LoginAsync("LOCK.test", "correct-horse-battery"); // user names are case-insensitive
        Assert.Equal("AnalyticsViewer", (await Json(await good.GetAsync("/api/me"))).GetProperty("role").GetString());

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "lock.test", password = "wrong-password-" + i })).StatusCode);
        // Locked now: even the right password is refused until an owner resets it.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "lock.test", password = "correct-horse-battery" })).StatusCode);
        var id = created.GetProperty("id").GetGuid();
        await Json(await owner.PutAsJsonAsync($"/api/accounts/{id}", new { displayName = "Lock Test", role = "AnalyticsViewer", disabled = false, password = "a-brand-new-password" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await good.GetAsync("/api/me")).StatusCode); // the reset ended the old session
        var again = await fx.LoginAsync("lock.test", "a-brand-new-password");

        // Disabling an account ends its sessions immediately and blocks sign-in.
        await Json(await owner.PutAsJsonAsync($"/api/accounts/{id}", new { displayName = "Lock Test", role = "AnalyticsViewer", disabled = true }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "lock.test", password = "a-brand-new-password" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/accounts/{id}")).StatusCode);
        var audit = await Json(await owner.GetAsync("/api/audit?search=account."));
        var actions = audit.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("account.created", actions);
        Assert.Contains("account.locked", actions);
        Assert.Contains("account.deleted", actions);
    }

    [Fact]
    public async Task Accounts_validate_input_and_users_can_change_their_own_password()
    {
        var owner = await fx.ClientAsync("u-owner");
        async Task<string> Error(Task<HttpResponseMessage> call)
        {
            var res = await call;
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        }

        Assert.Equal("invalid_username", await Error(owner.PostAsJsonAsync("/api/accounts", new { username = "a b", role = "Designer", password = "long-enough-password" })));
        Assert.Equal("weak_password", await Error(owner.PostAsJsonAsync("/api/accounts", new { username = "pw.test", role = "Designer", password = "short" })));
        Assert.Equal("weak_password", await Error(owner.PostAsJsonAsync("/api/accounts", new { username = "pw.test", role = "Designer", password = "xx-pw.test-xx" })));
        Assert.Equal("username_taken", await Error(owner.PostAsJsonAsync("/api/accounts", new { username = "OWNER", role = "Designer", password = "long-enough-password" })));
        Assert.Equal("link_required", await Error(owner.PostAsJsonAsync("/api/accounts", new { username = "pw.test", password = "long-enough-password" })));
        Assert.Equal("unknown_user", await Error(owner.PostAsJsonAsync("/api/accounts", new { username = "pw.test", role = "Designer", directoryUserId = "nope", password = "long-enough-password" })));

        await Json(await owner.PostAsJsonAsync("/api/accounts", new { username = "pw.test", displayName = "Pat Password", role = "Designer", password = "first-password-1" }));
        var user = await fx.LoginAsync("pw.test", "first-password-1");
        Assert.True((await Json(await user.GetAsync("/api/me"))).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        Assert.Equal("wrong_password", await Error(user.PostAsJsonAsync("/api/account/password", new { currentPassword = "nope", newPassword = "second-password-2" })));
        Assert.Equal("weak_password", await Error(user.PostAsJsonAsync("/api/account/password", new { currentPassword = "first-password-1", newPassword = "aaaaaaaaaaaa" })));
        var fresh = fx.WithToken(await ApiFixture.TokenAsync(await user.PostAsJsonAsync("/api/account/password", new { currentPassword = "first-password-1", newPassword = "second-password-2" })));

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/me")).StatusCode); // sessions from before the change are gone
        Assert.False((await Json(await fresh.GetAsync("/api/me"))).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "pw.test", password = "first-password-1" })).StatusCode);
        await fx.LoginAsync("pw.test", "second-password-2");

        // Passwords are stored as salted hashes only.
        var hash = await fx.DbAsync(db => db.Accounts.Where(a => a.Username == "pw.test").Select(a => a.PasswordHash).SingleAsync());
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.DoesNotContain("second-password-2", hash);
    }

    [Fact]
    public async Task A_manager_account_without_an_admin_role_only_reaches_auto_replies_for_their_reports()
    {
        var manager = await fx.LoginAsync("manager"); // Sam Carter, linked to u-sales1
        var me = await Json(await manager.GetAsync("/api/me"));
        Assert.Equal(JsonValueKind.Null, me.GetProperty("role").ValueKind);
        Assert.True(me.GetProperty("user").GetProperty("isManager").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/settings")).StatusCode);
        var targets = await Json(await manager.GetAsync("/api/auto-replies/targets"));
        Assert.Equal(new[] { "u-sales2", "u-sales3" }, targets.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("id").GetString()).Order());
    }

    [Fact]
    public async Task System_settings_are_edited_in_the_portal_stored_encrypted_and_never_returned_in_clear()
    {
        var owner = await fx.ClientAsync("u-owner");
        object Body(string? secret, string? alertPassword, string tenantId = "0f0f0f0f-1111-2222-3333-444444444444", string url = "https://signatures.contoso.example/") => new
        {
            publicBaseUrl = url, entraTenantId = tenantId, entraClientId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", entraClientSecret = secret,
            smtpHostName = "smtp.contoso.example", smtpRequireTls = false, smtpRequireClientCertificate = false,
            smtpAllowedClientNames = new[] { "mail.protection.outlook.com" }, smtpMaxMessageMb = 35, relayPort = 2526, relayRequireTls = false,
            alertMailHost = "mail.contoso.example", alertMailPort = 587, alertMailUser = "alerts", alertMailPassword = alertPassword,
            alertMailFrom = "alerts@contoso.example", syncIntervalMinutes = 30, autoReplyIntervalSeconds = 120,
        };
        var before = await Json(await owner.GetAsync("/api/settings"));
        var original = new { url = before.GetProperty("publicBaseUrl").GetString()!, tenant = before.GetProperty("entraTenantId").GetString()! };
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/settings", Body(null, null, url: "ftp://x"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/settings", Body(null, null, tenantId: "not-a-guid"))).StatusCode);

            var saved = await Json(await owner.PutAsJsonAsync("/api/settings", Body("s3cr3t-Value~1", "smtp-pass-1")));
            Assert.Equal("https://signatures.contoso.example", saved.GetProperty("publicBaseUrl").GetString());
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", saved.GetProperty("entraClientId").GetString());
            Assert.True(saved.GetProperty("entraClientSecretSet").GetBoolean());
            Assert.True(saved.GetProperty("alertMailPasswordSet").GetBoolean());
            Assert.False(saved.GetProperty("connected").GetBoolean()); // not until the connection test passes
            Assert.Equal("brk-multihub://signatures.contoso.example", saved.GetProperty("addinRedirectUri").GetString());
            Assert.DoesNotContain("s3cr3t", saved.GetRawText());
            Assert.DoesNotContain("smtp-pass", saved.GetRawText());

            // Null keeps a stored secret, an empty string clears it.
            saved = await Json(await owner.PutAsJsonAsync("/api/settings", Body(null, "")));
            Assert.True(saved.GetProperty("entraClientSecretSet").GetBoolean());
            Assert.False(saved.GetProperty("alertMailPasswordSet").GetBoolean());
            var settings = fx.Factory.Services.GetRequiredService<SettingsProvider>().Current;
            Assert.Equal("s3cr3t-Value~1", settings.EntraClientSecret);
            Assert.Equal(30, settings.SyncIntervalMinutes);

            // At rest the row is encrypted: no secret, no host name in clear.
            var row = await fx.DbAsync(db => db.SystemSettings.Select(r => r.Data).SingleAsync());
            Assert.StartsWith("v1:", row);
            Assert.DoesNotContain("s3cr3t", row);
            Assert.DoesNotContain("contoso", row);

            // Links in signatures and the mail-flow script follow the new settings at once.
            var rendered = await Json(await owner.PostAsJsonAsync("/api/templates/render", new { html = "<img src=\"{QrCode}\">", userId = "u-owner" }));
            Assert.Contains("https://signatures.contoso.example/assets/qr/", rendered.GetProperty("html").GetString());
            Assert.Contains("$smartHost = 'smtp.contoso.example'", await (await owner.GetAsync("/api/tenant/provisioning-script")).Content.ReadAsStringAsync());
            Assert.Equal("https://signatures.contoso.example", (await Json(await fx.Anonymous().GetAsync("/api/config"))).GetProperty("publicBaseUrl").GetString());
            Assert.True((await Json(await owner.GetAsync("/api/audit?search=settings.updated"))).GetProperty("total").GetInt32() >= 2);
        }
        finally
        {
            await Json(await owner.PutAsJsonAsync("/api/settings", Body("", "", original.tenant, original.url)));
            await fx.DbAsync(db => db.Tenants.Where(t => t.Id == DemoSeeder.TenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsentGranted, true)));
        }
    }

    [Fact]
    public async Task Tls_certificate_is_uploaded_through_the_portal()
    {
        var owner = await fx.ClientAsync("u-owner");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=smtp.contoso.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        MultipartFormDataContent Form(byte[] bytes, string password)
        {
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(bytes), "file", "smtp.pfx");
            content.Add(new StringContent(password), "password");
            return content;
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/settings/certificate", Form("not a certificate"u8.ToArray(), ""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/settings/certificate", Form(cert.Export(X509ContentType.Pfx, "pw"), "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/settings/certificate", Form(cert.Export(X509ContentType.Cert), ""))).StatusCode); // no private key
        Assert.Equal(HttpStatusCode.Forbidden, (await (await fx.ClientAsync("u-rules")).PostAsync("/api/settings/certificate", Form(cert.Export(X509ContentType.Pfx, "pw"), "pw"))).StatusCode);

        var uploaded = await Json(await owner.PostAsync("/api/settings/certificate", Form(cert.Export(X509ContentType.Pfx, "pw"), "pw")));
        Assert.Equal(cert.Thumbprint, uploaded.GetProperty("certificate").GetProperty("thumbprint").GetString());
        var info = (await Json(await owner.GetAsync("/api/settings"))).GetProperty("certificate");
        Assert.Equal("smtp.contoso.example", info.GetProperty("subject").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/settings/certificate")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await owner.GetAsync("/api/settings"))).GetProperty("certificate").ValueKind);
    }

    [Fact]
    public async Task Asset_upload_accepts_real_images_only_and_serves_them_publicly()
    {
        var designer = await fx.ClientAsync("u-design");
        MultipartFormDataContent File(string name, byte[] bytes)
        {
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(bytes), "file", name);
            return content;
        }
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        Assert.Equal(HttpStatusCode.BadRequest, (await designer.PostAsync("/api/assets", File("logo.svg", "<svg onload=alert(1)>"u8.ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await designer.PostAsync("/api/assets", File("logo.png", "<html><script>alert(1)</script>"u8.ToArray()))).StatusCode);
        var asset = await Json(await designer.PostAsync("/api/assets", File("../../Our Logo!.PNG", png)));
        Assert.Equal("OurLogo.png", asset.GetProperty("fileName").GetString());

        var anon = await fx.ClientAsync(null);
        var served = await anon.GetAsync(new Uri(asset.GetProperty("url").GetString()!).PathAndQuery);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());

        var qr = Regex.Match((await Json(await designer.PostAsJsonAsync("/api/templates/render", new { html = "<img src=\"{QrCode}\">", userId = "u-owner" }))).GetProperty("html").GetString()!,
            "src=\"http://localhost:5080([^\"]+)\"").Groups[1].Value.Replace("&amp;", "&");
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(qr)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(qr.Replace("u-owner", "u-rules"))).StatusCode); // signature bound to the user
    }
}
