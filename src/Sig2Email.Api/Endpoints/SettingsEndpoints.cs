using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sig2Email.Core.Models;
using Sig2Email.Data;

namespace Sig2Email.Api.Endpoints;

/// <summary>Settings → System in the portal: everything that used to live in configuration files. Owners only.</summary>
public static class SettingsEndpoints
{
    /// <summary>Secrets are write-only: null keeps the stored value, an empty string clears it.</summary>
    public record SettingsUpdate(
        string? PublicBaseUrl, string? EntraTenantId, string? EntraClientId, string? EntraClientSecret,
        string? SmtpHostName, bool SmtpRequireTls, bool SmtpRequireClientCertificate, List<string>? SmtpAllowedClientNames, int SmtpMaxMessageMb,
        int RelayPort, bool RelayRequireTls,
        string? AlertMailHost, int AlertMailPort, string? AlertMailUser, string? AlertMailPassword, string? AlertMailFrom,
        int SyncIntervalMinutes, int AutoReplyIntervalSeconds);

    public record TestMail(string? To);

    private static IResult Bad(string error, string message) => Results.BadRequest(new { error, message });

    private static object? CertificateInfo(SystemSettings s)
    {
        if (string.IsNullOrEmpty(s.SmtpCertificatePfx)) return null;
        try
        {
            var (leaf, chain) = Pfx.Load(Convert.FromBase64String(s.SmtpCertificatePfx), s.SmtpCertificatePassword);
            try
            {
                return new
                {
                    subject = leaf.GetNameInfo(X509NameType.SimpleName, false), issuer = leaf.GetNameInfo(X509NameType.SimpleName, true),
                    names = DnsNames(leaf), notBefore = leaf.NotBefore.ToUniversalTime(), notAfter = leaf.NotAfter.ToUniversalTime(),
                    thumbprint = leaf.Thumbprint, intermediates = chain.Count,
                };
            }
            finally
            {
                leaf.Dispose();
                foreach (var c in chain) c.Dispose();
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return new { subject = "(unreadable certificate)", issuer = "", names = Array.Empty<string>(), notBefore = DateTime.MinValue, notAfter = DateTime.MinValue, thumbprint = "", intermediates = 0 };
        }
    }

