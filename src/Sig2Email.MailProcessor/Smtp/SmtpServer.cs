using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Sig2Email.MailProcessor.Smtp;

public class SmtpServerOptions
{
    public int Port { get; set; } = 25;
    public string HostName { get; set; } = "sig2email.local";
    /// <summary>PFX file, or a PEM certificate when <see cref="CertificateKeyPath"/> is set.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public string? CertificateKeyPath { get; set; }
    /// <summary>The certificate in use. Loaded from <see cref="CertificatePath"/> at start, or assigned (and replaced) at run time.</summary>
    public X509Certificate2? Certificate { get; set; }
    /// <summary>Intermediate certificates to present with <see cref="Certificate"/>.</summary>
    public X509Certificate2Collection? CertificateChain { get; set; }
    /// <summary>Refuse MAIL FROM until the session is encrypted.</summary>
    public bool RequireTls { get; set; } = true;
    /// <summary>Require a trusted client certificate whose subject/SAN matches <see cref="AllowedClientNames"/>.</summary>
    public bool RequireClientCertificate { get; set; } = true;
    public List<string> AllowedClientNames { get; set; } = new() { "mail.protection.outlook.com" };
    public long MaxMessageBytes { get; set; } = 40L * 1024 * 1024;
    public int MaxConnections { get; set; } = 1000;
    public int MaxRecipients { get; set; } = 1000;
    public int CommandTimeoutSeconds { get; set; } = 300;
}

public sealed record SmtpReply(int Code, string Text)
{
    public static readonly SmtpReply Ok = new(250, "2.0.0 OK");
}

public sealed class SmtpEnvelope
{
    public string MailFrom { get; set; } = "";
    public List<string> Recipients { get; } = new();
}

public sealed record SmtpSessionInfo(EndPoint? Remote, bool Secure, string? ClientCertificateName);

public interface IMessageHandler
{
    /// <param name="data">The complete message, held in memory only.</param>
    Task<SmtpReply> HandleAsync(SmtpEnvelope envelope, MemoryStream data, SmtpSessionInfo session, CancellationToken ct);
}

/// <summary>
/// Minimal, dependency-free ESMTP receiver (STARTTLS with client-certificate verification, PIPELINING, CHUNKING,
/// 8BITMIME, SMTPUTF8, SIZE). Message content only ever lives in process memory.
/// </summary>
public sealed class SmtpServer(SmtpServerOptions options, IMessageHandler handler, ILogger logger)
{
    private readonly SemaphoreSlim _slots = new(options.MaxConnections);
    private TcpListener? _listener;

    private SmtpServerOptions Options => options;
    private IMessageHandler Handler => handler;
    private ILogger Logger => logger;

    public int Port => ((IPEndPoint)_listener!.LocalEndpoint).Port;
    public X509Certificate2? Certificate => options.Certificate;

    private static X509Certificate2? LoadCertificate(SmtpServerOptions o)
    {
        if (string.IsNullOrEmpty(o.CertificatePath)) return null;
        if (string.IsNullOrEmpty(o.CertificateKeyPath)) return new X509Certificate2(o.CertificatePath, o.CertificatePassword);
        using var pem = X509Certificate2.CreateFromPemFile(o.CertificatePath, o.CertificateKeyPath);
        return new X509Certificate2(pem.Export(X509ContentType.Pfx)); // SChannel cannot use ephemeral PEM keys directly
    }

    public void Start()
    {
        options.Certificate ??= LoadCertificate(options);
        if (options.RequireTls && options.Certificate == null)
            logger.LogWarning("TLS is required but no certificate is installed yet – mail is refused until one is uploaded (Settings → System)");
        _listener = new TcpListener(IPAddress.IPv6Any, options.Port);
        _listener.Server.DualMode = true;
        _listener.Start(512);
        logger.LogInformation("SMTP listening on port {Port} (TLS {Tls}, client certificate {ClientCert})", Port,
            options.Certificate == null ? "unavailable" : options.RequireTls ? "required" : "optional", options.RequireClientCertificate ? "required" : "not required");
    }

