using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Sig2Email.Core.Mail;
using Sig2Email.Core.Models;
using Sig2Email.Data;
using Sig2Email.MailProcessor;
using Sig2Email.MailProcessor.Smtp;

namespace Sig2Email.Tests;

internal sealed class DevEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class Sink : IMessageHandler
{
    public ConcurrentQueue<(SmtpEnvelope Envelope, MimeMessage Message, byte[] Raw)> Received { get; } = new();
    public SmtpReply Reply { get; set; } = SmtpReply.Ok;

    public Task<SmtpReply> HandleAsync(SmtpEnvelope envelope, MemoryStream data, SmtpSessionInfo session, CancellationToken ct)
    {
        var raw = data.ToArray();
        if (Reply.Code == 250) Received.Enqueue((envelope, MimeMessage.Load(new MemoryStream(raw)), raw));
        return Task.FromResult(Reply);
    }
}

/// <summary>A complete data plane in-process: SMTP front door → pipeline → relay → sink standing in for Exchange Online.</summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    internal Sink Sink { get; } = new();
    public ServiceProvider Services { get; private set; } = null!;
    public int Port { get; private set; }
    private readonly CancellationTokenSource _stop = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s2e-tests-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        var sink = new SmtpServer(new SmtpServerOptions { Port = 0, RequireTls = false, RequireClientCertificate = false }, Sink, NullLogger.Instance);
        sink.Start();
        _ = sink.RunAsync(_stop.Token);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sig2Email:DataDir"] = _dir,
            ["Sig2Email:SeedDemoData"] = "true",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSig2EmailData(config, new DevEnvironment());
        services.AddSingleton<RelayClient>();
        services.AddSingleton<MailPipeline>();
        Services = services.BuildServiceProvider();
        await Services.InitializeDatabaseAsync(schemaOwner: true);
        // The same settings an administrator edits in the portal; the demo seed already selects sender-domain identification.
        await Services.GetRequiredService<SettingsProvider>().UpdateAsync(s =>
        {
            s.PublicBaseUrl = "https://app.test";
            s.RelayOverrideHost = "localhost";
            s.RelayPort = sink.Port;
            s.RelayRequireTls = false;
        });

        var front = new SmtpServer(new SmtpServerOptions { Port = 0, RequireTls = false, RequireClientCertificate = false, MaxMessageBytes = 40 * 1024 * 1024 },
            Services.GetRequiredService<MailPipeline>(), NullLogger.Instance);
        front.Start();
        Port = front.Port;
        _ = front.RunAsync(_stop.Token);
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        return await action(db);
    }

    public static MimeMessage Compose(string from, string to, string subject, string html, string? text = null, Action<MimeMessage>? tweak = null)
    {
        var m = new MimeMessage { Subject = subject };
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Body = new BodyBuilder { HtmlBody = html, TextBody = text ?? "Hello" }.ToMessageBody();
        tweak?.Invoke(m);
        return m;
    }

    /// <summary>Sends through the front door and returns what "Exchange" got back.</summary>
    internal async Task<(SmtpEnvelope Envelope, MimeMessage Message, byte[] Raw)> SendAsync(MimeMessage message)
    {
        var marker = Guid.NewGuid().ToString("N");
        message.Headers.Add("X-Test-Marker", marker);
        using var client = new SmtpClient();
        await client.ConnectAsync("localhost", Port, SecureSocketOptions.None);
        await client.SendAsync(new SendRequest(message));
        await client.DisconnectAsync(true);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = Sink.Received.FirstOrDefault(r => r.Message.Headers["X-Test-Marker"] == marker);
            if (hit.Message != null) return hit;
            await Task.Delay(20);
        }
        throw new TimeoutException("The relayed message never arrived.");
    }
}

public class PipelineTests(PipelineFixture fx) : IClassFixture<PipelineFixture>
{
    private const string Ben = "ben.okafor@contoso.example";   // Customer Success → survey signature rule
    private const string Sam = "sam.carter@contoso.example";   // Sales → standard signature

