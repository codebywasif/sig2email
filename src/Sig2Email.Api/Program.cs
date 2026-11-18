using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Sig2Email.Api;
using Sig2Email.Api.Endpoints;
using Sig2Email.Data;

// One-off command for certificate-renewal hooks; everything else starts the portal/API.
if (args.Length > 0 && args[0] == CertificateInstaller.Command)
    return await CertificateInstaller.RunAsync(Console.OpenStandardInput(), Console.Out, Console.Error);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSig2EmailData(builder.Configuration, builder.Environment);
builder.Services.AddSig2EmailAuth();
builder.Services.AddSingleton<SetupCode>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertService>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("public", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
    // Sign-in, setup and password changes: slows password guessing from one address (accounts also lock themselves).
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.CustomSchemaIds(t => t.FullName?.Replace('+', '.')));

var app = builder.Build();
await app.Services.InitializeDatabaseAsync(schemaOwner: true);
await app.Services.GetRequiredService<SetupCode>().AnnounceIfNeededAsync(app.Services);

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseTenantBinding();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapAccountEndpoints();
app.MapSettingsEndpoints();
app.MapTenantEndpoints();
app.MapDesignEndpoints();
app.MapInsightEndpoints();
app.MapPublicEndpoints();
// When the built portal is copied into wwwroot the API serves it as a single-page app.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html"))) app.MapFallbackToFile("index.html");

app.Run();
return 0;

public partial class Program;
