// Load test for the SMTP pipeline. Starts a sink that plays the role of Exchange Online (the processor relays
// stamped mail to it), then pushes messages through the processor and reports throughput and latency.
//
//   dotnet run --project tools/Sig2Email.LoadTest -- --count 5000 --concurrency 32 --size-kb 50
//
// Prerequisite: the API (seeds the demo tenant) and the mail processor are running with their Development settings,
// i.e. the processor listens on 2525 and relays to localhost:2526.
using System.Collections.Concurrent;
using System.Diagnostics;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Sig2Email.MailProcessor.Smtp;

int Arg(string name, int fallback)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}
string ArgS(string name, string fallback)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var count = Arg("count", 2000);
var concurrency = Arg("concurrency", 16);
var sizeKb = Arg("size-kb", 20);
var port = Arg("port", 2525);
var sinkPort = Arg("sink-port", 2526);
var host = ArgS("host", "localhost");
var from = ArgS("from", "sam.carter@contoso.example");

var sink = new CountingSink();
var server = new SmtpServer(new SmtpServerOptions { Port = sinkPort, RequireTls = false, RequireClientCertificate = false, MaxMessageBytes = 64L * 1024 * 1024 }, sink, NullLogger.Instance);
server.Start();
using var stop = new CancellationTokenSource();
_ = server.RunAsync(stop.Token);

var attachment = new byte[Math.Max(0, sizeKb * 1024 * 3 / 4)];
Random.Shared.NextBytes(attachment);

MimeMessage Compose(int n)
{
    var m = new MimeMessage { Subject = $"Load test {n}" };
    m.From.Add(MailboxAddress.Parse(from));
    m.To.Add(MailboxAddress.Parse($"customer{n % 500}@fabrikam.example"));
    var body = new BodyBuilder { HtmlBody = $"<html><body><p>Hello, this is message {n}.</p></body></html>", TextBody = $"Hello, this is message {n}." };
    if (attachment.Length > 0) body.Attachments.Add("payload.bin", attachment);
    m.Body = body.ToMessageBody();
    return m;
}

Console.WriteLine($"Sending {count} messages of ~{sizeKb} KB to {host}:{port} with {concurrency} connections (sink on :{sinkPort})…");
var latencies = new ConcurrentBag<double>();
var failures = 0;
var next = -1;
var total = Stopwatch.StartNew();

await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
{
    using var client = new SmtpClient();
    await client.ConnectAsync(host, port, SecureSocketOptions.None);
    while (true)
    {
        var n = Interlocked.Increment(ref next);
        if (n >= count) break;
        var message = Compose(n);
        var sw = Stopwatch.StartNew();
        try
        {
            // The processor replies 250 only after it has stamped the message and handed it to the sink,
            // so the SMTP transaction time is the full transit overhead.
            await client.SendAsync(new SendRequest(message));
            latencies.Add(sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref failures) <= 3) Console.WriteLine($"  send failed: {e.Message}");
            if (!client.IsConnected) await client.ConnectAsync(host, port, SecureSocketOptions.None);
        }
    }
    await client.DisconnectAsync(true);
}));

total.Stop();
stop.Cancel();
var sorted = latencies.OrderBy(x => x).ToArray();
double P(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

Console.WriteLine();
Console.WriteLine($"Sent OK:            {sorted.Length} / {count}   (failures: {failures})");
Console.WriteLine($"Arrived at sink:    {sink.Received}   stamped: {sink.Stamped}   with loop header: {sink.WithLoopHeader}");
Console.WriteLine($"Elapsed:            {total.Elapsed.TotalSeconds:F1} s");
Console.WriteLine($"Throughput:         {sorted.Length / total.Elapsed.TotalSeconds:F0} msg/s  ({sorted.Length / total.Elapsed.TotalMinutes:F0} msg/min)");
Console.WriteLine($"Latency (ms):       p50 {P(0.50):F1}   p95 {P(0.95):F1}   p99 {P(0.99):F1}   max {(sorted.Length == 0 ? 0 : sorted[^1]):F1}");
return failures == 0 && sink.Received == count ? 0 : 1;

internal sealed class CountingSink : IMessageHandler
{
    private int _received, _stamped, _loop;
    public int Received => _received;
    public int Stamped => _stamped;
    public int WithLoopHeader => _loop;

    public Task<SmtpReply> HandleAsync(SmtpEnvelope envelope, MemoryStream data, SmtpSessionInfo session, CancellationToken ct)
    {
        Interlocked.Increment(ref _received);
        // Cheap inspection of the raw bytes; headers and the (quoted-printable) HTML part come first.
        var head = System.Text.Encoding.ASCII.GetString(data.GetBuffer(), 0, (int)Math.Min(data.Length, 16 * 1024));
        if (head.Contains("X-Sig2Email-Processed", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _loop);
        if (head.Contains("s2e-signature", StringComparison.Ordinal)) Interlocked.Increment(ref _stamped);
        return Task.FromResult(SmtpReply.Ok);
    }
}