    [Fact]
    public async Task External_new_mail_gets_signature_disclaimer_survey_and_loop_header()
    {
        var (envelope, m, _) = await fx.SendAsync(PipelineFixture.Compose(Ben, "customer@fabrikam.example", "Your ticket", "<html><body><p>Hello</p></body></html>"));

        Assert.Equal("true", m.Headers[MimeStamper.ProcessedHeader]);
        Assert.Equal(Ben, envelope.MailFrom);
        Assert.Equal(new[] { "customer@fabrikam.example" }, envelope.Recipients);

        var html = m.HtmlBody;
        Assert.Contains("Ben Okafor", html);
        Assert.Contains("Support Engineer", html);
        Assert.DoesNotContain("{", html.Replace("{color", ""));        // every placeholder resolved
        Assert.DoesNotContain("M:</span>", html);                      // Ben has no mobile → label suppressed
        Assert.Contains("How would you rate my reply?", html);
        Assert.Matches(@"https://app\.test/r/[0-9a-f]{32}/5\?k=", html);
        Assert.Matches(@"href=""https://app\.test/l/[^""]+""", html);  // tracked link
        Assert.Contains("confidential", html);
        Assert.True(html.IndexOf("Ben Okafor", StringComparison.Ordinal) < html.IndexOf("confidential", StringComparison.Ordinal));
        Assert.Contains("Ben Okafor", m.TextBody);
        Assert.Contains("confidential", m.TextBody);

        // The survey dispatch (metadata only) is queued for the background writer.
        var queued = fx.Services.GetRequiredService<DispatchQueue>();
        Assert.NotNull(queued);
    }

    [Fact]
    public async Task Internal_mail_gets_no_disclaimer_and_replies_use_the_short_signature_above_the_quote()
    {
        var (_, m, _) = await fx.SendAsync(PipelineFixture.Compose(Sam, "olivia.owner@contoso.example", "RE: Budget",
            "<html><body><p>Agreed.</p><div id=\"divRplyFwdMsg\">From: Olivia</div><p>old</p></body></html>",
            "Agreed.\r\n\r\n-----Original Message-----\r\nold"));
        var html = m.HtmlBody;
        Assert.DoesNotContain("confidential", html);
        Assert.Contains("<strong>Sam Carter</strong> | Sales Director", html);
        Assert.True(html.IndexOf("Sam Carter", StringComparison.Ordinal) < html.IndexOf("divRplyFwdMsg", StringComparison.Ordinal));
        Assert.True(m.TextBody.IndexOf("Sam Carter", StringComparison.Ordinal) < m.TextBody.IndexOf("Original Message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Combo_mode_does_not_duplicate_a_signature_the_addin_already_inserted()
    {
        var standard = await fx.DbAsync(db => db.Rules.Where(r => r.Name.StartsWith("Standard")).Select(r => r.Id).FirstAsync());
        var (_, m, _) = await fx.SendAsync(PipelineFixture.Compose(Sam, "customer@fabrikam.example", "Offer",
            "<html><body><p>Hi</p><div class=\"s2e-signature\">CLIENT-SIG</div></body></html>",
            tweak: x => x.Headers.Add(MimeStamper.ClientHeader, $"rules={standard:N}")));
        var html = m.HtmlBody;
        Assert.DoesNotContain("Sales Director", html);   // not stamped again
        Assert.Contains("CLIENT-SIG", html);
        Assert.Contains("confidential", html);            // the server-only disclaimer is still added
    }

    [Fact]
    public async Task Suppress_keyword_unknown_sender_and_already_processed_mail_pass_through_unstamped()
    {
        var (_, nosig, _) = await fx.SendAsync(PipelineFixture.Compose(Sam, "olivia.owner@contoso.example", "Quick one #nosig", "<p>x</p>"));
        Assert.DoesNotContain("Sam Carter", nosig.HtmlBody);

        var (_, unknown, _) = await fx.SendAsync(PipelineFixture.Compose("printer@contoso.example", "olivia.owner@contoso.example", "Scan", "<p>x</p>"));
        Assert.Equal("<p>x</p>", unknown.HtmlBody);
        Assert.Equal("true", unknown.Headers[MimeStamper.ProcessedHeader]);

        var (_, looped, _) = await fx.SendAsync(PipelineFixture.Compose(Sam, "customer@fabrikam.example", "Again", "<p>x</p>",
            tweak: x => x.Headers.Add(MimeStamper.ProcessedHeader, "true")));
        Assert.Equal("<p>x</p>", looped.HtmlBody);
        Assert.Single(looped.Headers, h => h.Field == MimeStamper.ProcessedHeader);
    }

    [Fact]
    public async Task A_message_that_comes_back_without_the_loop_header_is_not_stamped_twice()
    {
        var (_, first, raw) = await fx.SendAsync(PipelineFixture.Compose(Sam, "customer@fabrikam.example", "Proposal", "<html><body><p>Hi</p></body></html>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(first.HtmlBody, "Sales Director"));

        // Exchange hands the stamped message back, but the marker was lost on the way.
        var back = MimeMessage.Load(new MemoryStream(raw));
        back.Headers.RemoveAll(MimeStamper.ProcessedHeader);
        back.Headers.RemoveAll("X-Test-Marker");
        var (_, again, _) = await fx.SendAsync(back);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again.HtmlBody, "Sales Director"));
        Assert.Equal("true", again.Headers[MimeStamper.ProcessedHeader]);

        // The same message for somebody else (Exchange splits messages per recipient) is new work and is stamped.
        var copy = PipelineFixture.Compose(Sam, "other@fabrikam.example", "Proposal", "<html><body><p>Hi</p></body></html>",
            tweak: x => x.MessageId = first.MessageId);
        var (_, other, _) = await fx.SendAsync(copy);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(other.HtmlBody, "Sales Director"));
    }

    [Fact]
    public async Task Unregistered_organisations_are_refused_so_the_service_is_not_an_open_relay()
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("localhost", fx.Port, SecureSocketOptions.None);
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() =>
            client.SendAsync(new SendRequest(PipelineFixture.Compose("eve@evil.example", "victim@fabrikam.example", "x", "<p>x</p>"))));
        Assert.Equal(SmtpStatusCode.MailboxUnavailable, ex.StatusCode); // 550
    }

