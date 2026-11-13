using Microsoft.EntityFrameworkCore;
using Sig2Email.Core.Mail;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rules;
using Sig2Email.Data;

namespace Sig2Email.Api.Endpoints;

public static class InsightEndpoints
{
    public record SurveyInput(string Name, string Question, SurveyStyle Style, List<SurveyIcon>? CustomIcons, string ThankYouMessage, bool AskComment,
        int NegativeThreshold, List<string>? AlertEmails, string? WebhookUrl, bool Enabled);
    public record AutoReplyInput(string Name, bool Enabled, List<string>? UserIds, List<string>? GroupIds, DateTime StartUtc, DateTime EndUtc,
        string InternalMessage, string ExternalMessage, ExternalAudience ExternalAudience, string? ForwardTo);
    public record AddinRequest(List<string>? Recipients, MessageKind MessageType, string? Subject, string? ComposeKey, Guid? RuleId);

    public static void MapInsightEndpoints(this WebApplication app)
    {
        var ruleAdmins = new[] { TenantRole.RuleAdmin };
        var analysts = new[] { TenantRole.RuleAdmin, TenantRole.AnalyticsViewer };

        // ----- surveys -----
        app.MapGet("/api/surveys", (AppDbContext db) => db.Surveys.AsNoTracking().OrderBy(s => s.Name).ToListAsync()).RequireRoles(Auth.AllRoles);

        app.MapPost("/api/surveys", async (SurveyInput req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            if (ValidateSurvey(req) is { } problem) return problem;
            var s = new Survey();
            Apply(s, req);
            db.Surveys.Add(s);
            db.Log(http, "survey.created", "Survey", s.Id, s.Name);
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.Ok(s);
        }).RequireRoles(ruleAdmins);

        app.MapPut("/api/surveys/{id:guid}", async (Guid id, SurveyInput req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            if (ValidateSurvey(req) is { } problem) return problem;
            var s = await db.Surveys.FirstOrDefaultAsync(x => x.Id == id);
            if (s == null) return Results.NotFound();
            Apply(s, req);
            db.Log(http, "survey.updated", "Survey", s.Id, s.Name);
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.Ok(s);
        }).RequireRoles(ruleAdmins);

        app.MapDelete("/api/surveys/{id:guid}", async (Guid id, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            var s = await db.Surveys.FirstOrDefaultAsync(x => x.Id == id);
            if (s == null) return Results.NotFound();
            var token = $"{{Csat:{id}}}";
            if (await db.Templates.AnyAsync(t => t.Html.Contains(token)))
                return Results.Conflict(new { error = "in_use", message = "This survey is embedded in a template. Remove it from the template first." });
            db.Surveys.Remove(s);
            db.Log(http, "survey.deleted", "Survey", s.Id, s.Name);
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.NoContent();
        }).RequireRoles(ruleAdmins);

        // ----- analytics -----
        app.MapGet("/api/analytics/overview", async (AppDbContext db) =>
        {
            var from = DateTime.UtcNow.Date.AddDays(-29);
            var mail = await db.MailStats.AsNoTracking().Where(m => m.Day >= from).OrderBy(m => m.Day).ToListAsync();
            var responses = await db.SurveyResponses.Where(r => r.CreatedAt >= from).Select(r => new { r.Score, r.MaxScore }).ToListAsync();
            return new
            {
                mail = Days(from, 30).Select(d =>
                {
                    var m = mail.FirstOrDefault(x => x.Day == d);
                    return new { day = d, processed = m?.Processed ?? 0, stamped = m?.Stamped ?? 0, failed = m?.Failed ?? 0 };
                }),
                processed = mail.Sum(m => m.Processed),
                stamped = mail.Sum(m => m.Stamped),
                responses = responses.Count,
                csat = responses.Count == 0 ? (double?)null : Math.Round(100.0 * responses.Count(r => Satisfied(r.Score, r.MaxScore)) / responses.Count, 1),
                clicks = await db.LinkClicks.CountAsync(c => c.CreatedAt >= from),
            };
        }).RequireRoles(Auth.AllRoles);

        app.MapGet("/api/analytics/csat", async (Guid? surveyId, int? days, AppDbContext db) =>
        {
            var span = Math.Clamp(days ?? 30, 1, 365);
            var from = DateTime.UtcNow.Date.AddDays(-(span - 1));
            var rq = db.SurveyResponses.AsNoTracking().Where(r => r.CreatedAt >= from);
            var dq = db.SurveyDispatches.Where(d => d.CreatedAt >= from);
            if (surveyId != null)
            {
                rq = rq.Where(r => r.SurveyId == surveyId);
                dq = dq.Where(d => d.SurveyId == surveyId);
            }
            var rows = await rq.Select(r => new { r.Score, r.MaxScore, r.CreatedAt, r.SenderName, r.SenderEmail }).ToListAsync();
            var dispatched = await dq.CountAsync();
            var max = rows.Count == 0 ? 5 : rows.Max(r => r.MaxScore);
            double? Csat<T>(IReadOnlyCollection<T> set, Func<T, bool> ok) => set.Count == 0 ? null : Math.Round(100.0 * set.Count(ok) / set.Count, 1);

            return new
            {
                days = span,
                summary = new
                {
                    dispatched,
                    responses = rows.Count,
                    responseRate = dispatched == 0 ? (double?)null : Math.Round(100.0 * rows.Count / dispatched, 1),
                    average = rows.Count == 0 ? (double?)null : Math.Round(rows.Average(r => (double)r.Score), 2),
                    maxScore = max,
                    csat = Csat(rows, r => Satisfied(r.Score, r.MaxScore)),
                    negative = rows.Count(r => Negative(r.Score, r.MaxScore)),
                },
                distribution = Enumerable.Range(1, max).Select(score => new { score, count = rows.Count(r => r.Score == score) }),
                trend = Days(from, span).Select(d =>
                {
                    var day = rows.Where(r => r.CreatedAt.Date == d).ToList();
                    return new { day = d, responses = day.Count, csat = Csat(day, r => Satisfied(r.Score, r.MaxScore)) };
                }),
                bySender = rows.GroupBy(r => r.SenderEmail).Select(g => new
                {
                    senderName = g.First().SenderName, senderEmail = g.Key, responses = g.Count(),
                    average = Math.Round(g.Average(r => (double)r.Score), 2), csat = Csat(g.ToList(), r => Satisfied(r.Score, r.MaxScore)),
                }).OrderByDescending(x => x.responses).Take(25),
            };
        }).RequireRoles(analysts);

        app.MapGet("/api/analytics/responses", async (Guid? surveyId, bool? negativeOnly, int? skip, int? take, AppDbContext db) =>
        {
            var q = db.SurveyResponses.AsNoTracking();
            if (surveyId != null) q = q.Where(r => r.SurveyId == surveyId);
            if (negativeOnly == true) q = q.Where(r => r.Score * 5 <= r.MaxScore * 2);
            var total = await q.CountAsync();
            var items = await q.OrderByDescending(r => r.CreatedAt).Skip(skip ?? 0).Take(Math.Clamp(take ?? 25, 1, 100)).ToListAsync();
            return new { total, items };
        }).RequireRoles(analysts);

        app.MapGet("/api/analytics/clicks", async (int? days, AppDbContext db) =>
        {
            var span = Math.Clamp(days ?? 30, 1, 365);
            var from = DateTime.UtcNow.Date.AddDays(-(span - 1));
            var rows = await db.LinkClicks.AsNoTracking().Where(c => c.CreatedAt >= from).Select(c => new { c.Url, c.Campaign, c.CreatedAt }).ToListAsync();
            return new
            {
                days = span,
                total = rows.Count,
                byDay = Days(from, span).Select(d => new { day = d, clicks = rows.Count(r => r.CreatedAt.Date == d) }),
                byUrl = rows.GroupBy(r => r.Url).Select(g => new { url = g.Key, clicks = g.Count() }).OrderByDescending(x => x.clicks).Take(20),
                byCampaign = rows.GroupBy(r => r.Campaign ?? "(none)").Select(g => new { campaign = g.Key, clicks = g.Count() }).OrderByDescending(x => x.clicks).Take(20),
            };
        }).RequireRoles(analysts);

        app.MapGet("/api/audit", async (string? search, int? skip, int? take, AppDbContext db) =>
        {
            var q = db.AuditLogs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                q = q.Where(a => a.Action.ToLower().Contains(s) || a.ActorName.ToLower().Contains(s) || (a.Summary != null && a.Summary.ToLower().Contains(s)));
            }
            var total = await q.CountAsync();
            var items = await q.OrderByDescending(a => a.At).Skip(skip ?? 0).Take(Math.Clamp(take ?? 50, 1, 200)).ToListAsync();
            return new { total, items };
        }).RequireRoles(TenantRole.AnalyticsViewer);

