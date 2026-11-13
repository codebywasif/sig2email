using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sig2Email.Core.Models;
using Sig2Email.Data;

namespace Sig2Email.Api;

/// <summary>The signed-in principal, the organisation it belongs to and the role it acts in.</summary>
public sealed class CurrentUser
{
    /// <summary>Directory (Entra object) id of the person, or "account:…" for a portal account not linked to one.</summary>
    public string UserId { get; set; } = "";
    /// <summary>Set for portal accounts (user name + password); null for Outlook add-in users.</summary>
    public Account? Account { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public Tenant? Tenant { get; set; }
    public TenantRole? Role { get; set; }

    public bool Has(params TenantRole[] roles) => Role == TenantRole.Owner || (Role != null && roles.Contains(Role.Value));
}

/// <summary>
/// Two kinds of bearer token are accepted:
///  • "Local" – issued by this service after a user-name/password sign-in to the portal;
///  • "Entra" – Microsoft tokens presented by the Outlook add-in, which identify a mailbox user and carry no admin role.
/// </summary>
public static class Auth
{
    public const string LocalIssuer = "sig2email";
    private const string LocalScheme = "Local";
    private const string EntraScheme = "Entra";
    private const string SelectorScheme = "Bearer";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public static void AddSig2EmailAuth(this IServiceCollection services)
    {
        services.AddScoped<CurrentUser>();
        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, null, o => o.ForwardDefaultSelector = ctx =>
            {
                var header = ctx.Request.Headers.Authorization.ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var issuer = new JsonWebToken(header[7..].Trim()).Issuer ?? "";
                        if (issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal) ||
                            issuer.StartsWith("https://sts.windows.net/", StringComparison.Ordinal)) return EntraScheme;
                    }
                    catch (Exception) { /* malformed → rejected by the local scheme */ }
                }
                return LocalScheme;
            })
            .AddJwtBearer(LocalScheme, _ => { })
            .AddJwtBearer(EntraScheme, _ => { });

        services.AddOptions<JwtBearerOptions>(LocalScheme).Configure<AppKeys>((o, keys) =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = LocalIssuer,
                ValidateAudience = false,
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                IssuerSigningKey = new SymmetricSecurityKey(keys.Session),
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        });

        services.AddOptions<JwtBearerOptions>(EntraScheme).Configure<SettingsProvider>((o, settings) =>
        {
            o.MapInboundClaims = false;
            // Signing keys come from Microsoft's common endpoint; the audience is the app registration entered in the
            // portal (read at validation time, so changing it needs no restart) and the tenant is checked when the
            // request is bound to the organisation.
            o.MetadataAddress = "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";
            o.TokenValidationParameters = new TokenValidationParameters
            {
                AudienceValidator = (audiences, _, _) =>
                {
                    var clientId = settings.Current.EntraClientId;
                    return !string.IsNullOrWhiteSpace(clientId) &&
                           audiences.Any(a => a.Equals(clientId, StringComparison.OrdinalIgnoreCase) || a.Equals($"api://{clientId}", StringComparison.OrdinalIgnoreCase));
                },
                IssuerValidator = (issuer, token, _) =>
                {
                    var tid = (token as JsonWebToken)?.GetClaim("tid")?.Value ?? (token as JwtSecurityToken)?.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
                    if (Guid.TryParse(tid, out Guid _) &&
                        (issuer == $"https://login.microsoftonline.com/{tid}/v2.0" || issuer == $"https://sts.windows.net/{tid}/"))
                        return issuer;
                    throw new SecurityTokenInvalidIssuerException("Unexpected token issuer.");
                },
            };
        });
        services.AddAuthorization();
    }

    private static string Issue(AppKeys keys, IEnumerable<Claim> claims)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(keys.Session), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(LocalIssuer, null, claims, expires: DateTime.UtcNow.Add(SessionLifetime), signingCredentials: creds));
    }

    /// <summary>Session token for a portal account. It stops working when the account's security stamp changes.</summary>
    public static string IssueAccountToken(AppKeys keys, Account account) =>
        Issue(keys, new[] { new Claim("acct", account.Id.ToString("N")), new Claim("stamp", account.SecurityStamp) });

    /// <summary>Development only: stands in for the Microsoft token of a mailbox user (Outlook add-in) of the demo organisation.</summary>
    public static string IssueDirectoryUserToken(AppKeys keys, string userId, string name, string email, string entraTenantId) =>
        Issue(keys, new[] { new Claim("oid", userId), new Claim("name", name), new Claim("preferred_username", email), new Claim("tid", entraTenantId) });

    /// <summary>Binds the request to its organisation: fills <see cref="CurrentUser"/> and scopes the DbContext.</summary>
    public static void UseTenantBinding(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var me = ctx.RequestServices.GetRequiredService<CurrentUser>();
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            var accountId = ctx.User.FindFirstValue("acct");

            if (accountId != null)
            {
                var account = Guid.TryParse(accountId, out var id)
                    ? await db.Accounts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id)
                    : null;
                // Disabled accounts, and sessions from before a password change, are signed out at once.
                if (account == null || account.Disabled || account.SecurityStamp != ctx.User.FindFirstValue("stamp"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                me.Account = account;
                me.Tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == account.TenantId);
                me.UserId = account.DirectoryUserId ?? $"account:{account.Id:N}";
                me.Name = account.DisplayName;
                me.Email = account.Email ?? "";
                me.Role = account.Role;
            }
            else
            {
                var tid = ctx.User.FindFirstValue("tid") ?? "";
                me.UserId = ctx.User.FindFirstValue("oid") ?? "";
                me.Email = ctx.User.FindFirstValue("preferred_username") ?? ctx.User.FindFirstValue("upn") ?? "";
                me.Name = ctx.User.FindFirstValue("name") ?? me.Email;
                if (me.UserId.Length > 0 && tid.Length > 0)
                    me.Tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.EntraTenantId == tid);
            }
            if (me.Tenant != null) db.TenantId = me.Tenant.Id;
        }
        await next();
    });

    private static IResult NoOrganisation() =>
        Results.Json(new { error = "no_organisation", message = "This sign-in does not belong to the organisation set up here." }, statusCode: 403);

    /// <summary>Requires a portal account with one of the given roles (Owner always passes).</summary>
    public static TBuilder RequireRoles<TBuilder>(this TBuilder builder, params TenantRole[] roles) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var me = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (me.Tenant == null) return NoOrganisation();
            if (!me.Has(roles)) return Results.Json(new { error = "forbidden", message = "Your role does not allow this action." }, statusCode: 403);
            return await next(ctx);
        });
    }

    /// <summary>Anyone who belongs to the organisation (Outlook add-in users, manager-delegated auto-replies).</summary>
    public static TBuilder RequireTenantUser<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var me = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (me.Tenant == null) return NoOrganisation();
            return await next(ctx);
        });
    }

    public static readonly TenantRole[] Readers = { TenantRole.RuleAdmin, TenantRole.Designer };
    public static readonly TenantRole[] AllRoles = { TenantRole.RuleAdmin, TenantRole.Designer, TenantRole.AnalyticsViewer };
}

public static class Audit
{
    /// <summary>Queues an audit record; it is persisted with the caller's next SaveChanges.</summary>
    public static void Log(this AppDbContext db, HttpContext http, string action, string entityType, object? entityId, string? summary = null)
    {
        var me = http.RequestServices.GetRequiredService<CurrentUser>();
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = me.UserId, ActorName = me.Name, Action = action, EntityType = entityType,
            EntityId = entityId?.ToString(), Summary = summary, Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
    }
}
