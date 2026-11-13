using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sig2Email.Core.Mail;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;
using Sig2Email.Core.Rules;
using Sig2Email.Core.Security;

namespace Sig2Email.Data;

public sealed class ComposeRequest
{
    public required TenantRuntime Runtime { get; init; }
    public required DirectoryUser Sender { get; init; }
    public required MessageFacts Facts { get; init; }
    public required RuleChannels Channel { get; init; }
    /// <summary>Rules already stamped client-side (combo mode).</summary>
    public IReadOnlySet<Guid>? AlreadyApplied { get; init; }
    /// <summary>Restrict the pipeline to rules satisfying this predicate.</summary>
    public Func<Rule, bool>? RuleFilter { get; init; }
    public string? MessageId { get; init; }
    /// <summary>Stable id of an Outlook compose session, so re-rendering reuses one survey dispatch.</summary>
    public string? ComposeKey { get; init; }
    /// <summary>Preview/simulation: nothing is persisted and survey links open the landing page in demo mode.</summary>
    public bool Preview { get; init; }
}

public sealed record ComposeResult(List<SignaturePart> Parts, List<RuleMatch> Matches);

/// <summary>Turns "who is sending what to whom" into rendered signatures. Shared by the SMTP pipeline, the
/// Outlook add-in endpoints and the portal's preview/simulator so all three behave identically.</summary>
public sealed partial class SignatureService(
    SettingsProvider settings, TokenSigner signer, IBlobStore blobs, IMemoryCache cache, DispatchQueue dispatches)
{
    private string _base => settings.Current.BaseUrl;

    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*)""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();

    public string PhotoUrl(DirectoryUser u) =>
        u.PhotoBlobKey == null ? "" : $"{_base}/assets/photo/{u.TenantId:N}/{u.Id}?v={Uri.EscapeDataString(u.PhotoVersion ?? "1")}";

    public string QrUrl(DirectoryUser u) => $"{_base}/assets/qr/{u.TenantId:N}/{u.Id}.png?k={signer.Mac($"qr:{u.TenantId:N}:{u.Id}")}";

    public bool VerifyQr(Guid tenantId, string userId, string? mac) => signer.Verify($"qr:{tenantId:N}:{userId}", mac);

    public string AssetUrl(Asset a) => $"{_base}/assets/{a.TenantId:N}/{a.Id:N}/{Uri.EscapeDataString(a.FileName)}";

    public Dictionary<string, string?> ValuesFor(DirectoryUser sender, DateTime nowUtc)
    {
        var values = Placeholders.ForUser(sender);
        values["Photo"] = PhotoUrl(sender);
        values["QrCode"] = QrUrl(sender);
        values["CurrentYear"] = nowUtc.Year.ToString(CultureInfo.InvariantCulture);
        values["CurrentDate"] = nowUtc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        return values;
    }

    public async Task<ComposeResult> ComposeAsync(ComposeRequest req, CancellationToken ct = default)
    {
        var rt = req.Runtime;
        var candidates = req.RuleFilter == null ? rt.Rules : rt.Rules.Where(r => req.RuleFilter(r.Rule)).ToList();
        var matches = RuleEngine.Evaluate(candidates.Select(r => r.Rule), req.Facts, req.Sender, rt.Domains, req.Channel, req.AlreadyApplied);
        var parts = new List<SignaturePart>();
        if (matches.Count == 0) return new ComposeResult(parts, matches);

        var values = ValuesFor(req.Sender, req.Facts.NowUtc);
        var byId = candidates.ToDictionary(r => r.Rule.Id);

        foreach (var match in matches)
        {
            if (match.AlreadyApplied) continue;
            var rr = byId[match.Rule.Id];
            parts.Add(await RenderRuleAsync(rr.Rule, rr.Template, rr.ImageMode, req, values, ct));
        }
        return new ComposeResult(parts, matches);
    }

    /// <summary>Renders one template for one sender (also used directly for designer previews).</summary>
    public async Task<SignaturePart> RenderRuleAsync(Rule rule, CompiledTemplate template, ImageMode imageMode, ComposeRequest req,
        Dictionary<string, string?>? values = null, CancellationToken ct = default)
    {
        values ??= ValuesFor(req.Sender, req.Facts.NowUtc);
        var ctx = new RenderContext
        {
            Values = values,
            SurveyHtml = surveyId =>
            {
                if (!req.Runtime.Surveys.TryGetValue(surveyId, out var survey)) return "";
                var dispatchId = Guid.Empty;
                if (!req.Preview)
                {
                    dispatchId = req.ComposeKey == null ? Guid.NewGuid() : DeterministicId(req.Runtime.Tenant.Id, req.ComposeKey, surveyId);
                    dispatches.Enqueue(new SurveyDispatch
                    {
                        Id = dispatchId,
                        TenantId = req.Runtime.Tenant.Id,
                        SurveyId = surveyId,
                        RuleId = rule.Id == Guid.Empty ? null : rule.Id,
                        SenderId = req.Sender.Id,
                        SenderEmail = req.Sender.PrimaryEmail,
                        SenderName = req.Sender.DisplayName,
                        Recipients = Truncate(string.Join(", ", req.Facts.Recipients.Take(5)), 400),
                        Subject = Truncate(req.Facts.Subject, 200),
                        MessageId = req.MessageId,
                        ComposeKey = req.ComposeKey,
                    });
                }
                return SurveyWidget.Render(survey, _base, dispatchId, signer);
            },
        };

        var html = template.Render(ctx);
        if (!req.Preview) html = LinkRewriter.Rewrite(html, rule, req.Runtime.Tenant.Id, req.Sender.Id, _base, signer);
        var text = HtmlConverter.ToPlainText(html);

        var images = new List<InlineImage>();
        if (imageMode == ImageMode.Inline && req.Channel == RuleChannels.Server && !req.Preview)
            html = await InlineImagesAsync(html, req.Sender, images, ct);

        return new SignaturePart(rule.Id, html, text, rule.Placement, images);
    }

    private async Task<string> InlineImagesAsync(string html, DirectoryUser sender, List<InlineImage> images, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (Match m in ImgSrc().Matches(html))
        {
            var url = WebUtility.HtmlDecode(m.Groups[2].Value);
            if (map.ContainsKey(m.Groups[2].Value) || !url.StartsWith(_base + "/assets/", StringComparison.OrdinalIgnoreCase)) continue;
            var blob = await LoadAssetAsync(url[(_base.Length + "/assets/".Length)..], sender, ct);
            if (blob == null) continue;
            var cid = "s2e-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 8).ToLowerInvariant() + "@sig2email";
            var ext = blob.ContentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", _ => "" };
            images.Add(new InlineImage(cid, blob.Bytes, blob.ContentType, "image" + (images.Count + 1) + ext));
            map[m.Groups[2].Value] = "cid:" + cid;
        }
        return map.Count == 0 ? html : ImgSrc().Replace(html, m => map.TryGetValue(m.Groups[2].Value, out var cid) ? $"{m.Groups[1].Value}\"{cid}\"" : m.Value);
    }

    /// <param name="path">Part of a hosted asset URL after "/assets/".</param>
    private async Task<BlobData?> LoadAssetAsync(string path, DirectoryUser sender, CancellationToken ct)
    {
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        var seg = path.Split('/');
        // Hosted URLs are only ever resolved within the sender's own tenant.
        if (seg.Length == 3 && seg[0] == "qr")
            return seg[1] == sender.TenantId.ToString("N") ? new BlobData(VCard.QrPng(sender), "image/png") : null;
        string key;
        if (seg.Length == 3 && seg[0] == "photo" && seg[1] == sender.TenantId.ToString("N")) key = BlobKeys.Photo(sender.TenantId, seg[2]);
        else if (seg.Length == 3 && seg[0] == sender.TenantId.ToString("N") && Guid.TryParse(seg[1], out var assetId)) key = BlobKeys.Asset(sender.TenantId, assetId);
        else return null;

        return await cache.GetOrCreateAsync("blob:" + key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return blobs.GetAsync(key, ct);
        });
    }

    private static Guid DeterministicId(Guid tenantId, string composeKey, Guid surveyId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{composeKey}|{surveyId:N}")).AsSpan(0, 16));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
