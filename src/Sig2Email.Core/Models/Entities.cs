namespace Sig2Email.Core.Models;

public enum TenantRole { Owner, RuleAdmin, Designer, AnalyticsViewer }
public enum DeploymentMode { ServerSide, ClientSide, Combo }
[Flags] public enum RuleChannels { Server = 1, Client = 2, Both = 3 }
public enum OnMatchAction { Continue, Stop }
public enum SignaturePlacement { Bottom, AboveQuotedText }
public enum MessageKind { New, Reply, Forward }
public enum RecipientScope { Any, InternalOnly, ExternalAny, ExternalOnly }
public enum ImageMode { Hosted, Inline }
public enum SurveyStyle { Emoji, Stars, Thumbs, Custom }
public enum GroupKind { Security, Distribution, Microsoft365 }
public enum ExternalAudience { None, ContactsOnly, All }

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Microsoft Entra tenant id (the "tid" claim).</summary>
    public string EntraTenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public DeploymentMode Mode { get; set; } = DeploymentMode.ServerSide;
    /// <summary>Accepted (verified) mail domains, lower-case.</summary>
    public List<string> Domains { get; set; } = new();
    /// <summary>Where stamped mail is handed back, e.g. contoso-com.mail.protection.outlook.com.</summary>
    public string? RelayHost { get; set; }
    public bool ConsentGranted { get; set; }
    public DateTime? ConsentedAt { get; set; }
    public string? UsersDeltaLink { get; set; }
    public string? GroupsDeltaLink { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime? ManagersSyncedAt { get; set; }
    public string? LastSyncError { get; set; }
    public bool SyncRequested { get; set; }
    /// <summary>Bumped on every change that affects mail-flow behaviour; data-plane nodes reload on change.</summary>
    public long ConfigVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TenantDomain
{
    public string Domain { get; set; } = "";
    public Guid TenantId { get; set; }
}

/// <summary>A portal sign-in: user name and password, with the role it acts in.</summary>
public class Account : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Lower-case, unique across the installation.</summary>
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    /// <summary>Null = no administrative role: the account can only manage auto-replies for the linked person's direct reports.</summary>
    public TenantRole? Role { get; set; }
    /// <summary>Optional link to the person in the synchronised directory (needed for manager-delegated auto-replies).</summary>
    public string? DirectoryUserId { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Set when an administrator chose the password; the portal asks the user to replace it.</summary>
    public bool MustChangePassword { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    /// <summary>Embedded in session tokens; changing it signs the account out everywhere.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>The single row holding installation-wide settings (encrypted JSON), edited in the admin portal.</summary>
public class SystemSettingsRow
{
    public int Id { get; set; } = 1;
    public string Data { get; set; } = "";
    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class DirectoryUser : ITenantOwned
{
    public Guid TenantId { get; set; }
    /// <summary>Entra object id.</summary>
    public string Id { get; set; } = "";
    public string Upn { get; set; } = "";
    public string? Mail { get; set; }
    public string DisplayName { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Company { get; set; }
    public string? Office { get; set; }
    public string? Phone { get; set; }
    public string? MobilePhone { get; set; }
    public string? Fax { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? ManagerId { get; set; }
    public bool AccountEnabled { get; set; } = true;
    /// <summary>All SMTP addresses of the mailbox (primary + aliases), lower-case.</summary>
    public List<string> Addresses { get; set; } = new();
    /// <summary>Keys "1".."15" → onPremisesExtensionAttributes / custom attributes.</summary>
    public Dictionary<string, string> CustomAttributes { get; set; } = new();
    /// <summary>Transitive group membership, recomputed after each directory sync.</summary>
    public List<string> GroupIds { get; set; } = new();
    public string? PhotoBlobKey { get; set; }
    public string? PhotoVersion { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string PrimaryEmail => string.IsNullOrEmpty(Mail) ? Upn : Mail!;
}

/// <summary>Address → user index used for sender lookup during mail transit.</summary>
public class DirectoryAddress : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Address { get; set; } = "";
    public string UserId { get; set; } = "";
}

public class DirectoryGroup : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Mail { get; set; }
    public GroupKind Kind { get; set; }
    /// <summary>Direct members (user or group object ids).</summary>
    public List<string> MemberIds { get; set; } = new();
}

public class Template : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Working copy (draft). Live mail flow only ever uses the published version.</summary>
    public string Html { get; set; } = "";
    /// <summary>Block-editor document, null when the template is maintained in source mode.</summary>
    public string? DesignJson { get; set; }
    public ImageMode ImageMode { get; set; } = ImageMode.Hosted;
    public int? PublishedVersion { get; set; }
    public bool HasUnpublishedChanges { get; set; } = true;
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TemplateVersion : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public string Html { get; set; } = "";
    public string? DesignJson { get; set; }
    public ImageMode ImageMode { get; set; }
    public string PublishedBy { get; set; } = "";
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}

public class Rule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Evaluation order, ascending.</summary>
    public int Priority { get; set; }
    public Guid TemplateId { get; set; }
    public SignaturePlacement Placement { get; set; } = SignaturePlacement.AboveQuotedText;
    public RuleChannels Channels { get; set; } = RuleChannels.Both;
    public OnMatchAction OnMatch { get; set; } = OnMatchAction.Stop;
    /// <summary>Offered in the Outlook task pane as a manually selectable alternative.</summary>
    public bool UserSelectable { get; set; }
    public bool TrackLinks { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public RuleConditions Conditions { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Asset : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string BlobKey { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AutoReplyPolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> UserIds { get; set; } = new();
    public List<string> GroupIds { get; set; } = new();
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string InternalMessage { get; set; } = "";
    public string ExternalMessage { get; set; } = "";
    public ExternalAudience ExternalAudience { get; set; } = ExternalAudience.All;
    public string? ForwardTo { get; set; }
    /// <summary>Entra object id of the creator; set for manager-delegated policies.</summary>
    public string CreatedById { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public bool ManagerDelegated { get; set; }
    /// <summary>Set whenever the policy changes so the worker re-applies it.</summary>
    public bool Dirty { get; set; } = true;
    /// <summary>Soft delete: the worker reverts the mailboxes, then removes the row.</summary>
    public bool Deleted { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-mailbox application state of an auto-reply policy.</summary>
public class AutoReplyState : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PolicyId { get; set; }
    public string UserId { get; set; } = "";
    public string? ForwardRuleId { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? RevertedAt { get; set; }
    public string? Error { get; set; }
}

public class Survey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Question { get; set; } = "How did I do today?";
    public SurveyStyle Style { get; set; } = SurveyStyle.Emoji;
    /// <summary>For <see cref="SurveyStyle.Custom"/>: one entry per score, lowest first.</summary>
    public List<SurveyIcon> CustomIcons { get; set; } = new();
    public string ThankYouMessage { get; set; } = "Thank you for your feedback!";
    public bool AskComment { get; set; } = true;
    /// <summary>Scores at or below this value raise a negative-rating alert.</summary>
    public int NegativeThreshold { get; set; } = 2;
    public List<string> AlertEmails { get; set; } = new();
    public string? WebhookUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int MaxScore => Style switch
    {
        SurveyStyle.Thumbs => 2,
        SurveyStyle.Custom => Math.Max(2, CustomIcons.Count),
        _ => 5,
    };
}

public class SurveyIcon
{
    public string ImageUrl { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>One survey widget stamped into one email. Metadata only – never message content.</summary>
public class SurveyDispatch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SurveyId { get; set; }
    public Guid? RuleId { get; set; }
    public string SenderId { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Recipients { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? MessageId { get; set; }
    /// <summary>Client-side compose session key so re-renders reuse one dispatch.</summary>
    public string? ComposeKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SurveyResponse : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SurveyId { get; set; }
    public Guid DispatchId { get; set; }
    public int Score { get; set; }
    public int MaxScore { get; set; }
    public string? Comment { get; set; }
    public string SenderId { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Recipients { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool AlertSent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class LinkClick : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? RuleId { get; set; }
    public string Url { get; set; } = "";
    public string? Campaign { get; set; }
    public string? SenderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MailStatDaily : ITenantOwned
{
    public Guid TenantId { get; set; }
    public DateTime Day { get; set; }
    public long Processed { get; set; }
    public long Stamped { get; set; }
    public long Failed { get; set; }
}

public class AuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Summary { get; set; }
    public string? Ip { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}
