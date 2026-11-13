using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sig2Email.Core.Models;

namespace Sig2Email.Data;

/// <summary>Seeds a fictional tenant so the whole product can be explored without a Microsoft 365 tenant.</summary>
public static class DemoSeeder
{
    public const string EntraTenantId = "00000000-0000-0000-0000-00000000d3a0";
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const string Domain = "contoso.example";

    /// <summary>Password of every demo account. Demo data is refused outside the Development environment.</summary>
    public const string DemoPassword = "sig2email-demo";

    /// <summary>Demo sign-ins; Role null = a manager without an administrative role.</summary>
    public static readonly (string Username, string UserId, string Name, TenantRole? Role)[] Accounts =
    {
        ("owner", "u-owner", "Olivia Owner", TenantRole.Owner),
        ("rules", "u-rules", "Raj Rules", TenantRole.RuleAdmin),
        ("designer", "u-design", "Dana Designer", TenantRole.Designer),
        ("analyst", "u-analyst", "Alex Analyst", TenantRole.AnalyticsViewer),
        ("manager", "u-sales1", "Sam Carter", null),
    };

    public const string CorporateHtml = """
        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#323130;line-height:1.4">
          <tr>
            {if Photo}<td style="padding:0 14px 0 0;vertical-align:top"><img src="{Photo}" width="72" height="72" alt="{DisplayName}" style="border-radius:50%;display:block"></td>{/if}
            <td style="border-left:3px solid #0f6cbd;padding:0 0 0 14px;vertical-align:top">
              <div style="font-size:16px;font-weight:600;color:#0f6cbd">{DisplayName}</div>
              <div>{JobTitle}{if JobTitle and Department} &middot; {/if}{Department}</div>
              <div style="padding-top:6px">
                {if Phone}<span style="color:#605e5c">T:</span> <a href="tel:{Phone}" style="color:#323130;text-decoration:none">{Phone}</a>&nbsp;&nbsp;{/if}
                {if MobilePhone}<span style="color:#605e5c">M:</span> <a href="tel:{MobilePhone}" style="color:#323130;text-decoration:none">{MobilePhone}</a>{/if}
              </div>
              <div><a href="mailto:{Email}" style="color:#0f6cbd;text-decoration:none">{Email}</a> &middot; <a href="https://www.contoso.example" style="color:#0f6cbd;text-decoration:none">www.contoso.example</a></div>
              <div style="color:#605e5c">{Company}{if City}, {City}{/if}{if Country}, {Country}{/if}</div>
            </td>
          </tr>
        </table>
        """;

    public const string ShortHtml = """
        <div style="font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#323130">
          <strong>{DisplayName}</strong>{if JobTitle} | {JobTitle}{/if}<br>
          {if MobilePhone}{MobilePhone} | {/if}<a href="mailto:{Email}" style="color:#0f6cbd">{Email}</a>
        </div>
        """;

    public const string DisclaimerHtml = """
        <p style="font-family:Arial,sans-serif;font-size:10px;color:#8a8886;border-top:1px solid #e1dfdd;padding-top:8px;margin-top:12px">
          This email and any attachments are confidential and intended solely for the addressee. If you have received it in error,
          please notify the sender and delete it. &copy; {CurrentYear} Contoso Ltd.
        </p>
        """;

    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        if (await db.Tenants.AnyAsync(t => t.Id == TenantId, ct)) return;

        var now = DateTime.UtcNow;
        db.Tenants.Add(new Tenant
        {
            Id = TenantId, EntraTenantId = EntraTenantId, Name = "Contoso (demo)",
            Mode = DeploymentMode.Combo, Domains = { Domain }, RelayHost = "localhost", ConsentGranted = true,
            ConsentedAt = now, LastSyncAt = now,
        });
        db.TenantDomains.Add(new TenantDomain { Domain = Domain, TenantId = TenantId });

        var people = new (string Id, string First, string Last, string Title, string Dept, string City, string? Mobile, string? Manager)[]
        {
            ("u-owner", "Olivia", "Owner", "IT Director", "IT", "London", "+44 7700 900101", null),
            ("u-rules", "Raj", "Rules", "Messaging Administrator", "IT", "London", "+44 7700 900102", "u-owner"),
            ("u-design", "Dana", "Designer", "Brand Designer", "Marketing", "Manchester", null, "u-mkt"),
            ("u-analyst", "Alex", "Analyst", "Customer Insights Analyst", "Customer Success", "Leeds", null, "u-cs"),
            ("u-mkt", "Maria", "Keller", "Head of Marketing", "Marketing", "Manchester", "+44 7700 900105", "u-owner"),
            ("u-sales1", "Sam", "Carter", "Sales Director", "Sales", "London", "+44 7700 900106", "u-owner"),
            ("u-sales2", "Priya", "Nair", "Account Executive", "Sales", "London", "+44 7700 900107", "u-sales1"),
            ("u-sales3", "Tom", "Becker", "Account Executive", "Sales", "Birmingham", null, "u-sales1"),
            ("u-cs", "Chloe", "Martin", "Head of Customer Success", "Customer Success", "Leeds", "+44 7700 900109", "u-owner"),
            ("u-cs1", "Ben", "Okafor", "Support Engineer", "Customer Success", "Leeds", null, "u-cs"),
            ("u-cs2", "Lena", "Novak", "Support Engineer", "Customer Success", "Leeds", "+44 7700 900111", "u-cs"),
            ("u-fin", "George", "Hall", "Finance Manager", "Finance", "London", null, "u-owner"),
        };