    public async Task RunAsync(CancellationToken stop)
    {
        if (_listener == null) Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => ServeAsync(client, stop), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener!.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        client.NoDelay = true;
        if (!await _slots.WaitAsync(0, stop))
        {
            try { await client.GetStream().WriteAsync("421 4.3.2 Too many connections, try again later\r\n"u8.ToArray(), stop); } catch { /* ignore */ }
            return;
        }
        try
        {
            await new Session(this, client).RunAsync(stop);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or AuthenticationException or ObjectDisposedException)
        {
            logger.LogDebug("SMTP session ended: {Reason}", e.Message);
        }
        catch (Exception e)
        {
            logger.LogError(e, "SMTP session failed");
        }
        finally
        {
            _slots.Release();
        }
    }

    private sealed class Session(SmtpServer server, TcpClient client)
    {
        private readonly SmtpServerOptions _o = server.Options;
        private Stream _stream = client.GetStream();
        private readonly byte[] _buf = new byte[32 * 1024];
        private int _pos, _len;
        private bool _secure;
        private string? _clientCertName;
        private bool _clientCertTrusted;
        private SmtpEnvelope? _envelope;
        private MemoryStream? _chunks;
        private bool _chunkOverflow;

        private Task SendAsync(string line, CancellationToken ct) => _stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct).AsTask();
        private Task SendAsync(SmtpReply r, CancellationToken ct) => SendAsync($"{r.Code} {r.Text}", ct);

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_pos > 0)
            {
                Buffer.BlockCopy(_buf, _pos, _buf, 0, _len - _pos);
                _len -= _pos;
                _pos = 0;
            }
            if (_len == _buf.Length) return false;
            var n = await _stream.ReadAsync(_buf.AsMemory(_len), ct);
            if (n == 0) throw new IOException("Connection closed by peer.");
            _len += n;
            return true;
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                var i = _buf.AsSpan(_pos, _len - _pos).IndexOf((byte)'\n');
                if (i >= 0)
                {
                    var line = Encoding.UTF8.GetString(_buf, _pos, i).TrimEnd('\r');
                    _pos += i + 1;
                    return line;
                }
                if (_len - _pos >= 4096) return null; // command line too long
                await FillAsync(ct);
            }
        }

        /// <summary>Reads a dot-terminated DATA payload. Only CRLF.CRLF ends the data (no bare-LF smuggling).</summary>
        private async Task<bool> ReadDataAsync(MemoryStream target, CancellationToken ct)
        {
            _lineStart = true;
            _last = (byte)'\n';
            _overflow = false;
            while (true)
            {
                if (_pos == _len) await FillAsync(ct);
                if (_lineStart && _buf[_pos] == (byte)'.')
                {
                    while (_len - _pos < 3) await FillAsync(ct);
                    if (_buf[_pos + 1] == (byte)'\r' && _buf[_pos + 2] == (byte)'\n')
                    {
                        _pos += 3;
                        return !_overflow;
                    }
                    _pos++; // dot-unstuffing
                    _lineStart = false;
                    continue;
                }
                ConsumeDataSegment(target);
            }
        }

        private bool _lineStart, _overflow;
        private byte _last;

        /// <summary>Copies buffered bytes up to and including the next LF (or everything buffered) into the message.</summary>
        private void ConsumeDataSegment(MemoryStream target)
        {
            var span = _buf.AsSpan(_pos, _len - _pos);
            var i = span.IndexOf((byte)'\n');
            var take = i >= 0 ? i + 1 : span.Length;
            if (!_overflow)
            {
                if (target.Length + take > _o.MaxMessageBytes) _overflow = true;
                else target.Write(span[..take]);
            }
            var prev = take >= 2 ? span[take - 2] : _last;
            _lineStart = i >= 0 && prev == (byte)'\r';
            _last = span[take - 1];
            _pos += take;
        }

        private async Task ReadExactAsync(MemoryStream? target, long count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (_pos == _len) await FillAsync(ct);
                var take = (int)Math.Min(count, _len - _pos);
                target?.Write(_buf, _pos, take);
                _pos += take;
                count -= take;
            }
        }

        public async Task RunAsync(CancellationToken stop)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            void Arm() => timeout.CancelAfter(TimeSpan.FromSeconds(_o.CommandTimeoutSeconds));
            var ct = timeout.Token;
            Arm();
            await SendAsync($"220 {_o.HostName} ESMTP Sig2Email", ct);
            var errors = 0;

            while (true)
            {
                Arm();
                var line = await ReadLineAsync(ct);
                if (line == null) { await SendAsync("500 5.5.6 Line too long", ct); return; }
                var space = line.IndexOf(' ');
                var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                var arg = space < 0 ? "" : line[(space + 1)..].Trim();

                switch (verb)
                {
                    case "EHLO":
                    {
                        Reset();
                        var caps = new List<string>
                        {
                            _o.HostName, $"SIZE {_o.MaxMessageBytes}", "8BITMIME", "PIPELINING", "CHUNKING", "SMTPUTF8", "ENHANCEDSTATUSCODES",
                        };
                        if (_o.Certificate != null && !_secure) caps.Add("STARTTLS");
                        var sb = new StringBuilder();
                        for (var i = 0; i < caps.Count; i++) sb.Append("250").Append(i == caps.Count - 1 ? ' ' : '-').Append(caps[i]).Append("\r\n");
                        await _stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
                        break;
                    }
                    case "HELO":
                        Reset();
                        await SendAsync($"250 {_o.HostName}", ct);
                        break;
                    case "STARTTLS":
                        var certificate = _o.Certificate; // may be replaced while the service runs
                        if (certificate == null || _secure) { await SendAsync("503 5.5.1 STARTTLS not available", ct); break; }
                        await SendAsync("220 2.0.0 Ready to start TLS", ct);
                        _pos = _len = 0; // anything pipelined before the handshake is discarded (command injection)
                        var ssl = new SslStream(_stream, false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = SslStreamCertificateContext.Create(certificate, _o.CertificateChain, offline: true),
                            ClientCertificateRequired = _o.RequireClientCertificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            // The handshake always completes; the certificate is judged at MAIL FROM so the peer gets a clear SMTP error.
                            RemoteCertificateValidationCallback = (_, cert, _, errs) =>
                            {
                                if (cert != null)
                                {
                                    using var c2 = new X509Certificate2(cert);
                                    _clientCertName = MatchName(c2);
                                    _clientCertTrusted = errs == SslPolicyErrors.None;
                                }
                                return true;
                            },
                        }, ct);
                        _stream = ssl;
                        _secure = true;
                        Reset();
                        break;
                    case "MAIL":
                    {
                        if (_o.RequireTls && !_secure) { await SendAsync("530 5.7.0 Must issue a STARTTLS command first", ct); break; }
                        if (_o.RequireClientCertificate && (_clientCertName == null || !_clientCertTrusted))
                        {
                            await SendAsync("550 5.7.1 Client certificate not accepted", ct);
                            break;
                        }
                        if (_envelope != null) { await SendAsync("503 5.5.1 Nested MAIL command", ct); break; }
                        if (!arg.StartsWith("FROM:", StringComparison.OrdinalIgnoreCase) || ParsePath(arg[5..]) is not { } path)
                        {
                            await SendAsync("501 5.5.4 Syntax: MAIL FROM:<address>", ct);
                            break;
                        }
                        var size = path.Params.FirstOrDefault(p => p.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase));
                        if (size != null && long.TryParse(size[5..], out var declared) && declared > _o.MaxMessageBytes)
                        {
                            await SendAsync("552 5.3.4 Message size exceeds fixed maximum message size", ct);
                            break;
                        }
                        _envelope = new SmtpEnvelope { MailFrom = path.Address };
                        await SendAsync("250 2.1.0 Sender OK", ct);
                        break;
                    }
                    case "RCPT":
                        if (_envelope == null) { await SendAsync("503 5.5.1 Need MAIL command first", ct); break; }
                        if (!arg.StartsWith("TO:", StringComparison.OrdinalIgnoreCase) || ParsePath(arg[3..]) is not { Address.Length: > 0 } rcpt)
                        {
                            await SendAsync("501 5.5.4 Syntax: RCPT TO:<address>", ct);
                            break;
                        }
                        if (_envelope.Recipients.Count >= _o.MaxRecipients) { await SendAsync("452 4.5.3 Too many recipients", ct); break; }
                        _envelope.Recipients.Add(rcpt.Address);
                        await SendAsync("250 2.1.5 Recipient OK", ct);
                        break;
                    case "DATA":
                    {
                        if (_envelope == null || _envelope.Recipients.Count == 0) { await SendAsync("503 5.5.1 Need MAIL and RCPT first", ct); break; }
                        await SendAsync("354 Start mail input; end with <CRLF>.<CRLF>", ct);
                        var data = new MemoryStream();
                        var ok = await ReadDataAsync(data, ct);
                        if (!ok) await SendAsync("552 5.3.4 Message size exceeds fixed maximum message size", ct);
                        else await DeliverAsync(data, ct);
                        Reset();
                        break;
                    }
                    case "BDAT":
                    {
                        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0 || !long.TryParse(parts[0], out var count) || count < 0) { await SendAsync("501 5.5.4 Syntax: BDAT <size> [LAST]", ct); return; }
                        var lastChunk = parts.Length > 1 && parts[1].Equals("LAST", StringComparison.OrdinalIgnoreCase);
                        var valid = _envelope != null && _envelope.Recipients.Count > 0;
                        _chunks ??= new MemoryStream();
                        if (!valid || _chunks.Length + count > _o.MaxMessageBytes) _chunkOverflow = valid;
                        // The chunk must always be consumed to stay in sync with the client.
                        await ReadExactAsync(valid && !_chunkOverflow ? _chunks : null, count, ct);
                        if (!valid) { await SendAsync("503 5.5.1 Need MAIL and RCPT first", ct); Reset(); break; }
                        if (_chunkOverflow) { await SendAsync("552 5.3.4 Message size exceeds fixed maximum message size", ct); if (lastChunk) Reset(); break; }
                        if (!lastChunk) { await SendAsync($"250 2.0.0 {count} octets received", ct); break; }
                        await DeliverAsync(_chunks, ct);
                        Reset();
                        break;
                    }
                    case "RSET":
                        Reset();
                        await SendAsync("250 2.0.0 OK", ct);
                        break;
                    case "NOOP":
                        await SendAsync("250 2.0.0 OK", ct);
                        break;
                    case "VRFY":
                        await SendAsync("252 2.5.2 Cannot VRFY user", ct);
                        break;
                    case "QUIT":
                        await SendAsync("221 2.0.0 Bye", ct);
                        return;
                    default:
                        if (++errors > 10) { await SendAsync("421 4.7.0 Too many errors", ct); return; }
                        await SendAsync("500 5.5.2 Command not recognized", ct);
                        break;
                }
            }
        }

        private void Reset()
        {
            _envelope = null;
            _chunks = null;
            _chunkOverflow = false;
        }

        private async Task DeliverAsync(MemoryStream data, CancellationToken ct)
        {
            SmtpReply reply;
            try
            {
                data.Position = 0;
                // Processing gets its own budget: the handler relays onwards and must not be cut off by the idle timer.
                using var work = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                reply = await server.Handler.HandleAsync(_envelope!, data, new SmtpSessionInfo(client.Client.RemoteEndPoint, _secure, _clientCertName), work.Token);
            }
            catch (Exception e)
            {
                server.Logger.LogError("Message handling failed: {Type}: {Message}", e.GetType().Name, e.Message);
                reply = new SmtpReply(451, "4.3.0 Temporary processing failure");
            }
            await SendAsync(reply, ct);
        }

        private string? MatchName(X509Certificate2 cert)
        {
            var names = new List<string> { cert.GetNameInfo(X509NameType.DnsName, false) };
            var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            if (san != null) names.AddRange(san.EnumerateDnsNames());
            if (_o.AllowedClientNames.Count == 0) return names.FirstOrDefault(n => !string.IsNullOrEmpty(n));
            return names.FirstOrDefault(n => !string.IsNullOrEmpty(n) && _o.AllowedClientNames.Any(a =>
                n.Equals(a, StringComparison.OrdinalIgnoreCase) || n.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)));
        }

        private static (string Address, string[] Params)? ParsePath(string s)
        {
            s = s.Trim();
            if (!s.StartsWith('<')) return null;
            var end = s.IndexOf('>');
            if (end < 0) return null;
            var address = s[1..end];
            var colon = address.LastIndexOf(':'); // strip obsolete source routes "@a,@b:user@c"
            if (address.StartsWith('@') && colon > 0) address = address[(colon + 1)..];
            if (address.Any(c => c is '\r' or '\n' or ' ' or '<')) return null;
            return (address, s[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
