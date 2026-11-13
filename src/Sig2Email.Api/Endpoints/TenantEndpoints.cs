using System.Text;
using Microsoft.EntityFrameworkCore;
using Sig2Email.Core.Models;
using Sig2Email.Data;

namespace Sig2Email.Api.Endpoints;

public static class TenantEndpoints
{
    public record TenantUpdate(string Name, DeploymentMode Mode, string? RelayHost);

    public const string GraphPermissions = "User.Read.All, Group.Read.All, Organization.Read.All, MailboxSettings.ReadWrite";

    public static void MapTenantEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tenant", async (CurrentUser me, AppDbContext db, SettingsProvider settings) =>
        {
            var t = me.Tenant!;
            var s = settings.Current;
            return new
            {
                t.Id, t.Name, t.Mode, t.Domains, t.RelayHost, t.LastSyncAt, t.LastSyncError, t.SyncRequested,
                // "Connected" = the Entra app registration entered under Settings → System passed its connection test.
                connected = t.ConsentGranted && s.EntraConfigured,
                entraConfigured = s.EntraConfigured && t.EntraTenantId.Length > 0,
                smtpHost = string.IsNullOrWhiteSpace(s.SmtpHostName) ? null : s.SmtpHostName,
                graphPermissions = GraphPermissions,
                counts = new
                {
                    users = await db.Users.CountAsync(),
                    groups = await db.Groups.CountAsync(),
                    templates = await db.Templates.CountAsync(),
                    rules = await db.Rules.CountAsync(r => r.Enabled),
                },
            };
        }).RequireRoles(Auth.AllRoles);

        app.MapPut("/api/tenant", async (TenantUpdate req, CurrentUser me, AppDbContext db, RuntimeProvider runtime, HttpContext http) =>
        {
            var t = await db.Tenants.FirstAsync(x => x.Id == me.Tenant!.Id);
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            var relay = string.IsNullOrWhiteSpace(req.RelayHost) ? null : req.RelayHost.Trim();
            if (relay != null && Uri.CheckHostName(relay) == UriHostNameType.Unknown) return Results.BadRequest(new { error = "invalid_relay_host" });
            t.Name = req.Name.Trim();
            t.Mode = req.Mode;
            t.RelayHost = relay;
            db.Log(http, "tenant.updated", "Tenant", t.Id, $"Mode {t.Mode}, relay {t.RelayHost ?? "(none)"}");
            await db.SaveChangesAsync();
            await runtime.BumpAsync(t.Id);
            return Results.NoContent();
        }).RequireRoles();

        app.MapPost("/api/tenant/sync", async (CurrentUser me, AppDbContext db, HttpContext http) =>
        {
            await db.Tenants.Where(t => t.Id == me.Tenant!.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.SyncRequested, true));
            db.Log(http, "directory.sync_requested", "Tenant", me.Tenant!.Id);
            await db.SaveChangesAsync();
            return Results.Accepted();
        }).RequireRoles(TenantRole.RuleAdmin);

        app.MapGet("/api/tenant/provisioning-script", async (string? groupId, bool? remove, AppDbContext db, SettingsProvider settings) =>
        {
            if (remove == true) return Results.Text(ProvisioningScript.Remove(), "text/plain", Encoding.UTF8);
            var host = settings.Current.SmtpHostName;
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))
                return Results.BadRequest(new { error = "smtp_host_not_set", message = "Enter the public smart-host name under Settings → System → Mail routing first." });
            string? groupMail = null;
            if (!string.IsNullOrEmpty(groupId))
            {
                groupMail = await db.Groups.Where(g => g.Id == groupId).Select(g => g.Mail).FirstOrDefaultAsync();
                if (string.IsNullOrEmpty(groupMail)) return Results.BadRequest(new { error = "group_has_no_mail_address" });
            }
            return Results.Text(ProvisioningScript.Build(host, groupMail), "text/plain", Encoding.UTF8);
        }).RequireRoles();

        // ----- directory -----
        app.MapGet("/api/directory/users", async (string? search, int? skip, int? take, AppDbContext db) =>
        {
            var q = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                q = q.Where(u => u.DisplayName.ToLower().Contains(s) || u.Upn.ToLower().Contains(s) ||
                                 (u.Department != null && u.Department.ToLower().Contains(s)) || (u.JobTitle != null && u.JobTitle.ToLower().Contains(s)));
            }
            var total = await q.CountAsync();
            var items = await q.OrderBy(u => u.DisplayName).Skip(skip ?? 0).Take(Math.Clamp(take ?? 50, 1, 200)).ToListAsync();
            return new { total, items };
        }).RequireRoles(Auth.Readers);

        app.MapGet("/api/directory/users/{id}", async (string id, AppDbContext db, SignatureService sig) =>
        {
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return u == null ? Results.NotFound() : Results.Ok(new { user = u, photoUrl = sig.PhotoUrl(u) });
        }).RequireRoles(Auth.Readers);

        app.MapGet("/api/directory/groups", async (string? search, AppDbContext db) =>
        {
            var q = db.Groups.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                q = q.Where(g => g.DisplayName.ToLower().Contains(s));
            }
            var groups = await q.OrderBy(g => g.DisplayName).Take(500).ToListAsync();
            return groups.Select(g => new { g.Id, g.DisplayName, g.Mail, g.Kind, memberCount = g.MemberIds.Count });
        }).RequireRoles(Auth.Readers);
    }
}