    private static string[] DnsNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san != null) names.AddRange(san.EnumerateDnsNames());
        var cn = cert.GetNameInfo(X509NameType.DnsName, false);
        if (!string.IsNullOrEmpty(cn) && !names.Contains(cn, StringComparer.OrdinalIgnoreCase)) names.Insert(0, cn);
        return names.ToArray();
    }

    private static object View(SystemSettings s, Tenant t) => new
    {
        publicBaseUrl = s.BaseUrl,
        entraTenantId = t.EntraTenantId, entraClientId = s.EntraClientId ?? "", entraClientSecretSet = !string.IsNullOrEmpty(s.EntraClientSecret),
        connected = t.ConsentGranted && s.EntraConfigured, lastSyncAt = t.LastSyncAt, lastSyncError = t.LastSyncError,
        graphPermissions = TenantEndpoints.GraphPermissions,
        smtpHostName = s.SmtpHostName, smtpRequireTls = s.SmtpRequireTls, smtpRequireClientCertificate = s.SmtpRequireClientCertificate,
        smtpAllowedClientNames = s.SmtpAllowedClientNames, smtpMaxMessageMb = s.SmtpMaxMessageMb, certificate = CertificateInfo(s),
        relayPort = s.RelayPort, relayRequireTls = s.RelayRequireTls,
        // Only ever set by the development demo; shown so nobody is surprised by it.
        developmentOverrides = s.SenderDomainIdentification || !string.IsNullOrEmpty(s.RelayOverrideHost),
        alertMailHost = s.AlertMailHost ?? "", alertMailPort = s.AlertMailPort, alertMailUser = s.AlertMailUser ?? "",
        alertMailPasswordSet = !string.IsNullOrEmpty(s.AlertMailPassword), alertMailFrom = s.AlertMailFrom ?? "",
        syncIntervalMinutes = s.SyncIntervalMinutes, autoReplyIntervalSeconds = s.AutoReplyIntervalSeconds,
        addinManifestUrl = $"{s.BaseUrl}/addin/manifest.xml", addinRedirectUri = AddinRedirectUri(s),
    };

    private static string AddinRedirectUri(SystemSettings s) =>
        Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var u) ? $"brk-multihub://{u.Authority}" : "";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", async (CurrentUser me, AppDbContext db, SettingsProvider settings) =>
            View(await settings.GetAsync(), await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == me.Tenant!.Id))).RequireRoles();

        app.MapPut("/api/settings", async (SettingsUpdate req, CurrentUser me, AppDbContext db, SettingsProvider settings, RuntimeProvider runtime,
            IHostEnvironment env, HttpContext http) =>
        {
            if (!Uri.TryCreate(req.PublicBaseUrl?.Trim(), UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https") ||
                baseUrl.AbsolutePath != "/" || baseUrl.Query.Length > 0)
                return Bad("invalid_public_url", "The public address must look like https://signatures.example.com (no path).");
            var entraTenant = Clean(req.EntraTenantId)?.ToLowerInvariant() ?? "";
            if (entraTenant.Length > 0 && !Guid.TryParse(entraTenant, out _)) return Bad("invalid_tenant_id", "The directory (tenant) ID must be a GUID.");
            var clientId = Clean(req.EntraClientId)?.ToLowerInvariant();
            if (clientId != null && !Guid.TryParse(clientId, out _)) return Bad("invalid_client_id", "The application (client) ID must be a GUID.");
            var smtpHost = Clean(req.SmtpHostName) ?? "";
            if (smtpHost.Length > 0 && Uri.CheckHostName(smtpHost) != UriHostNameType.Dns) return Bad("invalid_smtp_host", "The smart-host name must be a DNS name such as smtp.example.com.");
            var names = (req.SmtpAllowedClientNames ?? new()).Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToList();
            if (names.Any(n => Uri.CheckHostName(n) != UriHostNameType.Dns)) return Bad("invalid_client_name", "Accepted client certificate names must be DNS names.");
            if (req.SmtpRequireClientCertificate && names.Count == 0) return Bad("client_name_required", "List at least one accepted client certificate name, or turn the requirement off.");
            if (req.SmtpMaxMessageMb is < 1 or > 150) return Bad("invalid_size", "The maximum message size must be between 1 and 150 MB.");
            if (req.RelayPort is < 1 or > 65535 || req.AlertMailPort is < 1 or > 65535) return Bad("invalid_port", "Ports must be between 1 and 65535.");
            if (req.SyncIntervalMinutes is < 5 or > 1440) return Bad("invalid_interval", "The directory sync interval must be between 5 minutes and 24 hours.");
            if (req.AutoReplyIntervalSeconds is < 30 or > 3600) return Bad("invalid_interval", "The auto-reply check interval must be between 30 seconds and 1 hour.");
            var alertHost = Clean(req.AlertMailHost);
            if (alertHost != null && Uri.CheckHostName(alertHost) == UriHostNameType.Unknown) return Bad("invalid_alert_host", "The alert mail server name is not valid.");
            var alertFrom = Clean(req.AlertMailFrom);
            if (alertFrom != null && !MimeKit.MailboxAddress.TryParse(alertFrom, out _)) return Bad("invalid_alert_from", "The alert sender must be an email address.");
            // The certificate check is what stops other senders from relaying through this service.
            if (!env.IsDevelopment() && req.SmtpRequireClientCertificate && !req.SmtpRequireTls)
                return Bad("tls_required", "Client certificates can only be checked on encrypted connections; keep TLS required.");

            var before = settings.Current;
            var tenant = await db.Tenants.FirstAsync(t => t.Id == me.Tenant!.Id);
            var identityChanged = tenant.EntraTenantId != entraTenant || (before.EntraClientId ?? "") != (clientId ?? "") ||
                                  (req.EntraClientSecret != null && req.EntraClientSecret != (before.EntraClientSecret ?? ""));
            if (tenant.EntraTenantId != entraTenant)
            {
                // A different directory: everything synchronised so far is re-read from scratch.
                tenant.EntraTenantId = entraTenant;
                tenant.UsersDeltaLink = tenant.GroupsDeltaLink = null;
                tenant.ManagersSyncedAt = null;
            }
            if (identityChanged)
            {
                tenant.ConsentGranted = false; // until the connection test passes again
                tenant.LastSyncError = null;
            }

            await settings.UpdateAsync(s =>
            {
                s.PublicBaseUrl = baseUrl.GetLeftPart(UriPartial.Authority);
                s.EntraClientId = clientId;
                if (req.EntraClientSecret != null) s.EntraClientSecret = Clean(req.EntraClientSecret);
                s.SmtpHostName = smtpHost;
                s.SmtpRequireTls = req.SmtpRequireTls;
                s.SmtpRequireClientCertificate = req.SmtpRequireClientCertificate;
                s.SmtpAllowedClientNames = names;
                s.SmtpMaxMessageMb = req.SmtpMaxMessageMb;
                s.RelayPort = req.RelayPort;
                s.RelayRequireTls = req.RelayRequireTls;
                s.AlertMailHost = alertHost;
                s.AlertMailPort = req.AlertMailPort;
                s.AlertMailUser = Clean(req.AlertMailUser);
                if (req.AlertMailPassword != null) s.AlertMailPassword = req.AlertMailPassword.Length == 0 ? null : req.AlertMailPassword;
                s.AlertMailFrom = alertFrom;
                s.SyncIntervalMinutes = req.SyncIntervalMinutes;
                s.AutoReplyIntervalSeconds = req.AutoReplyIntervalSeconds;
            });
            db.Log(http, "settings.updated", "SystemSettings", null,
                "System settings changed" + (identityChanged ? " (Microsoft Entra connection)" : "") + (req.AlertMailPassword != null ? " (alert mail password)" : ""));
            await db.SaveChangesAsync();
            await runtime.BumpAsync(tenant.Id); // rendered links depend on the public address
            return Results.Ok(View(settings.Current, tenant));
        }).RequireRoles();

        // Checks the Entra app registration against Microsoft Graph and, when it works, starts the directory sync.
        app.MapPost("/api/settings/test-entra", async (CurrentUser me, AppDbContext db, SettingsProvider settings, IHttpClientFactory clients, HttpContext http) =>
        {
            var s = await settings.GetAsync();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == me.Tenant!.Id);
            if (tenant.EntraTenantId.Length == 0 || !s.EntraConfigured)
                return Results.Ok(new { ok = false, message = "Enter the directory (tenant) ID, application (client) ID and client secret, save, then test again." });

            var (ok, message) = await TestEntraAsync(clients.CreateClient(), tenant.EntraTenantId, s.EntraClientId!, s.EntraClientSecret!, http.RequestAborted);
            if (ok != tenant.ConsentGranted || ok)
            {
                tenant.ConsentGranted = ok;
                if (ok)
                {
                    tenant.ConsentedAt ??= DateTime.UtcNow;
                    tenant.SyncRequested = true;
                }
                db.Log(http, ok ? "entra.connected" : "entra.connection_failed", "Tenant", tenant.Id, message);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok, message });
        }).RequireRoles();

        app.MapPost("/api/settings/test-alert-mail", async (TestMail req, CurrentUser me, SettingsProvider settings, HttpContext http) =>
        {
            var s = await settings.GetAsync();
            if (string.IsNullOrEmpty(s.AlertMailHost) || string.IsNullOrEmpty(s.AlertMailFrom))
                return Results.Ok(new { ok = false, message = "Enter the mail server and sender address, save, then test again." });
            if (!MimeKit.MailboxAddress.TryParse(req.To ?? "", out _)) return Bad("invalid_address", "Enter the address the test message should go to.");
            try
            {
                await AlertService.SendMailAsync(s, new[] { req.To! }, "Sig2Email test message",
                    $"This is a test of the alert mail settings, sent by {me.Name}.\n", http.RequestAborted);
                return Results.Ok(new { ok = true, message = $"Test message sent to {req.To}." });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.Ok(new { ok = false, message = $"Sending failed: {e.Message}" });
            }
        }).RequireRoles();

        // TLS certificate for the SMTP receiver (and for authenticating to Exchange Online when handing mail back).
        app.MapPost("/api/settings/certificate", async (HttpRequest request, AppDbContext db, SettingsProvider settings, HttpContext http) =>
        {
            if (!request.HasFormContentType) return Bad("file_required", "Upload a .pfx / .p12 file.");
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length is 0 or > 256 * 1024) return Bad("file_required", "Upload a .pfx / .p12 file (at most 256 KB).");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            var password = form["password"].ToString();
            string summary;
            try
            {
                var (leaf, chain) = Pfx.Load(bytes, password);
                summary = $"{leaf.GetNameInfo(X509NameType.SimpleName, false)}, valid until {leaf.NotAfter.ToUniversalTime():yyyy-MM-dd}";
                var expired = leaf.NotAfter < DateTime.Now;
                leaf.Dispose();
                foreach (var c in chain) c.Dispose();
                if (expired) return Bad("certificate_expired", "That certificate has expired.");
            }
            catch (CryptographicException)
            {
                return Bad("invalid_certificate", "The file could not be opened. Check that it is a PKCS#12 (.pfx) file containing the private key and that the password is right.");
            }
            await settings.UpdateAsync(s =>
            {
                s.SmtpCertificatePfx = Convert.ToBase64String(bytes);
                s.SmtpCertificatePassword = password.Length == 0 ? null : password;
            });
            db.Log(http, "settings.certificate_uploaded", "SystemSettings", null, summary);
            await db.SaveChangesAsync();
            return Results.Ok(new { certificate = CertificateInfo(settings.Current) });
        }).RequireRoles();

        app.MapDelete("/api/settings/certificate", async (AppDbContext db, SettingsProvider settings, HttpContext http) =>
        {
            await settings.UpdateAsync(s => s.SmtpCertificatePfx = s.SmtpCertificatePassword = null);
            db.Log(http, "settings.certificate_removed", "SystemSettings", null);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRoles();

        // ----- Outlook add-in, served from this origin with the current settings baked in at request time -----
        app.MapGet("/addin/{file}", async (string file, SettingsProvider settings, IOptions<Sig2EmailOptions> o, HttpContext http) =>
        {
            var dir = AddinDir(o.Value);
            var s = settings.Current;
            var clientId = string.IsNullOrWhiteSpace(s.EntraClientId) ? "00000000-0000-0000-0000-000000000000" : s.EntraClientId;
            http.Response.Headers.CacheControl = "no-cache";
            switch (file)
            {
                case "manifest.xml":
                {
                    var path = dir == null ? null : Path.Combine(dir, "manifest.template.xml");
                    if (path == null || !File.Exists(path)) return Results.NotFound();
                    var xml = (await File.ReadAllTextAsync(path))
                        .Replace("{{ADDIN_URL}}", WebUtility.HtmlEncode(s.BaseUrl + "/addin")).Replace("{{API_URL}}", WebUtility.HtmlEncode(s.BaseUrl))
                        .Replace("{{CLIENT_ID}}", clientId);
                    return Results.Text(xml, "application/xml", Encoding.UTF8);
                }
                case "launchevent.js" or "taskpane.js":
                {
                    var path = dir == null ? null : Path.Combine(dir, file);
                    if (path == null || !File.Exists(path)) return Results.NotFound();
                    // The bundles are built once; where the API lives and which Entra app to use is prepended here.
                    var config = JsonSerializer.Serialize(new { apiUrl = s.BaseUrl, clientId });
                    return Results.Text($"globalThis.__S2E__={config};\n" + await File.ReadAllTextAsync(path), "text/javascript", Encoding.UTF8);
                }
                case "launchevent.html" or "taskpane.html":
                {
                    var path = dir == null ? null : Path.Combine(dir, file);
                    return path == null || !File.Exists(path) ? Results.NotFound() : Results.Text(await File.ReadAllTextAsync(path), "text/html", Encoding.UTF8);
                }
                default:
                    return Results.NotFound();
            }
        });
    }

    /// <summary>The built add-in: next to the published API (container image) or in the source tree (development).</summary>
    private static string? AddinDir(Sig2EmailOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.AddinDir)) return o.AddinDir;
        var published = Path.Combine(AppContext.BaseDirectory, "addin");
        if (Directory.Exists(published)) return published;
        var root = Sig2EmailOptions.SolutionRoot();
        var source = root == null ? null : Path.Combine(root, "web", "addin", "dist");
        return source != null && Directory.Exists(source) ? source : null;
    }

    /// <summary>Gets an app-only token and probes the Graph permissions the worker needs.</summary>
    internal static async Task<(bool Ok, string Message)> TestEntraAsync(HttpClient http, string tenantId, string clientId, string secret, CancellationToken ct)
    {
        http.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            using var tokenResponse = await http.PostAsync($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["client_secret"] = secret, ["grant_type"] = "client_credentials", ["scope"] = "https://graph.microsoft.com/.default",
            }), ct);
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
            if (!tokenResponse.IsSuccessStatusCode)
            {
                var description = tokenJson.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
                var firstLine = description.Split('\n', '\r')[0];
                return (false, $"Microsoft Entra rejected the sign-in: {(firstLine.Length > 300 ? firstLine[..300] : firstLine)}");
            }
            var token = tokenJson.RootElement.GetProperty("access_token").GetString();

            var missing = new List<string>();
            foreach (var (permission, path) in new[]
                     {
                         ("User.Read.All", "users?$top=1&$select=id"), ("Group.Read.All", "groups?$top=1&$select=id"),
                         ("Organization.Read.All", "organization?$select=id"),
                     })
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/" + path);
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var result = await http.SendAsync(probe, ct);
                if (result.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) missing.Add(permission);
                else if (!result.IsSuccessStatusCode) return (false, $"Microsoft Graph returned {(int)result.StatusCode} for {path.Split('?')[0]}.");
            }
            return missing.Count == 0
                ? (true, "Connected. The directory sync has been queued. (MailboxSettings.ReadWrite is only exercised when an auto-reply is applied.)")
                : (false, $"Signed in, but these application permissions are missing or lack admin consent: {string.Join(", ", missing)}. Grant them in the Entra admin center under API permissions.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, $"Could not reach Microsoft: {e.Message}");
        }
    }
}
