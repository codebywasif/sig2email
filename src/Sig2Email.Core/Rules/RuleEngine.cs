using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;

namespace Sig2Email.Core.Rules;

public sealed class MessageFacts
{
    public required string SenderAddress { get; init; }
    public required IReadOnlyList<string> Recipients { get; init; }
    public MessageKind Kind { get; init; }
    public string Subject { get; init; } = "";
    /// <summary>Lazily produced plain text of the body. Null when unavailable (client-side compose).</summary>
    public Func<string>? BodyText { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

public sealed record RuleMatch(Rule Rule, bool AlreadyApplied);

public static class RuleEngine
{
    /// <summary>
    /// Walks the ordered pipeline and returns the rules that apply. A rule whose id is in
    /// <paramref name="alreadyApplied"/> (stamped by the Outlook add-in in combo mode) still takes part in
    /// stop/continue flow control but is flagged so the caller does not stamp it twice.
    /// </summary>
    public static List<RuleMatch> Evaluate(
        IEnumerable<Rule> orderedRules,
        MessageFacts msg,
        DirectoryUser sender,
        IReadOnlySet<string> tenantDomains,
        RuleChannels channel,
        IReadOnlySet<Guid>? alreadyApplied = null)
    {
        var result = new List<RuleMatch>();
        Dictionary<string, string?>? attrs = null;
        string? body = null;

        foreach (var rule in orderedRules)
        {
            if (!rule.Enabled) continue;
            var applied = alreadyApplied?.Contains(rule.Id) == true;
            if (!applied)
            {
                if ((rule.Channels & channel) == 0) continue;
                if (!Matches(rule.Conditions, msg, sender, tenantDomains, channel, ref attrs, ref body)) continue;
            }
            result.Add(new RuleMatch(rule, applied));
            if (rule.OnMatch == OnMatchAction.Stop) break;
        }
        return result;
    }

    public static bool Matches(RuleConditions c, MessageFacts msg, DirectoryUser sender, IReadOnlySet<string> tenantDomains,
        RuleChannels channel)
    {
        Dictionary<string, string?>? attrs = null;
        string? body = null;
        return Matches(c, msg, sender, tenantDomains, channel, ref attrs, ref body);
    }

    private static bool Matches(RuleConditions c, MessageFacts msg, DirectoryUser sender, IReadOnlySet<string> tenantDomains,
        RuleChannels channel, ref Dictionary<string, string?>? attrs, ref string? body)
    {
        if (c.StartUtc is { } start && msg.NowUtc < start) return false;
        if (c.EndUtc is { } end && msg.NowUtc > end) return false;
        if (c.MessageTypes.Count > 0 && !c.MessageTypes.Contains(msg.Kind)) return false;
        if (!SenderMatches(c.Senders, sender, ref attrs)) return false;
        if (!RecipientsMatch(c.Recipients, msg.Recipients, tenantDomains)) return false;

        if (c.SubjectKeywords.Count > 0 && !ContainsAny(msg.Subject, c.SubjectKeywords)) return false;

        var needsBody = c.BodyKeywords.Count > 0 || c.SuppressKeywords.Count > 0;
        if (needsBody)
        {
            // Body conditions cannot be evaluated while a message is still being composed, so rules that
            // depend on them are left to the server-side pass.
            if (c.BodyKeywords.Count > 0 && (channel == RuleChannels.Client || msg.BodyText == null)) return false;
            if (msg.BodyText != null) body ??= msg.BodyText();
            if (c.BodyKeywords.Count > 0 && !ContainsAny(body!, c.BodyKeywords)) return false;
            if (c.SuppressKeywords.Count > 0 &&
                (ContainsAny(msg.Subject, c.SuppressKeywords) || (body != null && ContainsAny(body, c.SuppressKeywords))))
                return false;
        }
        return true;
    }

    public static bool SenderMatches(SenderConditions s, DirectoryUser sender)
    {
        Dictionary<string, string?>? attrs = null;
        return SenderMatches(s, sender, ref attrs);
    }

    private static bool SenderMatches(SenderConditions s, DirectoryUser sender, ref Dictionary<string, string?>? attrs)
    {
        if (s.ExcludeUserIds.Contains(sender.Id)) return false;
        if (s.ExcludeGroupIds.Count > 0 && s.ExcludeGroupIds.Any(sender.GroupIds.Contains)) return false;
        if (s.Everyone) return true;

        if (s.UserIds.Contains(sender.Id)) return true;
        if (s.GroupIds.Count > 0 && s.GroupIds.Any(sender.GroupIds.Contains)) return true;
        if (s.Filters.Count > 0)
        {
            attrs ??= Placeholders.ForUser(sender);
            var a = attrs;
            bool Test(AttributeFilter f)
            {
                a.TryGetValue(f.Attribute, out var v);
                v = v?.Trim() ?? "";
                var target = f.Value?.Trim() ?? "";
                return f.Op switch
                {
                    FilterOp.Equals => v.Equals(target, StringComparison.OrdinalIgnoreCase),
                    FilterOp.NotEquals => !v.Equals(target, StringComparison.OrdinalIgnoreCase),
                    FilterOp.Contains => v.Contains(target, StringComparison.OrdinalIgnoreCase),
                    FilterOp.StartsWith => v.StartsWith(target, StringComparison.OrdinalIgnoreCase),
                    FilterOp.IsEmpty => v.Length == 0,
                    FilterOp.IsNotEmpty => v.Length > 0,
                    _ => false,
                };
            }
            return s.FilterMatch == FilterMatch.All ? s.Filters.All(Test) : s.Filters.Any(Test);
        }
        return false;
    }

    private static bool RecipientsMatch(RecipientConditions r, IReadOnlyList<string> recipients, IReadOnlySet<string> tenantDomains)
    {
        if (r.Exclude.Count > 0 && recipients.Any(a => MatchesAny(a, r.Exclude))) return false;
        if (r.Domains.Count > 0 && !recipients.Any(a => MatchesAny(a, r.Domains))) return false;
        if (r.Scope == RecipientScope.Any) return true;

        // With no recipients yet (a freshly opened compose window) treat the message as internal.
        var external = recipients.Count(a => !tenantDomains.Contains(DomainOf(a)));
        return r.Scope switch
        {
            RecipientScope.InternalOnly => external == 0,
            RecipientScope.ExternalAny => external > 0,
            RecipientScope.ExternalOnly => recipients.Count > 0 && external == recipients.Count,
            _ => true,
        };
    }

    public static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : address[(at + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>Entries are full addresses (a@b.com) or domains (b.com, also matching sub.b.com).</summary>
    private static bool MatchesAny(string address, List<string> patterns)
    {
        var domain = DomainOf(address);
        foreach (var raw in patterns)
        {
            var p = raw.Trim().TrimStart('@').ToLowerInvariant();
            if (p.Length == 0) continue;
            if (p.Contains('@'))
            {
                if (address.Trim().Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (domain == p || domain.EndsWith("." + p, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool ContainsAny(string text, List<string> keywords) =>
        keywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase));
}