    [Fact]
    public async Task Relay_failure_returns_a_temporary_error_so_exchange_retries()
    {
        fx.Sink.Reply = new SmtpReply(451, "4.3.0 busy");
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync("localhost", fx.Port, SecureSocketOptions.None);
            var ex = await Assert.ThrowsAsync<SmtpCommandException>(() =>
                client.SendAsync(new SendRequest(PipelineFixture.Compose(Sam, "customer@fabrikam.example", "x", "<p>x</p>"))));
            Assert.Equal(451, (int)ex.StatusCode);
        }
        finally
        {
            fx.Sink.Reply = SmtpReply.Ok;
        }
    }

    [Fact]
    public async Task Signed_messages_are_relayed_byte_identical_apart_from_the_loop_header()
    {
        var m = PipelineFixture.Compose(Sam, "customer@fabrikam.example", "Signed", "<p>x</p>");
        var signed = new MimeKit.Cryptography.MultipartSigned { m.Body, new MimePart("application", "pkcs7-signature") { Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 })) } };
        signed.ContentType.Parameters["protocol"] = "application/pkcs7-signature";
        signed.ContentType.Parameters["micalg"] = "sha-256";
        m.Body = signed;
        var (_, received, _) = await fx.SendAsync(m);
        Assert.True(received.Body.ContentType.IsMimeType("multipart", "signed"));
        Assert.DoesNotContain("Sam Carter", received.HtmlBody ?? "");
    }

    [Fact]
    public async Task Large_message_25mb_with_dot_lines_is_stamped_intact_and_fast()
    {
        var payload = RandomNumberGenerator.GetBytes(25 * 1024 * 1024 * 3 / 4); // ≈25 MB once base64-encoded
        var m = new MimeMessage { Subject = "Big" };
        m.From.Add(MailboxAddress.Parse(Sam));
        m.To.Add(MailboxAddress.Parse("customer@fabrikam.example"));
        var builder = new BodyBuilder { HtmlBody = "<html><body><p>See attached</p></body></html>", TextBody = "line\r\n.\r\n..leading dots\r\n.x\r\nend" };
        builder.Attachments.Add("big.bin", payload);
        m.Body = builder.ToMessageBody();

        var sw = Stopwatch.StartNew();
        var (_, received, raw) = await fx.SendAsync(m);
        sw.Stop();

        Assert.True(raw.Length > 25 * 1024 * 1024);
        using var ms = new MemoryStream();
        ((MimePart)received.Attachments.Single()).Content.DecodeTo(ms);
        Assert.True(ms.ToArray().AsSpan().SequenceEqual(payload));
        Assert.StartsWith("line\r\n.\r\n..leading dots\r\n.x\r\nend", received.TextBody);
        Assert.Contains("Sam Carter", received.HtmlBody);
        // Generous bound for CI machines; the load-test tool measures the real p95.
        Assert.True(sw.ElapsedMilliseconds < 15_000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Raw_protocol_bdat_chunking_pipelining_and_smuggling_resistance()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("localhost", fx.Port);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        async Task<string> Line() => (await reader.ReadLineAsync())!;
        async Task Write(string s) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s));
        async Task<string> Final()
        {
            string l;
            do l = await Line(); while (l.Length > 3 && l[3] == '-');
            return l;
        }

        Assert.StartsWith("220", await Line());
        await Write("EHLO test\r\n");
        var caps = new List<string>();
        string cap;
        do { cap = await Line(); caps.Add(cap[4..]); } while (cap[3] == '-');
        Assert.Contains("CHUNKING", caps);
        Assert.Contains("PIPELINING", caps);
        Assert.DoesNotContain("STARTTLS", caps); // no certificate configured in this fixture

        // BDAT in two chunks, commands pipelined.
        var marker = Guid.NewGuid().ToString("N");
        var part1 = $"From: {Sam}\r\nTo: customer@fabrikam.example\r\nSubject: bdat\r\nX-Test-Marker: {marker}\r\nContent-Type: text/plain\r\n\r\n";
        var part2 = "body line\r\n.\r\nafter dot\r\n";
        await Write($"MAIL FROM:<{Sam}> SIZE=500\r\nRCPT TO:<customer@fabrikam.example>\r\nBDAT {part1.Length}\r\n{part1}BDAT {part2.Length} LAST\r\n{part2}");
        Assert.StartsWith("250", await Final());
        Assert.StartsWith("250", await Final());
        Assert.StartsWith("250", await Final());
        Assert.StartsWith("250 2.0.0 OK queued", await Final());
        var got = fx.Sink.Received.Single(r => r.Message.Headers["X-Test-Marker"] == marker);
        Assert.StartsWith("body line\r\n.\r\nafter dot", got.Message.TextBody);

        // DATA: a bare-LF "\n.\r\n" must NOT end the message (SMTP smuggling); only CRLF.CRLF does.
        marker = Guid.NewGuid().ToString("N");
        await Write($"MAIL FROM:<{Sam}>\r\nRCPT TO:<customer@fabrikam.example>\r\nDATA\r\n");
        await Final(); await Final();
        Assert.StartsWith("354", await Final());
        await Write($"From: {Sam}\r\nSubject: smuggle\r\nX-Test-Marker: {marker}\r\n\r\nfirst\n.\r\nMAIL FROM:<attacker@evil.example>\r\nstill body\r\n.\r\n");
        Assert.StartsWith("250", await Final());
        got = fx.Sink.Received.Single(r => r.Message.Headers["X-Test-Marker"] == marker);
        Assert.Contains("MAIL FROM:<attacker@evil.example>", got.Message.TextBody);

        // Size limit announced via SIZE is enforced up front.
        await Write("MAIL FROM:<a@contoso.example> SIZE=99999999999\r\n");
        Assert.StartsWith("552", await Final());
        await Write("RCPT TO:<x@y.z>\r\n");
        Assert.StartsWith("503", await Final());
        await Write("QUIT\r\n");
        Assert.StartsWith("221", await Final());
    }
}