        // ----- auto-replies (rule admins for anyone; managers for their direct reports) -----
        app.MapGet("/api/auto-replies", async (AppDbContext db, CurrentUser me) =>
        {
            var q = db.AutoReplyPolicies.AsNoTracking().Where(p => !p.Deleted);
            if (!me.Has(TenantRole.RuleAdmin)) q = q.Where(p => p.CreatedById == me.UserId);
            var policies = await q.OrderByDescending(p => p.StartUtc).ToListAsync();
            var ids = policies.Select(p => p.Id).ToList();
            var states = await db.AutoReplyStates.AsNoTracking().Where(s => ids.Contains(s.PolicyId)).ToListAsync();
            return policies.Select(p => new
            {
                policy = p,
                applied = states.Count(s => s.PolicyId == p.Id && s.AppliedAt != null && s.RevertedAt == null && s.Error == null),
                errors = states.Where(s => s.PolicyId == p.Id && s.Error != null).Select(s => new { s.UserId, s.Error }),
            });
        }).RequireTenantUser();

        app.MapGet("/api/auto-replies/targets", async (AppDbContext db, CurrentUser me) =>
        {
            // Who the caller may configure auto-replies for.
            var admin = me.Has(TenantRole.RuleAdmin);
            var users = await db.Users.AsNoTracking().Where(u => admin || u.ManagerId == me.UserId).OrderBy(u => u.DisplayName)
                .Select(u => new { u.Id, u.DisplayName, u.Department }).Take(2000).ToListAsync();
            var groups = admin
                ? (await db.Groups.AsNoTracking().OrderBy(g => g.DisplayName).ToListAsync()).Select(g => new { g.Id, g.DisplayName }).ToList()
                : new();
            return new { admin, users, groups };
        }).RequireTenantUser();

