using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sig2Email.Api;

namespace Sig2Email.Tests;

/// <summary>A brand-new installation: empty database, no demo data, nothing configured.</summary>
public sealed class FreshInstallFixture : IDisposable
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "s2e-fresh-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<CurrentUser> Factory { get; }

    public FreshInstallFixture()
    {
        var addin = Path.Combine(DataDir, "addin");
        Directory.CreateDirectory(addin);
        File.WriteAllText(Path.Combine(addin, "manifest.template.xml"), "<OfficeApp><Id>{{CLIENT_ID}}</Id><Url>{{ADDIN_URL}}/taskpane.html</Url><Api>{{API_URL}}</Api></OfficeApp>");
        File.WriteAllText(Path.Combine(addin, "taskpane.js"), "console.log('bundle');");

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        Environment.SetEnvironmentVariable("Sig2Email__DataDir", DataDir);
        Environment.SetEnvironmentVariable("Sig2Email__SeedDemoData", "false");
        Environment.SetEnvironmentVariable("Sig2Email__AddinDir", addin);
        Factory = new WebApplicationFactory<CurrentUser>();
        _ = Factory.Server;
        Environment.SetEnvironmentVariable("Sig2Email__AddinDir", null);
    }

    public void Dispose()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
    }
}

public class SetupTests(FreshInstallFixture fx) : IClassFixture<FreshInstallFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task First_run_needs_no_configuration_and_the_first_owner_is_created_with_the_setup_code()
    {
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://signatures.internal.example") });
        var config = await Json(await client.GetAsync("/api/config"));
        Assert.True(config.GetProperty("needsSetup").GetBoolean());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("demo").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/dev/login", new { userId = "u-owner" })).StatusCode); // no dev shortcuts in production

        // The installation key and the one-time setup code were generated on first start; nothing came from configuration.
        Assert.True(File.Exists(Path.Combine(fx.DataDir, "installation.key")));
        var codeFile = Path.Combine(fx.DataDir, "setup-code.txt");
        var code = File.ReadAllText(codeFile).Trim();
        Assert.Equal(20, code.Length);

        object Setup(string? setupCode, string password = "a-strong-first-password") =>
            new { setupCode, organisation = "Example Ltd", username = "Admin", displayName = "First Admin", password };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/setup", Setup(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/setup", Setup("00000000000000000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/setup", Setup(code, "short"))).StatusCode);
        Assert.True((await Json(await client.GetAsync("/api/config"))).GetProperty("needsSetup").GetBoolean());

        var token = (await Json(await client.PostAsJsonAsync("/api/auth/setup", Setup(code)))).GetProperty("token").GetString();
        Assert.False(File.Exists(codeFile));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/setup", Setup(code))).StatusCode);
        Assert.False((await Json(await client.GetAsync("/api/config"))).GetProperty("needsSetup").GetBoolean());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await Json(await client.GetAsync("/api/me"));
        Assert.Equal("Owner", me.GetProperty("role").GetString());
        Assert.Equal("admin", me.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("Example Ltd", me.GetProperty("tenant").GetProperty("name").GetString());

        // Defaults are safe (TLS and Microsoft's client certificate required) and the public address starts as the one in use.
        var settings = await Json(await client.GetAsync("/api/settings"));
        Assert.Equal("https://signatures.internal.example", settings.GetProperty("publicBaseUrl").GetString());
        Assert.True(settings.GetProperty("smtpRequireTls").GetBoolean());
        Assert.True(settings.GetProperty("smtpRequireClientCertificate").GetBoolean());
        Assert.False(settings.GetProperty("developmentOverrides").GetBoolean());
        Assert.False(settings.GetProperty("connected").GetBoolean());
        var test = await Json(await client.PostAsync("/api/settings/test-entra", null));
        Assert.False(test.GetProperty("ok").GetBoolean()); // nothing entered yet; no call to Microsoft is made

        // The Entra app is entered in the portal and reaches the add-in without rebuilding or restarting anything.
        var body = new
        {
            publicBaseUrl = "https://signatures.internal.example", entraTenantId = "11111111-2222-3333-4444-555555555555",
            entraClientId = "99999999-8888-7777-6666-555555555555", entraClientSecret = "secret-value",
            smtpHostName = "smtp.internal.example", smtpRequireTls = true, smtpRequireClientCertificate = true,
            smtpAllowedClientNames = new[] { "mail.protection.outlook.com" }, smtpMaxMessageMb = 40, relayPort = 25, relayRequireTls = true,
            alertMailPort = 587, syncIntervalMinutes = 15, autoReplyIntervalSeconds = 60,
        };
        await Json(await client.PutAsJsonAsync("/api/settings", body));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", new
        {
            body.publicBaseUrl, body.smtpHostName, smtpRequireTls = false, smtpRequireClientCertificate = true, body.smtpAllowedClientNames,
            body.smtpMaxMessageMb, body.relayPort, body.relayRequireTls, body.alertMailPort, body.syncIntervalMinutes, body.autoReplyIntervalSeconds,
        })).StatusCode);

        var anon = fx.Factory.CreateClient();
        var manifest = await (await anon.GetAsync("/addin/manifest.xml")).Content.ReadAsStringAsync();
        Assert.Contains("<Id>99999999-8888-7777-6666-555555555555</Id>", manifest);
        Assert.Contains("https://signatures.internal.example/addin/taskpane.html", manifest);
        var script = await (await anon.GetAsync("/addin/taskpane.js")).Content.ReadAsStringAsync();
        Assert.StartsWith("globalThis.__S2E__={\"apiUrl\":\"https://signatures.internal.example\",\"clientId\":\"99999999-8888-7777-6666-555555555555\"};", script);
        Assert.DoesNotContain("secret-value", script + manifest);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/addin/installation.key")).StatusCode);

        // A Microsoft-issued token for some other application is not accepted (and no local token can be forged without the key).
        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token![..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/me")).StatusCode);
    }
}