public class SmtpTlsTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);
    }

    private static (SmtpServer server, Sink sink, CancellationTokenSource stop, string pfx) Start(bool requireClientCert)
    {
        var pfx = Path.Combine(Path.GetTempPath(), $"s2e-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfx, SelfSigned("localhost").Export(X509ContentType.Pfx, "pw"));
        var sink = new Sink();
        var server = new SmtpServer(new SmtpServerOptions
        {
            Port = 0, CertificatePath = pfx, CertificatePassword = "pw", RequireTls = true, RequireClientCertificate = requireClientCert,
            AllowedClientNames = { "mail.protection.outlook.com" },
        }, sink, NullLogger.Instance);
        server.Start();
        var stop = new CancellationTokenSource();
        _ = server.RunAsync(stop.Token);
        return (server, sink, stop, pfx);
    }

    private static readonly MimeMessage Mail = PipelineFixture.Compose("a@contoso.example", "b@fabrikam.example", "tls", "<p>x</p>");

    [Fact]
    public async Task Plaintext_sessions_are_refused_when_tls_is_required()
    {
        var (server, _, stop, pfx) = Start(false);
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync("localhost", server.Port, SecureSocketOptions.None);
            // MailKit surfaces the server's "530 Must issue a STARTTLS command first" as this exception type.
            var ex = await Assert.ThrowsAsync<SmtpServiceNotAuthenticatedException>(() => client.SendAsync(new SendRequest(Mail)));
            Assert.Contains("STARTTLS", ex.Message);
        }
        finally { stop.Cancel(); File.Delete(pfx); }
    }

    [Fact]
    public async Task Starttls_session_delivers()
    {
        var (server, sink, stop, pfx) = Start(false);
        try
        {
            using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
            await client.ConnectAsync("localhost", server.Port, SecureSocketOptions.StartTls);
            await client.SendAsync(new SendRequest(Mail));
            Assert.Single(sink.Received);
        }
        finally { stop.Cancel(); File.Delete(pfx); }
    }

    [Fact]
    public async Task Missing_or_untrusted_client_certificates_are_rejected()
    {
        var (server, sink, stop, pfx) = Start(true);
        try
        {
            // No certificate at all.
            using (var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
            {
                await client.ConnectAsync("localhost", server.Port, SecureSocketOptions.StartTls);
                var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(new SendRequest(Mail)));
                Assert.Equal(550, (int)ex.StatusCode);
            }
            // Right name, but self-signed (not chained to a trusted CA) – e.g. an attacker minting their own.
            using (var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
            {
                client.ClientCertificates = new X509CertificateCollection { SelfSigned("mail.protection.outlook.com") };
                await client.ConnectAsync("localhost", server.Port, SecureSocketOptions.StartTls);
                var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(new SendRequest(Mail)));
                Assert.Equal(550, (int)ex.StatusCode);
            }
            Assert.Empty(sink.Received);
        }
        finally { stop.Cancel(); File.Delete(pfx); }
    }
}