        var groups = new Dictionary<string, (string Name, GroupKind Kind, Func<string, bool> Member)>
        {
            ["g-all"] = ("All Staff", GroupKind.Distribution, _ => true),
            ["g-sales"] = ("Sales Team", GroupKind.Security, d => d == "Sales"),
            ["g-cs"] = ("Customer Success", GroupKind.Microsoft365, d => d == "Customer Success"),
            ["g-mkt"] = ("Marketing", GroupKind.Microsoft365, d => d == "Marketing"),
        };

        var i = 0;
        foreach (var p in people)
        {
            i++;
            var email = $"{p.First}.{p.Last}@{Domain}".ToLowerInvariant();
            db.Users.Add(new DirectoryUser
            {
                TenantId = TenantId, Id = p.Id, Upn = email, Mail = email, DisplayName = $"{p.First} {p.Last}",
                FirstName = p.First, LastName = p.Last, JobTitle = p.Title, Department = p.Dept, Company = "Contoso Ltd",
                Office = p.City, City = p.City, Country = "United Kingdom", Street = "1 Example Street", PostalCode = "EC1A 1BB",
                Phone = $"+44 20 7946 0{100 + i}", MobilePhone = p.Mobile, ManagerId = p.Manager,
                Addresses = { email }, GroupIds = groups.Where(g => g.Value.Member(p.Dept)).Select(g => g.Key).ToList(),
                CustomAttributes = p.Dept == "Sales" ? new() { ["1"] = "EMEA" } : new(),
            });
            db.Addresses.Add(new DirectoryAddress { TenantId = TenantId, Address = email, UserId = p.Id });
        }
        foreach (var (id, g) in groups)
            db.Groups.Add(new DirectoryGroup
            {
                TenantId = TenantId, Id = id, DisplayName = g.Name, Kind = g.Kind, Mail = $"{g.Name.Replace(" ", "").ToLowerInvariant()}@{Domain}",
                MemberIds = people.Where(p => g.Member(p.Dept)).Select(p => p.Id).ToList(),
            });

        // One hash for all demo accounts keeps start-up (and the test suite) quick.
        var demoHash = Core.Security.PasswordHasher.Hash(DemoPassword);
        foreach (var a in Accounts)
            db.Accounts.Add(new Account
            {
                TenantId = TenantId, Username = a.Username, DisplayName = a.Name, Role = a.Role, DirectoryUserId = a.UserId,
                Email = $"{a.Name.Replace(' ', '.')}@{Domain}".ToLowerInvariant(), PasswordHash = demoHash,
            });

        var survey = new Survey
        {
            TenantId = TenantId, Name = "Support satisfaction", Question = "How would you rate my reply?",
            Style = SurveyStyle.Emoji, NegativeThreshold = 2,
        };
        db.Surveys.Add(survey);

        Template Tpl(string name, string description, string html)
        {
            var t = new Template
            {
                TenantId = TenantId, Name = name, Description = description, Html = html, PublishedVersion = 1,
                HasUnpublishedChanges = false, UpdatedBy = "Dana Designer",
            };
            db.Templates.Add(t);
            db.TemplateVersions.Add(new TemplateVersion { TenantId = TenantId, TemplateId = t.Id, Version = 1, Html = html, PublishedBy = "Raj Rules" });
            return t;
        }

        var corporate = Tpl("Corporate signature", "Full signature for new messages", CorporateHtml);
        var shortSig = Tpl("Short reply signature", "Compact signature for replies and forwards", ShortHtml);
        var csat = Tpl("Customer Success + CSAT", "Corporate signature with a one-click satisfaction survey",
            CorporateHtml + $"\n{{Csat:{survey.Id}}}\n");
        var disclaimer = Tpl("Legal disclaimer", "Appended to all external mail", DisclaimerHtml);

