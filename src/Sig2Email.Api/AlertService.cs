using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Sig2Email.Core.Models;
using Sig2Email.Data;

namespace Sig2Email.Api;

/// <summary>Delivers negative-rating alerts (webhook and/or email) off the request path.</summary>
public sealed class AlertService(SettingsProvider settings, ILogger<AlertService> log) : BackgroundService
{
    private sealed record Alert(Survey Survey, SurveyResponse Response);

    private readonly Channel<Alert> _queue = Channel.CreateBounded<Alert>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    // Webhook targets are tenant-supplied, so connections to private/loopback/link-local addresses are refused (SSRF).
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var address = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("Webhook host resolves to a non-public address.");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { Timeout = TimeSpan.FromSeconds(10) };

    public void Queue(Survey survey, SurveyResponse response) => _queue.Writer.TryWrite(new Alert(survey, response));

    internal static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 0 || b[0] == 127 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var alert in _queue.Reader.ReadAllAsync(stop))
        {
            var (s, r) = (alert.Survey, alert.Response);
            if (!string.IsNullOrEmpty(s.WebhookUrl))
            {
                try
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        @event = "csat.negative", survey = s.Name, surveyId = s.Id, score = r.Score, maxScore = r.MaxScore, comment = r.Comment,
                        sender = r.SenderEmail, senderName = r.SenderName, recipients = r.Recipients, subject = r.Subject, at = r.UpdatedAt,
                    }, JsonDefaults.Options);
                    using var response = await _http.PostAsync(s.WebhookUrl, new StringContent(body, Encoding.UTF8, "application/json"), stop);
                    if (!response.IsSuccessStatusCode) log.LogWarning("CSAT webhook for survey {Survey} returned {Status}", s.Id, (int)response.StatusCode);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("CSAT webhook for survey {Survey} failed: {Error}", s.Id, e.Message);
                }
            }

            var mail = settings.Current;
            if (s.AlertEmails.Count == 0) continue;
            if (string.IsNullOrEmpty(mail.AlertMailHost) || string.IsNullOrEmpty(mail.AlertMailFrom))
            {
                log.LogWarning("Negative rating on survey {Survey} but no alert mail server is set under Settings → System – email alert skipped", s.Id);
                continue;
            }
            try
            {
                await SendMailAsync(mail, s.AlertEmails, $"Negative rating ({r.Score}/{r.MaxScore}) for {r.SenderName} – {s.Name}",
                    $"Survey: {s.Name}\nRating: {r.Score} of {r.MaxScore}\nSender: {r.SenderName} <{r.SenderEmail}>\n" +
                    $"Recipient(s): {r.Recipients}\nSubject: {r.Subject}\nComment: {r.Comment ?? "(none yet)"}\n", stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("CSAT alert email for survey {Survey} failed: {Error}", s.Id, e.Message);
            }
        }
    }

    /// <summary>Sends a plain-text message through the configured alert mail server.</summary>
    public static async Task SendMailAsync(SystemSettings mail, IEnumerable<string> to, string subject, string text, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(mail.AlertMailFrom));
        foreach (var address in to) msg.To.Add(MailboxAddress.Parse(address));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = text };
        using var smtp = new SmtpClient { Timeout = 20_000 };
        await smtp.ConnectAsync(mail.AlertMailHost, mail.AlertMailPort, SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(mail.AlertMailUser)) await smtp.AuthenticateAsync(mail.AlertMailUser, mail.AlertMailPassword ?? "", ct);
        await smtp.SendAsync(new MailKit.SendRequest(msg), ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