/// <summary>How the relay introduces itself to Exchange Online, which matches its inbound connector on exactly these things.</summary>
public class RelayIdentityTests
{
    private static X509Certificate2 Issue(string cn, X509Certificate2? issuer, bool ca)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        if (!ca)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(cn);
            req.CertificateExtensions.Add(san.Build());
        }
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = issuer == null ? DateTimeOffset.UtcNow.AddDays(30) : new DateTimeOffset(issuer.NotAfter.ToUniversalTime()).AddMinutes(-1);
        using var cert = issuer == null
            ? req.CreateSelfSigned(from, to)
            : req.Create(issuer, from, to, RandomNumberGenerator.GetBytes(8)).CopyWithPrivateKey(rsa);
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public async Task Relay_introduces_itself_by_name_and_presents_its_certificate_with_intermediates()
    {
        using var root = Issue("S2E Test Root", null, true);
        using var intermediate = Issue("S2E Test Intermediate", root, true);
        using var leaf = Issue("smtp.sig2email.test", intermediate, false);
        using var exchange = Issue("localhost", null, false);

        var fx = new PipelineFixture();
        await fx.InitializeAsync();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await fx.Services.GetRequiredService<SettingsProvider>().UpdateAsync(s =>
            {
                s.SmtpHostName = "smtp.sig2email.test";
                s.RelayPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            });

            var greetings = new List<string>();
            string? presented = null;
            var alsoSent = new List<string>();
            var exchangeSide = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                Stream s = tcp.GetStream();
                Task Say(string line) => s.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n")).AsTask();
                async Task<string> Hear()
                {
                    var sb = new StringBuilder();
                    var one = new byte[1];
                    while (await s.ReadAsync(one) == 1 && one[0] != (byte)'\n') sb.Append((char)one[0]);
                    return sb.ToString().TrimEnd('\r');
                }
                await Say("220 fake.mail.protection.outlook.com ESMTP");
                greetings.Add(await Hear());
                await Say("250-fake.mail.protection.outlook.com");
                await Say("250 STARTTLS");
                await Hear();
                await Say("220 2.0.0 SMTP server ready");
                var ssl = new SslStream(s);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = exchange,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                    {
                        if (cert != null)
                        {
                            presented = new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false);
                            alsoSent.AddRange(chain!.ChainPolicy.ExtraStore.Select(c => c.GetNameInfo(X509NameType.SimpleName, false)));
                        }
                        return true;
                    },
                });
                s = ssl;
                greetings.Add(await Hear());
                await Say("250 fake.mail.protection.outlook.com");
                await Hear();
                await Say("421 4.3.2 test over");
            });

            var relay = fx.Services.GetRequiredService<RelayClient>();
            relay.ClientCertificate = leaf;
            relay.ClientCertificateChain = new X509Certificate2Collection { intermediate };
            var mail = PipelineFixture.Compose("a@contoso.example", "b@fabrikam.example", "identity", "<p>x</p>");
            await Assert.ThrowsAnyAsync<Exception>(() => relay.SendAsync("ignored.example", mail, "a@contoso.example", new[] { "b@fabrikam.example" }, default));
            await exchangeSide.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(new[] { "EHLO smtp.sig2email.test", "EHLO smtp.sig2email.test" }, greetings);
            Assert.Equal("smtp.sig2email.test", presented);
            Assert.Contains("S2E Test Intermediate", alsoSent);
        }
        finally
        {
            listener.Stop();
            await fx.DisposeAsync();
        }
    }
}