        db.Rules.AddRange(
            new Rule
            {
                TenantId = TenantId, Name = "Legal disclaimer (external mail)", Priority = 1, TemplateId = disclaimer.Id,
                Placement = SignaturePlacement.Bottom, Channels = RuleChannels.Server, OnMatch = OnMatchAction.Continue,
                Conditions = { Recipients = { Scope = RecipientScope.ExternalAny } },
            },
            new Rule
            {
                TenantId = TenantId, Name = "Customer Success – survey signature", Priority = 2, TemplateId = csat.Id,
                OnMatch = OnMatchAction.Stop, TrackLinks = true, UtmSource = "email-signature", UtmMedium = "email", UtmCampaign = "cs-signature",
                Conditions = { Senders = { Everyone = false, GroupIds = { "g-cs" } }, Recipients = { Scope = RecipientScope.ExternalAny } },
            },
            new Rule
            {
                TenantId = TenantId, Name = "Standard signature (new messages)", Priority = 3, TemplateId = corporate.Id,
                OnMatch = OnMatchAction.Stop, UserSelectable = true,
                Conditions = { MessageTypes = { MessageKind.New }, SuppressKeywords = { "#nosig" } },
            },
            new Rule
            {
                TenantId = TenantId, Name = "Short signature (replies & forwards)", Priority = 4, TemplateId = shortSig.Id,
                OnMatch = OnMatchAction.Stop, UserSelectable = true,
                Conditions = { MessageTypes = { MessageKind.Reply, MessageKind.Forward }, SuppressKeywords = { "#nosig" } },
            });

        // Sample analytics so the dashboards are not empty.
        var rnd = new Random(42);
        var agents = people.Where(p => p.Dept == "Customer Success").ToArray();
        string[] comments = { "Fast and helpful, thank you!", "Solved my issue on the first reply.", "Took a while to get an answer.",
            "Still waiting for a proper fix.", "Great service as always.", "" , "", "" };
        for (var day = 29; day >= 0; day--)
        {
            var date = now.Date.AddDays(-day);
            var sent = 4 + rnd.Next(8);
            db.MailStats.Add(new MailStatDaily { TenantId = TenantId, Day = date, Processed = 180 + rnd.Next(140), Stamped = 150 + rnd.Next(120), Failed = 0 });
            for (var n = 0; n < sent; n++)
            {
                var agent = agents[rnd.Next(agents.Length)];
                var at = date.AddHours(8 + rnd.Next(10)).AddMinutes(rnd.Next(60));
                var d = new SurveyDispatch
                {
                    TenantId = TenantId, SurveyId = survey.Id, SenderId = agent.Id, SenderName = $"{agent.First} {agent.Last}",
                    SenderEmail = $"{agent.First}.{agent.Last}@{Domain}".ToLowerInvariant(), Recipients = $"customer{rnd.Next(400)}@fabrikam.example",
                    Subject = $"RE: Ticket #{10000 + rnd.Next(9000)}", CreatedAt = at,
                };
                db.SurveyDispatches.Add(d);
                if (rnd.NextDouble() < 0.45)
                {
                    var roll = rnd.NextDouble();
                    var score = roll < 0.06 ? 1 : roll < 0.14 ? 2 : roll < 0.28 ? 3 : roll < 0.6 ? 4 : 5;
                    var comment = comments[rnd.Next(comments.Length)];
                    if (score <= 2 && comment.Length > 0) comment = comments[2 + rnd.Next(2)];
                    else if (score >= 4 && comment.Length > 0) comment = comments[new[] { 0, 1, 4 }[rnd.Next(3)]];
                    db.SurveyResponses.Add(new SurveyResponse
                    {
                        TenantId = TenantId, SurveyId = survey.Id, DispatchId = d.Id, Score = score, MaxScore = 5,
                        Comment = comment.Length == 0 ? null : comment, SenderId = d.SenderId, SenderEmail = d.SenderEmail,
                        SenderName = d.SenderName, Recipients = d.Recipients, Subject = d.Subject,
                        CreatedAt = at.AddHours(rnd.Next(1, 20)), UpdatedAt = at.AddHours(20),
                    });
                }
            }
            for (var c = rnd.Next(2, 9); c > 0; c--)
                db.LinkClicks.Add(new LinkClick
                {
                    TenantId = TenantId, Url = rnd.NextDouble() < 0.7 ? "https://www.contoso.example" : "https://www.contoso.example/webinar",
                    Campaign = "cs-signature", SenderId = agents[rnd.Next(agents.Length)].Id, CreatedAt = date.AddHours(9 + rnd.Next(9)),
                });
        }

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = TenantId, ActorId = "system", ActorName = "System", Action = "tenant.seeded", EntityType = "Tenant",
            EntityId = TenantId.ToString(), Summary = "Demo tenant created",
        });
        await db.SaveChangesAsync(ct);

        // Local wiring for the demo: no TLS, tenant found by sender domain, stamped mail handed to localhost:2526
        // (the load-test tool or smtp4dev). Change any of it under Settings → System.
        await sp.GetRequiredService<SettingsProvider>().UpdateAsync(x =>
        {
            x.SmtpHostName = "smtp.contoso.example";
            x.SmtpRequireTls = false;
            x.SmtpRequireClientCertificate = false;
            x.TenantIdentification = "SenderDomain";
            x.RelayOverrideHost = "localhost";
            x.RelayPort = 2526;
            x.RelayRequireTls = false;
        }, ct);
    }
}
