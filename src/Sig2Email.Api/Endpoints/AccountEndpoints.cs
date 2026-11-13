using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;
using Sig2Email.Core.Security;
using Sig2Email.Data;

namespace Sig2Email.Api.Endpoints;

/// <summary>
/// First-run protection: until the first owner account exists, creating it requires a one-time code that only someone
/// with access to the server can read (service log and a file in the data directory).
/// </summary>
public sealed class SetupCode(IOptions<Sig2EmailOptions> options, ILogger<SetupCode> log)
{
    private string File => Path.Combine(options.Value.ResolveDataDir(), "setup-code.txt");

    public async Task AnnounceIfNeededAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Accounts.IgnoreQueryFilters().AnyAsync())
        {
            Clear();
            return;
        }
        if (!System.IO.File.Exists(File))
            await System.IO.File.WriteAllTextAsync(File, Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant());
        log.LogWarning("No administrator account exists yet. Open the portal to create one; the one-time setup code is {Code} (also in {File})",
            (await System.IO.File.ReadAllTextAsync(File)).Trim(), File);
    }

    public bool Matches(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || !System.IO.File.Exists(File)) return false;
        var expected = Encoding.ASCII.GetBytes(System.IO.File.ReadAllText(File).Trim());
        var actual = Encoding.ASCII.GetBytes(code.Trim().ToLowerInvariant());
        return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Clear()
    {
        try { System.IO.File.Delete(File); } catch (IOException) { /* best effort */ }
    }
}