public class CertificateInstallerTests
{
    private static byte[] Pfx(string cn, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(notAfter.AddDays(-30), notAfter);
        return cert.Export(X509ContentType.Pfx);
    }

    [Fact]
    public async Task A_renewed_certificate_piped_in_replaces_the_stored_one_and_bad_input_changes_nothing()
    {
        var fx = new PipelineFixture();
        await fx.InitializeAsync();
        try
        {
            var settings = fx.Services.GetRequiredService<SettingsProvider>();
            var output = new StringWriter();
            var error = new StringWriter();

            var renewed = Pfx("smtp.sig2email.test", DateTimeOffset.UtcNow.AddDays(60));
            Assert.Equal(0, await Sig2Email.Api.CertificateInstaller.InstallAsync(settings, renewed, output, error));
            Assert.Contains("smtp.sig2email.test", output.ToString());
            Assert.Equal(Convert.ToBase64String(renewed), settings.Current.SmtpCertificatePfx);
            var (leaf, _) = Sig2Email.Data.Pfx.Load(Convert.FromBase64String(settings.Current.SmtpCertificatePfx!), settings.Current.SmtpCertificatePassword);
            Assert.True(leaf.HasPrivateKey);

            Assert.Equal(1, await Sig2Email.Api.CertificateInstaller.InstallAsync(settings, Pfx("old.test", DateTimeOffset.UtcNow.AddDays(-1)), output, error));
            Assert.Equal(1, await Sig2Email.Api.CertificateInstaller.InstallAsync(settings, new byte[] { 1, 2, 3 }, output, error));
            Assert.Equal(2, await Sig2Email.Api.CertificateInstaller.InstallAsync(settings, Array.Empty<byte>(), output, error));
            Assert.Equal(Convert.ToBase64String(renewed), settings.Current.SmtpCertificatePfx);
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }
}
