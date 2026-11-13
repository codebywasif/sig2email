using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;

namespace Sig2Email.Core.Mail;

public sealed record InlineImage(string ContentId, byte[] Data, string ContentType, string FileName);

public sealed record SignaturePart(Guid RuleId, string Html, string Text, SignaturePlacement Placement, IReadOnlyList<InlineImage> Images);

/// <summary>Inserts rendered signatures into a MIME message, entirely in memory.</summary>
public static partial class MimeStamper
{
    /// <summary>Loop-prevention marker; the Exchange transport rule has an exception on this header.</summary>
    public const string ProcessedHeader = "X-Sig2Email-Processed";
    /// <summary>Set by the Outlook add-in: comma-separated ids of the rules it already applied (combo mode).</summary>
    public const string ClientHeader = "X-Sig2Email-Client";

    [GeneratedRegex(@"^\s*(re|aw|sv|antw|odp)\s*(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"^\s*(fw|fwd|wg|tr|vs|doorst)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardPrefix();

    // Start of the quoted original in replies/forwards produced by the common mail clients.
    [GeneratedRegex(
        @"<div[^>]*\bid\s*=\s*[""']?(?:appendonsend|divRplyFwdMsg|mail-editor-reference-message-container)\b" +
        @"|<hr[^>]*\btabindex\s*=\s*[""']?-1" +
        @"|<div[^>]*\bstyle\s*=\s*[""']border:none;\s*border-top:solid #(?:E1E1E1|B5C4DF)" +
        @"|<div[^>]*\bclass\s*=\s*[""']?(?:gmail_quote|ms-outlook-mobile-reference-message|moz-cite-prefix|OutlookMessageHeader)" +
        @"|<blockquote[^>]*\btype\s*=\s*[""']?cite",
        RegexOptions.IgnoreCase)]
    private static partial Regex HtmlQuoteStart();

    [GeneratedRegex(
        @"^[ \t]*-{2,}\s*(Original Message|Forwarded message|Ursprüngliche Nachricht)\s*-{2,}[ \t]*\r?$" +
        @"|^_{10,}[ \t]*\r?\n[ \t]*(From|Von|De):" +
        @"|^(From|Von|De):[ \t].+\r?\n(Sent|Date|Gesendet|Envoyé):" +
        @"|^On .{10,200} wrote:[ \t]*\r?$" +
        @"|^>[ \t]?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TextQuoteStart();

    public static MessageKind DetectKind(MimeMessage m)
    {
        var subject = m.Subject ?? "";
        if (ForwardPrefix().IsMatch(subject)) return MessageKind.Forward;
        if (ReplyPrefix().IsMatch(subject)) return MessageKind.Reply;
        return !string.IsNullOrEmpty(m.InReplyTo) || m.References.Count > 0 ? MessageKind.Reply : MessageKind.New;
    }

    /// <summary>False for messages that must pass through untouched (signed, encrypted, machine-generated).</summary>
    public static bool IsStampable(MimeMessage m)
    {
        var ct = m.Body?.ContentType;
        if (ct == null) return false;
        // Judged by content type rather than MimeKit's entity class, which depends on how the message was parsed.
        if (ct.IsMimeType("multipart", "signed") || ct.IsMimeType("multipart", "encrypted") || ct.IsMimeType("multipart", "report")) return false;
        if (ct.IsMimeType("application", "pkcs7-mime") || ct.IsMimeType("application", "x-pkcs7-mime")) return false;
        if (m.BodyParts.Any(p => p.ContentType.IsMimeType("application", "x-microsoft-rpmsg-message"))) return false;
        var auto = m.Headers["Auto-Submitted"];
        if (!string.IsNullOrEmpty(auto) && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static (TextPart? html, TextPart? plain) FindBodies(MimeMessage m)
    {
        TextPart? html = null, plain = null;
        foreach (var part in m.BodyParts.OfType<TextPart>())
        {
            if (part.IsAttachment) continue;
            if (html == null && part.IsHtml) html = part;
            else if (plain == null && part.IsPlain) plain = part;
            if (html != null && plain != null) break;
        }
        return (html, plain);
    }

    /// <summary>Text the sender just wrote (quoted history excluded), for keyword conditions.</summary>
    public static string GetNewBodyText(MimeMessage m)
    {
        var (html, plain) = FindBodies(m);
        if (plain != null)
        {
            var text = plain.Text;
            var q = TextQuoteStart().Match(text);
            return q.Success ? text[..q.Index] : text;
        }
        if (html != null)
        {
            var text = html.Text;
            var q = HtmlQuoteStart().Match(text);
            return HtmlConverter.ToPlainText(q.Success ? text[..q.Index] : text);
        }
        return "";
    }

    /// <summary>Returns true when the message was modified.</summary>
    public static bool Apply(MimeMessage m, IReadOnlyList<SignaturePart> parts)
    {
        if (parts.Count == 0) return false;
        var (html, plain) = FindBodies(m);
        if (html == null && plain == null) return false;

        if (html != null)
        {
            var above = new StringBuilder();
            var bottom = new StringBuilder();
            foreach (var p in parts)
                (p.Placement == SignaturePlacement.AboveQuotedText ? above : bottom)
                    .Append("<div class=\"s2e-signature\"><br>").Append(p.Html).Append("</div>");
            SetText(html, InsertHtml(html.Text, above.ToString(), bottom.ToString()));

            var images = parts.SelectMany(p => p.Images).DistinctBy(i => i.ContentId).ToList();
            if (images.Count > 0) AttachInline(m, html, images);
        }

        if (plain != null)
        {
            var above = new StringBuilder();
            var bottom = new StringBuilder();
            foreach (var p in parts.Where(p => p.Text.Length > 0))
                (p.Placement == SignaturePlacement.AboveQuotedText ? above : bottom).Append("\r\n").Append(p.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
            SetText(plain, InsertText(plain.Text, above.ToString(), bottom.ToString()));
        }
        return true;
    }

    private static void SetText(TextPart part, string text)
    {
        part.SetText(Encoding.UTF8, text);
        if (part.ContentTransferEncoding != ContentEncoding.Base64)
            part.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
    }

    internal static string InsertHtml(string body, string above, string bottom)
    {
        if (above.Length > 0)
        {
            var bodyTag = body.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            var q = HtmlQuoteStart().Match(body, Math.Max(bodyTag, 0));
            if (q.Success) body = body.Insert(q.Index, above);
            else bottom = above + bottom;
        }
        if (bottom.Length > 0)
        {
            var end = body.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = body.LastIndexOf("</html", StringComparison.OrdinalIgnoreCase);
            body = end < 0 ? body + bottom : body.Insert(end, bottom);
        }
        return body;
    }

    internal static string InsertText(string body, string above, string bottom)
    {
        if (above.Length > 0)
        {
            var q = TextQuoteStart().Match(body);
            if (q.Success) body = body.Insert(q.Index, above.TrimStart('\r', '\n') + "\r\n");
            else bottom = above + bottom;
        }
        if (bottom.Length > 0) body = body.TrimEnd('\r', '\n') + "\r\n" + bottom;
        return body;
    }

    private static void AttachInline(MimeMessage m, TextPart html, List<InlineImage> images)
    {
        var parent = m.Body is Multipart root ? FindParent(root, html) : null;
        MultipartRelated related;
        if (parent is MultipartRelated existing) related = existing;
        else
        {
            related = new MultipartRelated();
            if (parent == null) m.Body = related;
            else parent[parent.IndexOf(html)] = related;
            related.Root = html;
        }

        foreach (var img in images)
        {
            var type = ContentType.TryParse(img.ContentType, out var parsed) ? parsed : new ContentType("application", "octet-stream");
            related.Add(new MimePart(type)
            {
                Content = new MimeContent(new MemoryStream(img.Data, false)),
                ContentId = img.ContentId,
                ContentTransferEncoding = ContentEncoding.Base64,
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = img.FileName },
            });
        }
    }

    private static Multipart? FindParent(Multipart container, MimeEntity target)
    {
        foreach (var child in container)
        {
            if (ReferenceEquals(child, target)) return container;
            if (child is Multipart mp && FindParent(mp, target) is { } found) return found;
        }
        return null;
    }
}
