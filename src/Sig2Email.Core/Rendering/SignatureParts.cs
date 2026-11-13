using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QRCoder;
using Sig2Email.Core.Models;
using Sig2Email.Core.Security;

namespace Sig2Email.Core.Rendering;

public static class SurveyWidget
{
    private static readonly (string Glyph, string Label)[] Emoji =
    {
        ("\U0001F621", "Very unhappy"), ("\U0001F641", "Unhappy"), ("\U0001F610", "Neutral"),
        ("\U0001F642", "Happy"), ("\U0001F60D", "Very happy"),
    };

    public static string RatingUrl(string publicBaseUrl, Guid dispatchId, int score, TokenSigner signer) =>
        $"{publicBaseUrl}/r/{dispatchId:N}/{score}?k={signer.Mac(dispatchId.ToString("N"))}";

    public static string Render(Survey s, string publicBaseUrl, Guid dispatchId, TokenSigner signer)
    {
        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0\"><tr>")
          .Append("<td style=\"font-family:Arial,Helvetica,sans-serif;font-size:13px;color:#555555;padding:0 0 4px 0\">")
          .Append(WebUtility.HtmlEncode(s.Question)).Append("</td></tr><tr><td>");

        for (var score = 1; score <= s.MaxScore; score++)
        {
            var url = WebUtility.HtmlEncode(RatingUrl(publicBaseUrl, dispatchId, score, signer));
            string inner, label;
            switch (s.Style)
            {
                case SurveyStyle.Stars:
                    label = score == 1 ? "1 star" : $"{score} stars";
                    inner = "<span style=\"color:#f5a623\">&#9733;</span>";
                    break;
                case SurveyStyle.Thumbs:
                    label = score == 1 ? "Thumbs down" : "Thumbs up";
                    inner = score == 1 ? "&#128078;" : "&#128077;";
                    break;
                case SurveyStyle.Custom:
                    var icon = s.CustomIcons[Math.Min(score - 1, s.CustomIcons.Count - 1)];
                    label = icon.Label;
                    inner = $"<img src=\"{WebUtility.HtmlEncode(icon.ImageUrl)}\" alt=\"{WebUtility.HtmlEncode(icon.Label)}\" height=\"32\" style=\"border:0;height:32px\">";
                    break;
                default:
                    label = Emoji[score - 1].Label;
                    inner = Emoji[score - 1].Glyph;
                    break;
            }
            sb.Append("<a href=\"").Append(url).Append("\" title=\"").Append(WebUtility.HtmlEncode(label))
              .Append("\" style=\"text-decoration:none;font-size:26px;line-height:32px;margin-right:6px\">")
              .Append(inner).Append("</a>");
            if (score < s.MaxScore) sb.Append("&nbsp;");
        }
        return sb.Append("</td></tr></table>").ToString();
    }
}

public sealed record TrackedLink(Guid T, Guid? R, string? S, string U, string? C);

public static partial class LinkRewriter
{
    [GeneratedRegex(@"(<a\b[^>]*?\bhref\s*=\s*)(?:""(https?://[^""]+)""|'(https?://[^']+)')", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    /// <summary>Adds UTM parameters and/or routes links through the click-tracking redirect.</summary>
    public static string Rewrite(string html, Rule rule, Guid tenantId, string senderId, string publicBaseUrl, TokenSigner signer)
    {
        var hasUtm = !string.IsNullOrWhiteSpace(rule.UtmSource) || !string.IsNullOrWhiteSpace(rule.UtmMedium) ||
                     !string.IsNullOrWhiteSpace(rule.UtmCampaign);
        if (!rule.TrackLinks && !hasUtm) return html;

        return Href().Replace(html, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            if (url.StartsWith(publicBaseUrl + "/r/", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith(publicBaseUrl + "/l/", StringComparison.OrdinalIgnoreCase))
                return m.Value;

            if (hasUtm) url = AddUtm(url, rule);
            if (rule.TrackLinks)
            {
                var payload = JsonSerializer.Serialize(new TrackedLink(tenantId, rule.Id, senderId, url, rule.UtmCampaign));
                url = $"{publicBaseUrl}/l/{signer.Pack(payload)}";
            }
            return m.Groups[1].Value + "\"" + WebUtility.HtmlEncode(url) + "\"";
        });
    }

    private static string AddUtm(string url, Rule rule)
    {
        var hash = url.IndexOf('#');
        var fragment = hash >= 0 ? url[hash..] : "";
        if (hash >= 0) url = url[..hash];
        var sb = new StringBuilder(url);
        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (url.Contains(key + "=", StringComparison.OrdinalIgnoreCase)) return;
            sb.Append(sb.ToString().Contains('?') ? '&' : '?').Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
        }
        Add("utm_source", rule.UtmSource);
        Add("utm_medium", rule.UtmMedium);
        Add("utm_campaign", rule.UtmCampaign);
        return sb.Append(fragment).ToString();
    }
}

public static class VCard
{
    public static string Build(DirectoryUser u)
    {
        static string E(string? s) => (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");
        var sb = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\n");
        sb.Append($"N:{E(u.LastName)};{E(u.FirstName)};;;\r\n");
        sb.Append($"FN:{E(u.DisplayName)}\r\n");
        if (!string.IsNullOrWhiteSpace(u.Company)) sb.Append($"ORG:{E(u.Company)}\r\n");
        if (!string.IsNullOrWhiteSpace(u.JobTitle)) sb.Append($"TITLE:{E(u.JobTitle)}\r\n");
        if (!string.IsNullOrWhiteSpace(u.Phone)) sb.Append($"TEL;TYPE=WORK,VOICE:{E(u.Phone)}\r\n");
        if (!string.IsNullOrWhiteSpace(u.MobilePhone)) sb.Append($"TEL;TYPE=CELL:{E(u.MobilePhone)}\r\n");
        sb.Append($"EMAIL;TYPE=INTERNET:{E(u.PrimaryEmail)}\r\n");
        if (!string.IsNullOrWhiteSpace(u.Street) || !string.IsNullOrWhiteSpace(u.City))
            sb.Append($"ADR;TYPE=WORK:;;{E(u.Street)};{E(u.City)};{E(u.State)};{E(u.PostalCode)};{E(u.Country)}\r\n");
        return sb.Append("END:VCARD\r\n").ToString();
    }

    public static byte[] QrPng(DirectoryUser u, int pixelsPerModule = 4)
    {
        using var data = QRCodeGenerator.GenerateQrCode(Build(u), QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
