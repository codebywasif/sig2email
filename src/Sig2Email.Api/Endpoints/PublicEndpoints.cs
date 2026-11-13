using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;
using Sig2Email.Core.Security;
using Sig2Email.Data;

namespace Sig2Email.Api.Endpoints;

/// <summary>Unauthenticated endpoints reached from inside emails. Everything is authorised by HMAC-signed links.</summary>
public static class PublicEndpoints
{
    public record RatingRequest(int Score, string? Comment, string? K);

    public static void MapPublicEndpoints(this WebApplication app)
    {
        var pub = app.MapGroup("").RequireRateLimiting("public");

        // ----- one-click CSAT -----
        pub.MapGet("/r/{dispatch}/{score:int}", async (string dispatch, int score, string? k, AppDbContext db, TokenSigner signer, HttpContext http) =>
        {
            if (!Guid.TryParseExact(dispatch, "N", out var id) || !signer.Verify(id.ToString("N"), k)) return Results.NotFound();
            db.SystemScope = true;
            Survey survey;
            if (id == Guid.Empty) survey = new Survey { Question = "How did I do today?" }; // designer preview
            else
            {
                var d = await db.SurveyDispatches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                var s = d == null ? null : await db.Surveys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.SurveyId && x.TenantId == d.TenantId);
                if (s == null) return Results.NotFound();
                survey = s;
            }
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            http.Response.Headers.ContentSecurityPolicy =
                $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; connect-src 'self'; img-src https: http: data:; base-uri 'none'; form-action 'none'";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(LandingPage.Render(survey, id, Math.Clamp(score, 1, survey.MaxScore), k!, nonce), "text/html; charset=utf-8");
        });

        pub.MapPost("/r/{dispatch}", async (string dispatch, RatingRequest req, AppDbContext db, TokenSigner signer, AlertService alerts) =>
        {
            if (!Guid.TryParseExact(dispatch, "N", out var id) || !signer.Verify(id.ToString("N"), req.K)) return Results.NotFound();
            if (id == Guid.Empty) return Results.Ok(new { preview = true });
            db.SystemScope = true;
            var d = await db.SurveyDispatches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            var survey = d == null ? null : await db.Surveys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.SurveyId && x.TenantId == d.TenantId);
            if (d == null || survey == null) return Results.NotFound();
            if (req.Score < 1 || req.Score > survey.MaxScore) return Results.BadRequest(new { error = "invalid_score" });
            var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
            if (comment?.Length > 2000) comment = comment[..2000];

            var r = await db.SurveyResponses.FirstOrDefaultAsync(x => x.DispatchId == id);
            if (r == null)
            {
                r = new SurveyResponse
                {
                    TenantId = d.TenantId, SurveyId = d.SurveyId, DispatchId = id, SenderId = d.SenderId, SenderEmail = d.SenderEmail,
                    SenderName = d.SenderName, Recipients = d.Recipients, Subject = d.Subject,
                };
                db.SurveyResponses.Add(r);
            }
            r.Score = req.Score;
            r.MaxScore = survey.MaxScore;
            if (comment != null || !survey.AskComment) r.Comment = comment;
            r.UpdatedAt = DateTime.UtcNow;

            var negative = req.Score <= survey.NegativeThreshold;
            // Alert once per response; a comment arriving later on an already-alerted rating does not re-alert.
            var alert = negative && !r.AlertSent;
            if (alert) r.AlertSent = true;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "retry" }); // two first clicks raced on the unique dispatch index
            }
            if (alert) alerts.Queue(survey, r);
            return Results.Ok(new { ok = true });
        });

        // ----- tracked links -----
        pub.MapGet("/l/{token}", async (string token, AppDbContext db, TokenSigner signer) =>
        {
            var payload = signer.Unpack(token);
            var link = payload == null ? null : JsonSerializer.Deserialize<TrackedLink>(payload);
            // Only ever redirect to the URL we signed ourselves – never an open redirect.
            if (link == null || !Uri.TryCreate(link.U, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
                return Results.NotFound();
            db.SystemScope = true;
            db.LinkClicks.Add(new LinkClick { TenantId = link.T, RuleId = link.R, SenderId = link.S, Url = StripUtm(link.U), Campaign = link.C });
            await db.SaveChangesAsync();
            return Results.Redirect(link.U);
        });

        // ----- hosted images (ids are unguessable; content is non-sensitive branding) -----
        pub.MapGet("/assets/{tenant}/{asset}/{name}", async (string tenant, string asset, IBlobStore blobs, HttpContext http) =>
            Guid.TryParseExact(tenant, "N", out var t) && Guid.TryParseExact(asset, "N", out var a)
                ? Image(await blobs.GetAsync(BlobKeys.Asset(t, a)), http)
                : Results.NotFound());

        pub.MapGet("/assets/photo/{tenant}/{user}", async (string tenant, string user, IBlobStore blobs, HttpContext http) =>
            Guid.TryParseExact(tenant, "N", out var t) ? Image(await blobs.GetAsync(BlobKeys.Photo(t, user)), http) : Results.NotFound());

        pub.MapGet("/assets/qr/{tenant}/{file}", async (string tenant, string file, string? k, AppDbContext db, SignatureService sig, HttpContext http) =>
        {
            var userId = file.EndsWith(".png") ? file[..^4] : file;
            if (!Guid.TryParseExact(tenant, "N", out var t) || !sig.VerifyQr(t, userId, k)) return Results.NotFound();
            db.SystemScope = true;
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == t && u.Id == userId);
            return user == null ? Results.NotFound() : Image(new BlobData(VCard.QrPng(user), "image/png"), http, 3600);
        });
    }

    private static IResult Image(BlobData? blob, HttpContext http, int maxAge = 86400)
    {
        if (blob == null) return Results.NotFound();
        http.Response.Headers.CacheControl = $"public, max-age={maxAge}";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(blob.Bytes, blob.ContentType);
    }

    private static string StripUtm(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var kept = url[(q + 1)..].Split('#')[0].Split('&').Where(p => !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)).ToArray();
        return url[..q] + (kept.Length > 0 ? "?" + string.Join("&", kept) : "");
    }
}

