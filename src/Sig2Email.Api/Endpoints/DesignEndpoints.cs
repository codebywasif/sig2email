using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;
using Sig2Email.Core.Rules;
using Sig2Email.Data;

namespace Sig2Email.Api.Endpoints;

public static class DesignEndpoints
{
    public record TemplateInput(string Name, string? Description, string Html, string? DesignJson, ImageMode ImageMode);
    public record RenderRequest(string Html, string? UserId);
    public record RuleInput(string Name, bool Enabled, Guid TemplateId, SignaturePlacement Placement, RuleChannels Channels, OnMatchAction OnMatch,
        bool UserSelectable, bool TrackLinks, string? UtmSource, string? UtmMedium, string? UtmCampaign, RuleConditions Conditions);
    public record SimulateRequest(string SenderId, List<string>? Recipients, MessageKind MessageType, string? Subject, string? Body, RuleChannels Channel, DateTime? At);

    private const int MaxTemplateBytes = 512 * 1024;
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
    };

    public static void MapDesignEndpoints(this WebApplication app)
    {
        var writers = new[] { TenantRole.Designer };
        var publishers = new[] { TenantRole.RuleAdmin };

        // ----- templates -----
        app.MapGet("/api/templates", async (AppDbContext db) =>
        {
            var usage = (await db.Rules.Select(r => r.TemplateId).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var list = await db.Templates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
            return list.Select(t => new
            {
                t.Id, t.Name, t.Description, t.ImageMode, t.PublishedVersion, t.HasUnpublishedChanges, t.UpdatedAt, t.UpdatedBy,
                sourceMode = t.DesignJson == null, ruleCount = usage.GetValueOrDefault(t.Id),
            });
        }).RequireRoles(Auth.Readers);

        app.MapGet("/api/templates/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequireRoles(Auth.Readers);

        app.MapPost("/api/templates", async (TemplateInput req, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            if (Validate(req) is { } problem) return problem;
            var t = new Template
            {
                Name = req.Name.Trim(), Description = req.Description, Html = req.Html, DesignJson = req.DesignJson,
                ImageMode = req.ImageMode, UpdatedBy = me.Name,
            };
            db.Templates.Add(t);
            db.Log(http, "template.created", "Template", t.Id, t.Name);
            await db.SaveChangesAsync();
            return Results.Ok(t);
        }).RequireRoles(writers);

        app.MapPut("/api/templates/{id:guid}", async (Guid id, TemplateInput req, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            if (Validate(req) is { } problem) return problem;
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return Results.NotFound();
            t.Name = req.Name.Trim();
            t.Description = req.Description;
            t.Html = req.Html;
            t.DesignJson = req.DesignJson;
            t.ImageMode = req.ImageMode;
            t.HasUnpublishedChanges = true;
            t.UpdatedBy = me.Name;
            t.UpdatedAt = DateTime.UtcNow;
            db.Log(http, "template.updated", "Template", t.Id, t.Name);
            await db.SaveChangesAsync();
            return Results.Ok(t);
        }).RequireRoles(writers);

        app.MapDelete("/api/templates/{id:guid}", async (Guid id, AppDbContext db, HttpContext http) =>
        {
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return Results.NotFound();
            if (await db.Rules.AnyAsync(r => r.TemplateId == id))
                return Results.Conflict(new { error = "in_use", message = "This template is used by one or more rules." });
            db.Templates.Remove(t);
            await db.TemplateVersions.Where(v => v.TemplateId == id).ExecuteDeleteAsync();
            db.Log(http, "template.deleted", "Template", t.Id, t.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRoles(writers);

        // Publishing is what changes live mail flow, so it is reserved for rule administrators and owners.
        app.MapPost("/api/templates/{id:guid}/publish", async (Guid id, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return Results.NotFound();
            var version = (await db.TemplateVersions.Where(v => v.TemplateId == id).MaxAsync(v => (int?)v.Version) ?? 0) + 1;
            db.TemplateVersions.Add(new TemplateVersion
            {
                TemplateId = id, Version = version, Html = t.Html, DesignJson = t.DesignJson, ImageMode = t.ImageMode, PublishedBy = me.Name,
            });
            t.PublishedVersion = version;
            t.HasUnpublishedChanges = false;
            db.Log(http, "template.published", "Template", t.Id, $"{t.Name} v{version}");
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.Ok(new { version });
        }).RequireRoles(publishers);

        app.MapGet("/api/templates/{id:guid}/versions", async (Guid id, AppDbContext db) =>
            await db.TemplateVersions.AsNoTracking().Where(v => v.TemplateId == id).OrderByDescending(v => v.Version)
                .Select(v => new { v.Version, v.PublishedBy, v.PublishedAt }).ToListAsync())
            .RequireRoles(Auth.Readers);

        app.MapPost("/api/templates/{id:guid}/restore/{version:int}", async (Guid id, int version, AppDbContext db, CurrentUser me, HttpContext http) =>
        {
            var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id);
            var v = await db.TemplateVersions.AsNoTracking().FirstOrDefaultAsync(x => x.TemplateId == id && x.Version == version);
            if (t == null || v == null) return Results.NotFound();
            t.Html = v.Html;
            t.DesignJson = v.DesignJson;
            t.ImageMode = v.ImageMode;
            t.HasUnpublishedChanges = t.PublishedVersion != version;
            t.UpdatedBy = me.Name;
            t.UpdatedAt = DateTime.UtcNow;
            db.Log(http, "template.restored", "Template", t.Id, $"{t.Name} draft restored from v{version}");
            await db.SaveChangesAsync();
            return Results.Ok(t);
        }).RequireRoles(writers);

        app.MapPost("/api/templates/render", async (RenderRequest req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, SignatureService sig) =>
        {
            if (req.Html.Length > MaxTemplateBytes) return Results.BadRequest(new { error = "too_large" });
            var user = req.UserId == null
                ? await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).FirstOrDefaultAsync()
                : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId);
            if (user == null) return Results.BadRequest(new { error = "no_user", message = "No directory user available for preview." });
            var rt = await runtime.GetAsync(me.Tenant!.Id);
            var part = await sig.RenderRuleAsync(new Rule { Id = Guid.Empty }, CompiledTemplate.Parse(req.Html), ImageMode.Hosted, new ComposeRequest
            {
                Runtime = rt!, Sender = user, Channel = RuleChannels.Server, Preview = true,
                Facts = new MessageFacts { SenderAddress = user.PrimaryEmail, Recipients = Array.Empty<string>() },
            });
            return Results.Ok(new { html = part.Html, text = part.Text, rtf = HtmlConverter.ToRtf(part.Html), userId = user.Id });
        }).RequireRoles(Auth.Readers);

        // ----- assets -----
        app.MapGet("/api/assets", async (AppDbContext db, SignatureService sig) =>
            (await db.Assets.AsNoTracking().OrderByDescending(a => a.CreatedAt).ToListAsync())
                .Select(a => new { a.Id, a.FileName, a.ContentType, a.Size, a.CreatedAt, a.CreatedBy, url = sig.AssetUrl(a) }))
            .RequireRoles(Auth.Readers);

        app.MapPost("/api/assets", async (IFormFile file, AppDbContext db, CurrentUser me, IBlobStore blobs, SignatureService sig, HttpContext http) =>
        {
            if (file.Length is 0 or > 2 * 1024 * 1024) return Results.BadRequest(new { error = "invalid_size", message = "Images must be between 1 byte and 2 MB." });
            var ext = Path.GetExtension(file.FileName);
            if (!ImageTypes.TryGetValue(ext, out var contentType))
                return Results.BadRequest(new { error = "invalid_type", message = "Only PNG, JPEG and GIF images are supported in email." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (!LooksLikeImage(bytes, contentType)) return Results.BadRequest(new { error = "invalid_type", message = "The file content is not a valid image." });

            var name = new string(Path.GetFileNameWithoutExtension(file.FileName).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(60).ToArray());
            var asset = new Asset
            {
                TenantId = me.Tenant!.Id, FileName = (name.Length == 0 ? "image" : name) + ext.ToLowerInvariant(), ContentType = contentType,
                Size = bytes.Length, CreatedBy = me.Name,
            };
            asset.BlobKey = BlobKeys.Asset(asset.TenantId, asset.Id);
            await blobs.PutAsync(asset.BlobKey, bytes, contentType);
            db.Assets.Add(asset);
            db.Log(http, "asset.uploaded", "Asset", asset.Id, asset.FileName);
            await db.SaveChangesAsync();
            return Results.Ok(new { asset.Id, asset.FileName, asset.ContentType, asset.Size, asset.CreatedAt, asset.CreatedBy, url = sig.AssetUrl(asset) });
        }).RequireRoles(writers).DisableAntiforgery();

        app.MapDelete("/api/assets/{id:guid}", async (Guid id, AppDbContext db, IBlobStore blobs, HttpContext http) =>
        {
            var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == id);
            if (a == null) return Results.NotFound();
            db.Assets.Remove(a);
            db.Log(http, "asset.deleted", "Asset", a.Id, a.FileName);
            await db.SaveChangesAsync();
            await blobs.DeleteAsync(a.BlobKey);
            return Results.NoContent();
        }).RequireRoles(writers);

        // ----- rules -----
        var ruleAdmins = new[] { TenantRole.RuleAdmin };

        app.MapGet("/api/rules", (AppDbContext db) => db.Rules.AsNoTracking().OrderBy(r => r.Priority).ToListAsync()).RequireRoles(Auth.Readers);

        app.MapGet("/api/rules/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireRoles(Auth.Readers);

        app.MapPost("/api/rules", async (RuleInput req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            if (await ValidateRule(req, db) is { } problem) return problem;
            var rule = new Rule { Priority = (await db.Rules.MaxAsync(r => (int?)r.Priority) ?? 0) + 1 };
            Apply(rule, req);
            db.Rules.Add(rule);
            db.Log(http, "rule.created", "Rule", rule.Id, rule.Name);
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.Ok(rule);
        }).RequireRoles(ruleAdmins);

        app.MapPut("/api/rules/{id:guid}", async (Guid id, RuleInput req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            if (await ValidateRule(req, db) is { } problem) return problem;
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule == null) return Results.NotFound();
            Apply(rule, req);
            db.Log(http, "rule.updated", "Rule", rule.Id, $"{rule.Name} ({(rule.Enabled ? "enabled" : "disabled")})");
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.Ok(rule);
        }).RequireRoles(ruleAdmins);

        app.MapDelete("/api/rules/{id:guid}", async (Guid id, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule == null) return Results.NotFound();
            db.Rules.Remove(rule);
            db.Log(http, "rule.deleted", "Rule", rule.Id, rule.Name);
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.NoContent();
        }).RequireRoles(ruleAdmins);

        app.MapPut("/api/rules/order", async ([FromBody] List<Guid> ids, AppDbContext db, CurrentUser me, RuntimeProvider runtime, HttpContext http) =>
        {
            var rules = await db.Rules.ToListAsync();
            if (ids.Distinct().Count() != rules.Count || rules.Any(r => !ids.Contains(r.Id)))
                return Results.BadRequest(new { error = "invalid_order", message = "The list must contain every rule exactly once." });
            foreach (var r in rules) r.Priority = ids.IndexOf(r.Id) + 1;
            db.Log(http, "rule.reordered", "Rule", null, "Rule priority order changed");
            await db.SaveChangesAsync();
            await runtime.BumpAsync(me.Tenant!.Id);
            return Results.NoContent();
        }).RequireRoles(ruleAdmins);

        app.MapPost("/api/rules/simulate", async (SimulateRequest req, AppDbContext db, CurrentUser me, RuntimeProvider runtime, SignatureService sig) =>
        {
            var sender = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.SenderId);
            if (sender == null) return Results.BadRequest(new { error = "unknown_sender" });
            var rt = await runtime.GetAsync(me.Tenant!.Id);
            var body = req.Body ?? "";
            var result = await sig.ComposeAsync(new ComposeRequest
            {
                Runtime = rt!, Sender = sender, Channel = req.Channel == RuleChannels.Client ? RuleChannels.Client : RuleChannels.Server, Preview = true,
                Facts = new MessageFacts
                {
                    SenderAddress = sender.PrimaryEmail, Recipients = req.Recipients ?? new(), Kind = req.MessageType,
                    Subject = req.Subject ?? "", BodyText = req.Channel == RuleChannels.Client ? null : () => body, NowUtc = req.At ?? DateTime.UtcNow,
                },
            });
            var unpublished = await db.Rules.Where(r => r.Enabled).Join(db.Templates.Where(t => t.PublishedVersion == null), r => r.TemplateId, t => t.Id, (r, t) => r.Name).ToListAsync();
            return Results.Ok(new
            {
                matches = result.Matches.Select(m => new { m.Rule.Id, m.Rule.Name, m.Rule.OnMatch, m.Rule.Placement }),
                above = string.Concat(result.Parts.Where(p => p.Placement == SignaturePlacement.AboveQuotedText).Select(p => p.Html)),
                bottom = string.Concat(result.Parts.Where(p => p.Placement == SignaturePlacement.Bottom).Select(p => p.Html)),
                text = string.Join("\n\n", result.Parts.Select(p => p.Text)),
                skippedUnpublished = unpublished,
            });
        }).RequireRoles(Auth.Readers);
    }

    private static IResult? Validate(TemplateInput req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required", message = "Give the template a name." });
        if (req.Html.Length > MaxTemplateBytes || (req.DesignJson?.Length ?? 0) > MaxTemplateBytes)
            return Results.BadRequest(new { error = "too_large", message = "Templates are limited to 512 KB." });
        return null;
    }

    private static async Task<IResult?> ValidateRule(RuleInput req, AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required", message = "Give the rule a name." });
        if (!await db.Templates.AnyAsync(t => t.Id == req.TemplateId)) return Results.BadRequest(new { error = "unknown_template", message = "Choose a template." });
        if (req.Conditions.StartUtc > req.Conditions.EndUtc) return Results.BadRequest(new { error = "invalid_schedule", message = "The end date must be after the start date." });
        return null;
    }

    private static void Apply(Rule rule, RuleInput req)
    {
        rule.Name = req.Name.Trim();
        rule.Enabled = req.Enabled;
        rule.TemplateId = req.TemplateId;
        rule.Placement = req.Placement;
        rule.Channels = req.Channels == 0 ? RuleChannels.Both : req.Channels;
        rule.OnMatch = req.OnMatch;
        rule.UserSelectable = req.UserSelectable;
        rule.TrackLinks = req.TrackLinks;
        rule.UtmSource = req.UtmSource;
        rule.UtmMedium = req.UtmMedium;
        rule.UtmCampaign = req.UtmCampaign;
        rule.Conditions = req.Conditions ?? new RuleConditions();
        rule.UpdatedAt = DateTime.UtcNow;
    }

    private static bool LooksLikeImage(byte[] b, string contentType) => contentType switch
    {
        "image/png" => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
        "image/jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/gif" => b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46,
        _ => false,
    };
}