        app.MapPost("/api/auto-replies", async (AutoReplyInput req, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            if (await ValidateAutoReply(req, db, me) is { } problem) return problem;
            var p = new AutoReplyPolicy { CreatedById = me.UserId, CreatedByName = me.Name, ManagerDelegated = !me.Has(TenantRole.RuleAdmin) };
            Apply(p, req);
            db.AutoReplyPolicies.Add(p);
            db.Log(http, "autoreply.created", "AutoReplyPolicy", p.Id, p.Name);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        }).RequireTenantUser();

        app.MapPut("/api/auto-replies/{id:guid}", async (Guid id, AutoReplyInput req, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            if (await ValidateAutoReply(req, db, me) is { } problem) return problem;
            var p = await db.AutoReplyPolicies.FirstOrDefaultAsync(x => x.Id == id && !x.Deleted);
            if (p == null || (!me.Has(TenantRole.RuleAdmin) && p.CreatedById != me.UserId)) return Results.NotFound();
            Apply(p, req);
            db.Log(http, "autoreply.updated", "AutoReplyPolicy", p.Id, p.Name);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        }).RequireTenantUser();

        app.MapDelete("/api/auto-replies/{id:guid}", async (Guid id, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            var p = await db.AutoReplyPolicies.FirstOrDefaultAsync(x => x.Id == id && !x.Deleted);
            if (p == null || (!me.Has(TenantRole.RuleAdmin) && p.CreatedById != me.UserId)) return Results.NotFound();
            p.Deleted = true;
            p.Dirty = true;
            db.Log(http, "autoreply.deleted", "AutoReplyPolicy", p.Id, p.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireTenantUser();

        // ----- Outlook add-in -----
        app.MapGet("/api/addin/options", async (AppDbContext db, CurrentUser me, RuntimeProvider runtime) =>
        {
            var rt = await runtime.GetAsync(me.Tenant!.Id);
            var sender = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me.UserId);
            var enabled = rt != null && sender != null && rt.Tenant.Mode != DeploymentMode.ServerSide;
            return new
            {
                enabled,
                mode = rt?.Tenant.Mode,
                alternatives = !enabled
                    ? Enumerable.Empty<object>()
                    : rt!.Rules.Where(r => r.Rule.UserSelectable && (r.Rule.Channels & RuleChannels.Client) != 0 &&
                                           RuleEngine.SenderMatches(r.Rule.Conditions.Senders, sender!))
                        .Select(r => (object)new { r.Rule.Id, r.Rule.Name }),
            };
        }).RequireTenantUser();

        app.MapPost("/api/addin/signature", async (AddinRequest req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, SignatureService sig) =>
        {
            var rt = await runtime.GetAsync(me.Tenant!.Id);
            var sender = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me.UserId);
            if (rt == null || sender == null || rt.Tenant.Mode == DeploymentMode.ServerSide)
                return Results.Ok(new { enabled = false, html = "", ruleIds = Array.Empty<Guid>(), headerName = MimeStamper.ClientHeader, headerValue = "" });

            var compose = new ComposeRequest
            {
                Runtime = rt, Sender = sender, Channel = RuleChannels.Client, ComposeKey = req.ComposeKey is { Length: > 0 and <= 100 } ? req.ComposeKey : null,
                Facts = new MessageFacts
                {
                    SenderAddress = sender.PrimaryEmail, Recipients = (req.Recipients ?? new()).Take(500).ToList(),
                    Kind = req.MessageType, Subject = req.Subject ?? "",
                },
            };

            List<SignaturePart> parts;
            var manual = false;
            if (req.RuleId is { } ruleId)
            {
                var rr = rt.Rules.FirstOrDefault(r => r.Rule.Id == ruleId && r.Rule.UserSelectable && (r.Rule.Channels & RuleChannels.Client) != 0);
                if (rr == null || !RuleEngine.SenderMatches(rr.Rule.Conditions.Senders, sender)) return Results.BadRequest(new { error = "signature_not_available" });
                parts = new() { await sig.RenderRuleAsync(rr.Rule, rr.Template, rr.ImageMode, compose) };
                manual = true;
            }
            else parts = (await sig.ComposeAsync(compose)).Parts;

            var ids = parts.Select(p => p.RuleId).ToList();
            return Results.Ok(new
            {
                enabled = true,
                html = string.Concat(parts.Select(p => $"<div class=\"s2e-signature\">{p.Html}</div>")),
                ruleIds = ids,
                headerName = MimeStamper.ClientHeader,
                headerValue = (manual ? "manual; " : "") + "rules=" + string.Join(",", ids.Select(i => i.ToString("N"))),
            });
        }).RequireTenantUser();
    }

