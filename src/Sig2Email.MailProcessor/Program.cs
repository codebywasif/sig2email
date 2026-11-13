using Microsoft.Extensions.Options;
using Sig2Email.Data;
using Sig2Email.MailProcessor;
using Sig2Email.MailProcessor.Smtp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSig2EmailData(builder.Configuration, builder.Environment);
// Only the listening port and connection limits come from configuration; host name, TLS policy, certificate and
// relay behaviour are edited in the portal (Settings → System) and applied while the service runs.
builder.Services.Configure<SmtpServerOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<RelayClient>();
builder.Services.AddSingleton<MailPipeline>();
builder.Services.AddSingleton(sp => new SmtpServer(sp.GetRequiredService<IOptions<SmtpServerOptions>>().Value, sp.GetRequiredService<MailPipeline>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Sig2Email.Smtp")));
builder.Services.AddSingleton<SmtpSettingsApplier>();
builder.Services.AddHostedService<SmtpHost>();

var app = builder.Build();
await app.Services.InitializeDatabaseAsync(schemaOwner: false);

// Liveness for load balancers / orchestrators. Exchange fails over between smart hosts on its own.
app.MapGet("/healthz", (MailPipeline p) => Results.Ok(new
{
    status = "ok",
    processed = Interlocked.Read(ref p.Processed),
    stamped = Interlocked.Read(ref p.Stamped),
    failed = Interlocked.Read(ref p.Failed),
}));

app.Run();

/// <summary>Copies the portal's system settings onto the running SMTP server and relay client.</summary>
public sealed class SmtpSettingsApplier(SettingsProvider settings, IOptions<SmtpServerOptions> options, RelayClient relay, ILogger<SmtpSettingsApplier> log)
{
    private long _applied = -1;
    private string? _pfx;

    public void Apply()
    {
        if (settings.Version == _applied) return;
        var s = settings.Current;
        var o = options.Value;
        o.HostName = string.IsNullOrWhiteSpace(s.SmtpHostName) ? "sig2email.local" : s.SmtpHostName;
        o.RequireTls = s.SmtpRequireTls;
        o.RequireClientCertificate = s.SmtpRequireClientCertificate;
        o.AllowedClientNames = s.SmtpAllowedClientNames.ToList();
        o.MaxMessageBytes = Math.Clamp(s.SmtpMaxMessageMb, 1, 150) * 1024L * 1024L;

        var pfx = string.IsNullOrEmpty(s.SmtpCertificatePfx) ? null : s.SmtpCertificatePfx + "|" + s.SmtpCertificatePassword;
        if (pfx != _pfx)
        {
            try
            {
                if (pfx == null)
                {
                    o.Certificate = null;
                    o.CertificateChain = null;
                    log.LogWarning("The TLS certificate was removed; STARTTLS is no longer offered");
                }
                else
                {
                    var (leaf, chain) = Pfx.Load(Convert.FromBase64String(s.SmtpCertificatePfx!), s.SmtpCertificatePassword);
                    o.CertificateChain = chain;
                    o.Certificate = leaf;
                    log.LogInformation("TLS certificate in use: {Subject}, valid until {NotAfter:yyyy-MM-dd}", leaf.Subject, leaf.NotAfter);
                }
                // The same certificate identifies us to Exchange Online's inbound connector when handing mail back.
                relay.ClientCertificate = o.Certificate;
                _pfx = pfx;
            }
            catch (Exception e)
            {
                log.LogError("The stored TLS certificate could not be loaded, keeping the previous one: {Message}", e.Message);
            }
        }
        _applied = settings.Version;
    }
}

public sealed class SmtpHost(SmtpServer server, SmtpSettingsApplier applier) : BackgroundService
{
    public override Task StartAsync(CancellationToken ct)
    {
        applier.Apply();
        server.Start(); // fail fast on a busy port
        return base.StartAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stop).ContinueWith(_ => { }, CancellationToken.None);
                applier.Apply();
            }
        }, CancellationToken.None);
        return server.RunAsync(stop);
    }
}

public partial class Program;
