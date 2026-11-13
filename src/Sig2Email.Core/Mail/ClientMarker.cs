namespace Sig2Email.Core.Mail;

/// <summary>
/// Parsed <c>X-Sig2Email-Client</c> header, e.g. <c>manual; rules=0f3a…,9b1c…</c>. The Outlook add-in sets it so
/// the server-side pass does not stamp the same signature again (combo mode).
/// </summary>
public sealed record ClientMarker(bool Manual, IReadOnlySet<Guid> RuleIds)
{
    public static readonly ClientMarker None = new(false, new HashSet<Guid>());

    public static ClientMarker Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return None;
        var manual = false;
        var ids = new HashSet<Guid>();
        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("manual", StringComparison.OrdinalIgnoreCase)) manual = true;
            else if (part.StartsWith("rules=", StringComparison.OrdinalIgnoreCase))
                foreach (var id in part[6..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (Guid.TryParse(id, out var g)) ids.Add(g);
        }
        return new ClientMarker(manual, ids);
    }
}