    private static bool Satisfied(int score, int max) => score * 5 >= max * 4;
    private static bool Negative(int score, int max) => score * 5 <= max * 2;
    private static IEnumerable<DateTime> Days(DateTime from, int count) => Enumerable.Range(0, count).Select(i => from.AddDays(i));

    private static IResult? ValidateSurvey(SurveyInput req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required", message = "Give the survey a name." });
        if (req.Style == SurveyStyle.Custom && (req.CustomIcons == null || req.CustomIcons.Count is < 2 or > 10 || req.CustomIcons.Any(i => !IsHttps(i.ImageUrl) && !i.ImageUrl.StartsWith("http://localhost"))))
            return Results.BadRequest(new { error = "invalid_icons", message = "Custom surveys need 2–10 icons with image URLs." });
        if (!string.IsNullOrWhiteSpace(req.WebhookUrl) && !IsHttps(req.WebhookUrl))
            return Results.BadRequest(new { error = "invalid_webhook", message = "The webhook must be an https:// URL." });
        return null;
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static void Apply(Survey s, SurveyInput req)
    {
        s.Name = req.Name.Trim();
        s.Question = req.Question;
        s.Style = req.Style;
        s.CustomIcons = req.Style == SurveyStyle.Custom ? req.CustomIcons! : new();
        s.ThankYouMessage = req.ThankYouMessage;
        s.AskComment = req.AskComment;
        s.NegativeThreshold = Math.Clamp(req.NegativeThreshold, 0, s.MaxScore);
        s.AlertEmails = (req.AlertEmails ?? new()).Select(e => e.Trim()).Where(e => e.Contains('@')).Distinct().ToList();
        s.WebhookUrl = string.IsNullOrWhiteSpace(req.WebhookUrl) ? null : req.WebhookUrl.Trim();
        s.Enabled = req.Enabled;
    }

    private static async Task<IResult?> ValidateAutoReply(AutoReplyInput req, AppDbContext db, CurrentUser me)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required", message = "Give the auto-reply a name." });
        if (req.EndUtc <= req.StartUtc) return Results.BadRequest(new { error = "invalid_schedule", message = "The end must be after the start." });
        var users = req.UserIds ?? new();
        var groups = req.GroupIds ?? new();
        if (users.Count + groups.Count == 0) return Results.BadRequest(new { error = "no_targets", message = "Choose at least one mailbox or group." });
        if (!string.IsNullOrWhiteSpace(req.ForwardTo) && !req.ForwardTo.Contains('@'))
            return Results.BadRequest(new { error = "invalid_forward", message = "Forward-to must be an email address." });
        if (!me.Has(TenantRole.RuleAdmin))
        {
            // Manager delegation: only direct reports, never groups.
            var reports = await db.Users.Where(u => u.ManagerId == me.UserId).Select(u => u.Id).ToListAsync();
            if (groups.Count > 0 || users.Any(u => !reports.Contains(u)))
                return Results.Json(new { error = "forbidden", message = "You can only set auto-replies for your direct reports." }, statusCode: 403);
        }
        return null;
    }

    private static void Apply(AutoReplyPolicy p, AutoReplyInput req)
    {
        p.Name = req.Name.Trim();
        p.Enabled = req.Enabled;
        p.UserIds = req.UserIds ?? new();
        p.GroupIds = req.GroupIds ?? new();
        p.StartUtc = req.StartUtc.ToUniversalTime();
        p.EndUtc = req.EndUtc.ToUniversalTime();
        p.InternalMessage = req.InternalMessage ?? "";
        p.ExternalMessage = req.ExternalMessage ?? "";
        p.ExternalAudience = req.ExternalAudience;
        p.ForwardTo = string.IsNullOrWhiteSpace(req.ForwardTo) ? null : req.ForwardTo.Trim();
        p.Dirty = true;
        p.UpdatedAt = DateTime.UtcNow;
    }
}