public static partial class AccountEndpoints
{
    public record LoginRequest(string? Username, string? Password);
    public record SetupRequest(string? SetupCode, string? Organisation, string? Username, string? DisplayName, string? Password);
    public record DevLogin(string UserId);
    public record PasswordChange(string? CurrentPassword, string? NewPassword);
    public record AccountCreate(string? Username, string? DisplayName, string? Email, TenantRole? Role, string? DirectoryUserId, string? Password);
    public record AccountUpdate(string? DisplayName, string? Email, TenantRole? Role, string? DirectoryUserId, bool Disabled, string? Password);

    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    // Verified against when the user name is unknown, so both cases take the same time.
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString());
    private static readonly SemaphoreSlim SetupGate = new(1, 1);

    [GeneratedRegex("^[a-z0-9][a-z0-9._@-]{2,63}$")]
    private static partial Regex UsernamePattern();

    private static IResult Invalid() =>
        Results.Json(new { error = "invalid_credentials", message = "The user name or password is incorrect." }, statusCode: 401);

    private static IResult Bad(string error, string message) => Results.BadRequest(new { error, message });

    private static object View(Account a) => new
    {
        a.Id, a.Username, a.DisplayName, a.Email, a.Role, a.DirectoryUserId, a.Disabled, a.MustChangePassword, a.LastLoginAt, a.CreatedAt,
        locked = a.LockedUntil > DateTime.UtcNow,
    };

    private static bool DemoMode(IHostEnvironment env, IOptions<Sig2EmailOptions> o) => env.IsDevelopment() && o.Value.SeedDemoData;

    public static void MapAccountEndpoints(this WebApplication app)
    {
        // What the sign-in page needs before anyone is signed in. Nothing here is secret.
        app.MapGet("/api/config", async (AppDbContext db, SettingsProvider settings, IHostEnvironment env, IOptions<Sig2EmailOptions> o) => new
        {
            needsSetup = !await db.Accounts.IgnoreQueryFilters().AnyAsync(),
            publicBaseUrl = settings.Current.BaseUrl,
            placeholders = Placeholders.UserFields.Concat(Enumerable.Range(1, 15).Select(i => "CustomAttribute" + i)),
            specialPlaceholders = Placeholders.Special,
            demo = DemoMode(env, o)
                ? new
                {
                    password = DemoSeeder.DemoPassword,
                    accounts = DemoSeeder.Accounts.Select(a => new { username = a.Username, name = a.Name, role = a.Role?.ToString() ?? "Manager (no admin role)" }),
                }
                : null,
        });

        app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, AppKeys keys, HttpContext http) =>
        {
            db.SystemScope = true;
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            var password = req.Password ?? "";
            if (username.Length is 0 or > 64 || password.Length is 0 or > PasswordHasher.MaxLength) return Invalid();

            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Username == username);
            if (account == null)
            {
                PasswordHasher.Verify(password, DummyHash);
                return Invalid();
            }
            var now = DateTime.UtcNow;
            if (account.LockedUntil > now)
                return Results.Json(new { error = "locked", message = "Too many failed attempts. This account is locked for a few minutes; an owner can unlock it by resetting the password." }, statusCode: 429);

            if (!PasswordHasher.Verify(password, account.PasswordHash))
            {
                if (++account.FailedLogins >= MaxFailedLogins)
                {
                    account.FailedLogins = 0;
                    account.LockedUntil = now + LockDuration;
                    db.AuditLogs.Add(new AuditLog
                    {
                        TenantId = account.TenantId, ActorId = "system", ActorName = "System", Action = "account.locked", EntityType = "Account",
                        EntityId = account.Id.ToString(), Summary = $"{account.Username} locked after {MaxFailedLogins} failed sign-ins",
                        Ip = http.Connection.RemoteIpAddress?.ToString(),
                    });
                }
                await db.SaveChangesAsync();
                return Invalid();
            }
            if (account.Disabled) return Results.Json(new { error = "disabled", message = "This account has been disabled." }, statusCode: 403);

            account.FailedLogins = 0;
            account.LockedUntil = null;
            account.LastLoginAt = now;
            await db.SaveChangesAsync();
            return Results.Ok(new { token = Auth.IssueAccountToken(keys, account) });
        }).RequireRateLimiting("login");

        // First run only: creates the organisation and its first owner.
        app.MapPost("/api/auth/setup", async (SetupRequest req, AppDbContext db, AppKeys keys, SetupCode code, SettingsProvider settings, HttpContext http) =>
        {
            await SetupGate.WaitAsync();
            try
            {
                db.SystemScope = true;
                if (await db.Accounts.AnyAsync()) return Results.Conflict(new { error = "already_set_up", message = "Setup has already been completed. Sign in instead." });
                if (!code.Matches(req.SetupCode))
                    return Results.Json(new { error = "invalid_setup_code", message = "The setup code is not correct. It is printed in the service log and stored in setup-code.txt in the data directory." }, statusCode: 403);

                var username = (req.Username ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(req.Organisation)) return Bad("organisation_required", "Enter the name of your organisation.");
                if (!UsernamePattern().IsMatch(username)) return Bad("invalid_username", "User names are 3–64 characters: letters, digits, dot, dash, underscore or @.");
                if (PasswordHasher.Problem(req.Password, username) is { } problem) return Bad("weak_password", problem);

                var tenant = await db.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync();
                if (tenant == null) db.Tenants.Add(tenant = new Tenant());
                tenant.Name = req.Organisation.Trim();
                var account = new Account
                {
                    TenantId = tenant.Id, Username = username, PasswordHash = PasswordHasher.Hash(req.Password!), Role = TenantRole.Owner,
                    DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim(), LastLoginAt = DateTime.UtcNow,
                };
                db.Accounts.Add(account);
                db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenant.Id, ActorId = $"account:{account.Id:N}", ActorName = account.DisplayName, Action = "system.setup", EntityType = "Tenant",
                    EntityId = tenant.Id.ToString(), Summary = $"{tenant.Name} set up; first owner {username}", Ip = http.Connection.RemoteIpAddress?.ToString(),
                });
                await db.SaveChangesAsync();
                code.Clear();

                // A sensible starting point for the public address: the one the administrator is using right now.
                if (settings.Version == 0)
                    await settings.UpdateAsync(s => s.PublicBaseUrl = $"{http.Request.Scheme}://{http.Request.Host}");
                return Results.Ok(new { token = Auth.IssueAccountToken(keys, account) });
            }
            finally
            {
                SetupGate.Release();
            }
        }).RequireRateLimiting("login");

        // Development with demo data only: a token for a mailbox user of the demo organisation, standing in for the
        // Microsoft token the Outlook add-in presents in production.
        app.MapPost("/api/dev/login", async (DevLogin req, AppDbContext db, AppKeys keys, IHostEnvironment env, IOptions<Sig2EmailOptions> o) =>
        {
            if (!DemoMode(env, o)) return Results.NotFound();
            db.SystemScope = true;
            var user = await db.Users.FirstOrDefaultAsync(u => u.TenantId == DemoSeeder.TenantId && u.Id == req.UserId);
            if (user == null) return Bad("unknown_user", "No such demo user.");
            return Results.Ok(new { token = Auth.IssueDirectoryUserToken(keys, user.Id, user.DisplayName, user.PrimaryEmail, DemoSeeder.EntraTenantId) });
        });

        app.MapGet("/api/me", async (CurrentUser me, AppDbContext db) =>
        {
            var isManager = me.Tenant != null && await db.Users.AnyAsync(u => u.ManagerId == me.UserId);
            return new
            {
                user = new
                {
                    id = me.UserId, name = me.Name, email = me.Email, username = me.Account?.Username, isManager,
                    mustChangePassword = me.Account?.MustChangePassword ?? false,
                },
                tenant = me.Tenant == null ? null : new { me.Tenant.Id, me.Tenant.Name, me.Tenant.Mode },
                role = me.Role,
            };
        }).RequireAuthorization();

        app.MapPost("/api/account/password", async (PasswordChange req, CurrentUser me, AppDbContext db, AppKeys keys, HttpContext http) =>
        {
            if (me.Account == null) return Results.Json(new { error = "forbidden", message = "Only portal accounts have a password here." }, statusCode: 403);
            var account = await db.Accounts.FirstAsync(a => a.Id == me.Account.Id);
            if (!PasswordHasher.Verify(req.CurrentPassword ?? "", account.PasswordHash)) return Bad("wrong_password", "The current password is not correct.");
            if (PasswordHasher.Problem(req.NewPassword, account.Username) is { } problem) return Bad("weak_password", problem);
            if (req.NewPassword == req.CurrentPassword) return Bad("same_password", "Choose a password that differs from the current one.");
            account.PasswordHash = PasswordHasher.Hash(req.NewPassword!);
            account.SecurityStamp = Guid.NewGuid().ToString("N"); // signs out every other session
            account.MustChangePassword = false;
            db.Log(http, "account.password_changed", "Account", account.Id, account.Username);
            await db.SaveChangesAsync();
            return Results.Ok(new { token = Auth.IssueAccountToken(keys, account) });
        }).RequireAuthorization().RequireRateLimiting("login");

        // ----- account management (owners) -----
        app.MapGet("/api/accounts", async (AppDbContext db) =>
            (await db.Accounts.AsNoTracking().OrderBy(a => a.DisplayName).ToListAsync()).Select(View)).RequireRoles();

        app.MapPost("/api/accounts", async (AccountCreate req, AppDbContext db, HttpContext http) =>
        {
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            if (!UsernamePattern().IsMatch(username)) return Bad("invalid_username", "User names are 3–64 characters: letters, digits, dot, dash, underscore or @.");
            if (await db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.Username == username)) return Bad("username_taken", "That user name is already in use.");
            if (PasswordHasher.Problem(req.Password, username) is { } problem) return Bad("weak_password", problem);
            if (await LinkProblem(db, req.Role, req.DirectoryUserId) is { } link) return link;

            var account = new Account
            {
                Username = username, DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim(),
                Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(), Role = req.Role,
                DirectoryUserId = string.IsNullOrWhiteSpace(req.DirectoryUserId) ? null : req.DirectoryUserId,
                PasswordHash = PasswordHasher.Hash(req.Password!), MustChangePassword = true,
            };
            db.Accounts.Add(account);
            db.Log(http, "account.created", "Account", account.Id, $"{account.Username} ({RoleName(account.Role)})");
            await db.SaveChangesAsync();
            return Results.Ok(View(account));
        }).RequireRoles();

        app.MapPut("/api/accounts/{id:guid}", async (Guid id, AccountUpdate req, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account == null) return Results.NotFound();
            if (await LinkProblem(db, req.Role, req.DirectoryUserId) is { } link) return link;
            var losesOwner = account.Role == TenantRole.Owner && !account.Disabled && (req.Role != TenantRole.Owner || req.Disabled);
            if (losesOwner && await db.Accounts.CountAsync(a => a.Role == TenantRole.Owner && !a.Disabled) == 1)
                return Bad("last_owner", "At least one active owner account is required.");
            if (req.Disabled && account.Id == me.Account?.Id) return Bad("self_disable", "You cannot disable your own account.");

            var changes = new List<string>();
            if (account.Role != req.Role) changes.Add($"role {RoleName(account.Role)} → {RoleName(req.Role)}");
            if (account.Disabled != req.Disabled)
            {
                changes.Add(req.Disabled ? "disabled" : "enabled");
                account.SecurityStamp = Guid.NewGuid().ToString("N");
            }
            if (!string.IsNullOrEmpty(req.Password))
            {
                if (PasswordHasher.Problem(req.Password, account.Username) is { } problem) return Bad("weak_password", problem);
                account.PasswordHash = PasswordHasher.Hash(req.Password);
                account.SecurityStamp = Guid.NewGuid().ToString("N");
                account.MustChangePassword = account.Id != me.Account?.Id;
                account.FailedLogins = 0;
                account.LockedUntil = null;
                changes.Add("password reset");
            }
            account.DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? account.Username : req.DisplayName.Trim();
            account.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
            account.Role = req.Role;
            account.DirectoryUserId = string.IsNullOrWhiteSpace(req.DirectoryUserId) ? null : req.DirectoryUserId;
            account.Disabled = req.Disabled;
            db.Log(http, "account.updated", "Account", account.Id, $"{account.Username}: {(changes.Count == 0 ? "details" : string.Join(", ", changes))}");
            await db.SaveChangesAsync();
            return Results.Ok(View(account));
        }).RequireRoles();

        app.MapDelete("/api/accounts/{id:guid}", async (Guid id, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
            if (account == null) return Results.NotFound();
            if (account.Id == me.Account?.Id) return Bad("self_delete", "You cannot delete your own account.");
            if (account.Role == TenantRole.Owner && !account.Disabled && await db.Accounts.CountAsync(a => a.Role == TenantRole.Owner && !a.Disabled) == 1)
                return Bad("last_owner", "At least one active owner account is required.");
            db.Accounts.Remove(account);
            db.Log(http, "account.deleted", "Account", account.Id, $"{account.Username} ({RoleName(account.Role)})");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRoles();
    }

    private static string RoleName(TenantRole? role) => role?.ToString() ?? "Manager";

    private static async Task<IResult?> LinkProblem(AppDbContext db, TenantRole? role, string? directoryUserId)
    {
        if (string.IsNullOrWhiteSpace(directoryUserId))
            return role == null ? Bad("link_required", "An account without an administrative role must be linked to a person in the directory.") : null;
        return await db.Users.AnyAsync(u => u.Id == directoryUserId) ? null : Bad("unknown_user", "The linked person was not found in the directory.");
    }
}
