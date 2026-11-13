namespace Sig2Email.Core.Models;

public enum FilterOp { Equals, NotEquals, Contains, StartsWith, IsEmpty, IsNotEmpty }
public enum FilterMatch { All, Any }

public class AttributeFilter
{
    /// <summary>Placeholder name, e.g. Department, Country, CustomAttribute3.</summary>
    public string Attribute { get; set; } = "";
    public FilterOp Op { get; set; }
    public string? Value { get; set; }
}

public class SenderConditions
{
    /// <summary>When true every sender in the tenant matches (exclusions still apply).</summary>
    public bool Everyone { get; set; } = true;
    public List<string> UserIds { get; set; } = new();
    /// <summary>Security groups, distribution lists or Microsoft 365 groups (membership is transitive).</summary>
    public List<string> GroupIds { get; set; } = new();
    public List<AttributeFilter> Filters { get; set; } = new();
    public FilterMatch FilterMatch { get; set; } = FilterMatch.All;
    public List<string> ExcludeUserIds { get; set; } = new();
    public List<string> ExcludeGroupIds { get; set; } = new();
}

public class RecipientConditions
{
    public RecipientScope Scope { get; set; } = RecipientScope.Any;
    /// <summary>When non-empty, at least one recipient must be in one of these domains.</summary>
    public List<string> Domains { get; set; } = new();
    /// <summary>The rule is skipped when any recipient matches one of these domains or addresses.</summary>
    public List<string> Exclude { get; set; } = new();
}

public class RuleConditions
{
    public SenderConditions Senders { get; set; } = new();
    public RecipientConditions Recipients { get; set; } = new();
    /// <summary>Empty = all message types.</summary>
    public List<MessageKind> MessageTypes { get; set; } = new();
    /// <summary>When non-empty the subject must contain one of these.</summary>
    public List<string> SubjectKeywords { get; set; } = new();
    /// <summary>When non-empty the body must contain one of these (server-side only).</summary>
    public List<string> BodyKeywords { get; set; } = new();
    /// <summary>The rule is suppressed when subject or body contains one of these (e.g. "#nosig").</summary>
    public List<string> SuppressKeywords { get; set; } = new();
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
}
