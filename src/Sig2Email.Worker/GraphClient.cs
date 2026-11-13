using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Sig2Email.Data;

namespace Sig2Email.Worker;

public sealed class GraphException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public interface IGraphClient
{
    bool IsConfigured { get; }
    /// <summary>GET returning JSON. <paramref name="urlOrPath"/> is a path under /v1.0 or an absolute next/delta link.</summary>
    Task<JsonDocument> GetAsync(string entraTenantId, string urlOrPath, CancellationToken ct);
    /// <summary>Returns null when the resource does not exist (404).</summary>
    Task<(byte[] Bytes, string ContentType)?> GetBytesAsync(string entraTenantId, string path, CancellationToken ct);
    /// <summary>PATCH/POST/DELETE; returns the response JSON if there is one.</summary>
    Task<JsonDocument?> SendAsync(string entraTenantId, HttpMethod method, string path, object? body, CancellationToken ct);
}

/// <summary>Thin Microsoft Graph client using app-only (client credentials) tokens per customer tenant.</summary>
public sealed class GraphClient(HttpClient http, SettingsProvider settings, ILogger<GraphClient> log) : IGraphClient
{
    private const string Root = "https://graph.microsoft.com/v1.0";
    private readonly ConcurrentDictionary<string, IConfidentialClientApplication> _apps = new();

    public bool IsConfigured => settings.Current.EntraConfigured;

    private async Task<string> TokenAsync(string tenant, CancellationToken ct)
    {
        // The app registration is edited in the portal, so the MSAL client is rebuilt whenever it changes.
        var s = settings.Current;
        if (!s.EntraConfigured) throw new InvalidOperationException("The Microsoft Entra app registration is not configured (Settings → System).");
        var key = $"{tenant}|{s.EntraClientId}|{s.EntraClientSecret!.GetHashCode()}";
        if (!_apps.ContainsKey(key)) _apps.Clear();
        var app = _apps.GetOrAdd(key, _ => ConfidentialClientApplicationBuilder.Create(s.EntraClientId)
            .WithClientSecret(s.EntraClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            .Build());
        // MSAL caches and refreshes app tokens internally.
        return (await app.AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" }).ExecuteAsync(ct)).AccessToken;
    }

    private async Task<HttpResponseMessage> ExecuteAsync(string tenant, HttpMethod method, string urlOrPath, object? body, CancellationToken ct)
    {
        var url = urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? urlOrPath : Root + urlOrPath;
        if (!url.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to send a Graph token to a non-Graph host.");

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(tenant, ct));
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await http.SendAsync(req, ct);
            if ((res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) && attempt < 4)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                log.LogInformation("Graph returned {Status}; retrying in {Seconds}s", (int)res.StatusCode, wait.TotalSeconds);
                res.Dispose();
                await Task.Delay(wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait, ct);
                continue;
            }
            return res;
        }
    }

    private static async Task<GraphException> ErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        return new GraphException(res.StatusCode, $"Graph {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
    }

    public async Task<JsonDocument> GetAsync(string entraTenantId, string urlOrPath, CancellationToken ct)
    {
        using var res = await ExecuteAsync(entraTenantId, HttpMethod.Get, urlOrPath, null, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetBytesAsync(string entraTenantId, string path, CancellationToken ct)
    {
        using var res = await ExecuteAsync(entraTenantId, HttpMethod.Get, path, null, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        return (await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    public async Task<JsonDocument?> SendAsync(string entraTenantId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var res = await ExecuteAsync(entraTenantId, method, path, body, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0) return null;
        return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