public static class LandingPage
{
    private static readonly string[] EmojiGlyphs = { "\U0001F621", "\U0001F641", "\U0001F610", "\U0001F642", "\U0001F60D" };

    public static string Render(Survey s, Guid dispatchId, int score, string k, string nonce)
    {
        static string E(string v) => WebUtility.HtmlEncode(v);
        var options = new System.Text.StringBuilder();
        for (var i = 1; i <= s.MaxScore; i++)
        {
            var (glyph, label) = s.Style switch
            {
                SurveyStyle.Stars => ("★", i == 1 ? "1 star" : $"{i} stars"),
                SurveyStyle.Thumbs => (i == 1 ? "\U0001F44E" : "\U0001F44D", i == 1 ? "Thumbs down" : "Thumbs up"),
                SurveyStyle.Custom => ($"<img src=\"{E(s.CustomIcons[Math.Min(i - 1, s.CustomIcons.Count - 1)].ImageUrl)}\" alt=\"\">", s.CustomIcons[Math.Min(i - 1, s.CustomIcons.Count - 1)].Label),
                _ => (EmojiGlyphs[i - 1], new[] { "Very unhappy", "Unhappy", "Neutral", "Happy", "Very happy" }[i - 1]),
            };
            options.Append($"<button type=\"button\" class=\"opt\" data-score=\"{i}\" aria-label=\"{E(label)}\" title=\"{E(label)}\" aria-pressed=\"false\">{glyph}</button>");
        }

        var config = JsonSerializer.Serialize(new { id = dispatchId.ToString("N"), score, k, stars = s.Style == SurveyStyle.Stars });
        return """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="robots" content="noindex"><title>Your feedback</title>
            <style>
              :root{color-scheme:light dark;--bg:#f5f5f5;--card:#fff;--fg:#242424;--muted:#616161;--line:#d1d1d1;--accent:#0f6cbd}
              @media (prefers-color-scheme:dark){:root{--bg:#1f1f1f;--card:#292929;--fg:#f5f5f5;--muted:#adadad;--line:#525252;--accent:#479ef5}}
              *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.5 "Segoe UI",system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;padding:16px}
              main{background:var(--card);border-radius:12px;padding:32px 28px;max-width:460px;width:100%;box-shadow:0 2px 12px rgba(0,0,0,.12);text-align:center}
              h1{font-size:22px;margin:0 0 4px}p{margin:0 0 16px;color:var(--muted)}
              .opts{display:flex;justify-content:center;gap:6px;margin:16px 0 20px;flex-wrap:wrap}
              .opt{font-size:34px;line-height:1;background:none;border:2px solid transparent;border-radius:10px;padding:8px;cursor:pointer;filter:grayscale(1);opacity:.55;color:#f5a623}
              .opt img{height:40px;display:block}.opt:hover,.opt:focus-visible{opacity:1;outline:none;border-color:var(--line)}
              .opt.on{filter:none;opacity:1;border-color:var(--accent)}
              textarea{width:100%;min-height:96px;border:1px solid var(--line);border-radius:8px;padding:10px;font:inherit;background:transparent;color:inherit;resize:vertical}
              .send{margin-top:12px;background:var(--accent);color:#fff;border:0;border-radius:8px;padding:10px 22px;font:inherit;font-weight:600;cursor:pointer}
              .send:disabled{opacity:.5;cursor:default}#status{min-height:24px;margin:12px 0 0;font-size:14px}label{display:block;text-align:left;font-size:14px;color:var(--muted);margin-bottom:6px}
            </style></head>
            <body><main>
              <h1>__THANKS__</h1>
              <p>__QUESTION__</p>
              <div class="opts" role="group" aria-label="Your rating">__OPTIONS__</div>
              __COMMENT__
              <p id="status" role="status" aria-live="polite"></p>
              <noscript><p>Please enable JavaScript to record your rating.</p></noscript>
            </main>
            <script nonce="__NONCE__">
            (function(){
              var c=__CONFIG__,status=document.getElementById('status'),opts=[].slice.call(document.querySelectorAll('.opt')),
                  comment=document.getElementById('comment'),send=document.getElementById('send');
              function paint(){opts.forEach(function(b){var n=+b.dataset.score,on=c.stars?n<=c.score:n===c.score;b.classList.toggle('on',on);b.setAttribute('aria-pressed',n===c.score)})}
              function save(withComment){
                status.textContent='Saving…';
                return fetch('/r/'+c.id,{method:'POST',headers:{'Content-Type':'application/json'},
                  body:JSON.stringify({score:c.score,comment:withComment&&comment?comment.value:null,k:c.k})})
                  .then(function(r){if(!r.ok)throw 0;status.textContent=withComment?'Comment sent. Thank you!':'Your rating has been recorded.'})
                  .catch(function(){status.textContent='Sorry, we could not save your rating. Please try again.'});
              }
              opts.forEach(function(b){b.addEventListener('click',function(){c.score=+b.dataset.score;paint();save(false)})});
              if(send)send.addEventListener('click',function(){send.disabled=true;save(true).then(function(){send.disabled=false})});
              paint();save(false);
            })();
            </script></body></html>
            """
            .Replace("__THANKS__", E(s.ThankYouMessage)).Replace("__QUESTION__", E(s.Question)).Replace("__OPTIONS__", options.ToString())
            .Replace("__COMMENT__", s.AskComment
                ? "<div id=\"cbox\"><label for=\"comment\">Anything you would like to add? (optional)</label><textarea id=\"comment\" maxlength=\"2000\"></textarea><button class=\"send\" id=\"send\" type=\"button\">Send comment</button></div>"
                : "")
            .Replace("__NONCE__", nonce).Replace("__CONFIG__", config);
    }
}
