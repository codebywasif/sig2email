using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Sig2Email.Core.Mail;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rules;
using Sig2Email.Data;
using Sig2Email.MailProcessor.Smtp;

namespace Sig2Email.MailProcessor;

/// <summary>Pool of outbound SMTP connections; MailKit clients are single-threaded so each send rents one.</summary>
public sealed class RelayClient(SettingsProvider settings, ILogger<RelayClient> log) : IDisposable
{
    private const long MaxIdleMs = 45_000;
    private const int MaxPooledConnectionsPerHost = 50;
    private readonly ConcurrentDictionary<string, ConcurrentBag<(SmtpClient Client, long LastUsed)>> _pools = new();
    public X509Certificate2? ClientCertificate { get; set; }

    public async Task SendAsync(string host, MimeMessage message, string mailFrom, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var o = settings.Current;
        if (!string.IsNullOrEmpty(o.RelayOverrideHost)) host = o.RelayOverrideHost;
        // Port and TLS are part of the key, so a settings change never reuses connections made under the old settings.
        var pool = _pools.GetOrAdd($"{host}:{o.RelayPort}:{o.RelayRequireTls}", _ => new ConcurrentBag<(SmtpClient, long)>());
        var sender = new MailboxAddress("", mailFrom);
        var rcpts = recipients.Select(r => new MailboxAddress("", r)).ToList();
        var format = FormatOptions.Default.Clone();
        format.International = true;

        var allowPooled = true;
        while (true)
        {
            SmtpClient? client = null;
            var pooled = false;
            while (allowPooled && pool.TryTake(out var entry))
            {
                // Exchange drops idle connections, so anything idle for long is not trusted.
                if (entry.Client.IsConnected && Environment.TickCount64 - entry.LastUsed < MaxIdleMs) { client = entry.Client; pooled = true; break; }
                entry.Client.Dispose();
            }
            client ??= await ConnectAsync(host, o, ct);
            try
            {
                await client.SendAsync(format, new SendRequest(message, sender, rcpts), ct);
                if (pool.Count < MaxPooledConnectionsPerHost) pool.Add((client, Environment.TickCount64));
                else _ = DisposeAsync(client);
                return;
            }
            catch (Exception e) when (pooled && e is IOException or ServiceNotConnectedException or SmtpProtocolException)
            {
                // The pooled connection had gone stale (peer closed it while idle): retry once on a brand-new one,
                // never on another pooled connection that may be just as stale.
                client.Dispose();
                allowPooled = false;
                log.LogDebug("Stale relay connection to {Host}, retrying on a fresh connection", host);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private async Task<SmtpClient> ConnectAsync(string host, SystemSettings o, CancellationToken ct)
    {
        var client = new SmtpClient { Timeout = 60_000 };
        try
        {
            if (ClientCertificate != null) client.ClientCertificates = new X509CertificateCollection { ClientCertificate };
            if (!o.RelayRequireTls) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(host, o.RelayPort, o.RelayRequireTls ? SecureSocketOptions.StartTls : SecureSocketOptions.StartTlsWhenAvailable, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task DisposeAsync(SmtpClient client)
    {
        try { await client.DisconnectAsync(true); } catch { /* ignore */ }
        client.Dispose();
    }

    public void Dispose()
    {
        foreach (var pool in _pools.Values)
            while (pool.TryTake(out var c)) c.Client.Dispose();
    }
}

/// <summary>
/// Receives a message from Exchange Online, stamps the signatures its rules call for and hands it straight back.
/// Fail-open: any error while stamping relays the original message unchanged. Message content is never written
/// to disk or logged.
/// </summary>
public sealed class MailPipeline(
    RuntimeProvider runtime, SignatureService signatures, RelayClient relay, MailStatsCollector stats,
    SettingsProvider settings, RuntimeEnvironment environment, ILogger<MailPipeline> log) : IMessageHandler
{
    private const string TenantHeader = "X-MS-Exchange-CrossTenant-Id";

    public long Processed;
    public long Stamped;
    public long Failed;

    public async Task<SmtpReply> HandleAsync(SmtpEnvelope envelope, MemoryStream data, SmtpSessionInfo session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var message = await MimeMessage.LoadAsync(data, ct);
        var from = message.From.Mailboxes.FirstOrDefault()?.Address ?? envelope.MailFrom;

        // Finding the organisation by sender domain is trivially spoofable, so it is honoured in development only.
        var options = settings.Current;
        var byHeader = !(options.SenderDomainIdentification && environment.IsDevelopment);
        Guid? tenantId;
        if (byHeader)
        {
            var tid = message.Headers[TenantHeader]?.Trim();
            tenantId = string.IsNullOrEmpty(tid) ? null : await runtime.ResolveTenantByEntraIdAsync(tid, ct);
        }
        else tenantId = await runtime.ResolveTenantByDomainAsync(RuleEngine.DomainOf(from), ct);

        var rt = tenantId == null ? null : await runtime.GetAsync(tenantId.Value, ct);
        if (rt == null) return new SmtpReply(550, "5.7.1 Relaying denied: sender organisation is not registered");
        var relayHost = string.IsNullOrEmpty(options.RelayOverrideHost) ? rt.Tenant.RelayHost : options.RelayOverrideHost;
        if (string.IsNullOrEmpty(relayHost)) return new SmtpReply(451, "4.4.0 No return route configured for this organisation");

        var stamped = false;
        var failed = false;
        if (message.Headers[MimeStamper.ProcessedHeader] == null)
        {
            try
            {
                stamped = await StampAsync(message, rt, from, envelope, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Fail open: deliver exactly what we received.
                failed = true;
                log.LogError("Stamping failed for tenant {Tenant}, relaying unmodified: {Type}: {Message}", rt.Tenant.Id, e.GetType().Name, e.Message);
                data.Position = 0;
                message = await MimeMessage.LoadAsync(data, ct);
            }
            message.Headers.Add(MimeStamper.ProcessedHeader, "true");
        }

        try
        {
            await relay.SendAsync(relayHost, message, envelope.MailFrom, envelope.Recipients, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Interlocked.Increment(ref Failed);
            stats.Record(rt.Tenant.Id, false, true);
            log.LogWarning("Relay to {Host} failed for tenant {Tenant}: {Type}: {Message}", relayHost, rt.Tenant.Id, e.GetType().Name, e.Message);
            return new SmtpReply(451, "4.4.1 Could not hand the message back, please retry"); // Exchange keeps it queued
        }

        Interlocked.Increment(ref Processed);
        if (stamped) Interlocked.Increment(ref Stamped);
        if (failed) Interlocked.Increment(ref Failed);
        stats.Record(rt.Tenant.Id, stamped, failed);
        log.LogDebug("Processed message for tenant {Tenant} in {Ms} ms (stamped: {Stamped})", rt.Tenant.Id, sw.ElapsedMilliseconds, stamped);
        return new SmtpReply(250, "2.0.0 OK queued");
    }

    private async Task<bool> StampAsync(MimeMessage message, TenantRuntime rt, string from, SmtpEnvelope envelope, CancellationToken ct)
    {
        if (rt.Tenant.Mode == DeploymentMode.ClientSide || rt.Rules.Count == 0 || !MimeStamper.IsStampable(message)) return false;
        var sender = await runtime.GetUserByAddressAsync(rt.Tenant.Id, from, ct);
        if (sender == null && !from.Equals(envelope.MailFrom, StringComparison.OrdinalIgnoreCase) && envelope.MailFrom.Length > 0)
            sender = await runtime.GetUserByAddressAsync(rt.Tenant.Id, envelope.MailFrom, ct);
        if (sender == null) return false;

        var client = ClientMarker.Parse(message.Headers[MimeStamper.ClientHeader]);
        var recipients = envelope.Recipients
            .Concat(message.To.Mailboxes.Select(m => m.Address)).Concat(message.Cc.Mailboxes.Select(m => m.Address))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var result = await signatures.ComposeAsync(new ComposeRequest
        {
            Runtime = rt,
            Sender = sender,
            Channel = RuleChannels.Server,
            AlreadyApplied = client.RuleIds,
            // The user picked a signature by hand in Outlook: leave every client-capable rule alone.
            RuleFilter = client.Manual ? r => r.Channels == RuleChannels.Server : null,
            MessageId = message.MessageId,
            Facts = new MessageFacts
            {
                SenderAddress = from,
                Recipients = recipients,
                Kind = MimeStamper.DetectKind(message),
                Subject = message.Subject ?? "",
                BodyText = () => MimeStamper.GetNewBodyText(message),
            },
        }, ct);

        return MimeStamper.Apply(message, result.Parts);
    }
}
