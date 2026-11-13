using System.Text;
using MimeKit;
using Sig2Email.Core.Mail;
using Sig2Email.Core.Models;
using Sig2Email.Core.Rendering;
using Sig2Email.Core.Rules;
using Sig2Email.Core.Security;

namespace Sig2Email.Tests;

public class TemplateTests
{
    private static string Render(string template, params (string, string?)[] values) =>
        CompiledTemplate.Parse(template).Render(new RenderContext
        {
            Values = values.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.OrdinalIgnoreCase),
            SurveyHtml = id => $"[survey {id:N}]",
        });

    [Fact]
    public void Replaces_known_placeholders_and_html_encodes_values() =>
        Assert.Equal("<b>Ann &amp; Co</b> / Sales", Render("<b>{DisplayName}</b> / {department}", ("DisplayName", "Ann & Co"), ("Department", "Sales")));

    [Fact]
    public void Leaves_css_and_unknown_braces_untouched() =>
        Assert.Equal("<style>a{color:red} .x{}</style>{NotAPlaceholder}", Render("<style>a{color:red} .x{}</style>{NotAPlaceholder}"));

    [Fact]
    public void Empty_value_renders_nothing() => Assert.Equal("T: ", Render("T: {Phone}", ("Phone", null)));

    [Theory]
    [InlineData("{if MobilePhone}M: {MobilePhone}{/if}", "")]
    [InlineData("{if !MobilePhone}no mobile{/if}", "no mobile")]
    [InlineData("{if MobilePhone}a{else}b{/if}", "b")]
    [InlineData("{if Department == \"Sales\"}S{else}O{/if}", "S")]
    [InlineData("{if Department != 'sales'}S{else}O{/if}", "O")]
    [InlineData("{if Department contains \"ale\" and Phone}yes{/if}", "yes")]
    [InlineData("{if MobilePhone or Phone}either{/if}", "either")]
    [InlineData("{if Phone}{if MobilePhone}both{else}phone only{/if}{/if}", "phone only")]
    public void Conditionals(string template, string expected) =>
        Assert.Equal(expected, Render(template, ("Department", "Sales"), ("Phone", "123"), ("MobilePhone", "  ")));

    [Fact]
    public void Malformed_blocks_degrade_to_literal_text()
    {
        Assert.Equal("{/if}x{else}", Render("{/if}x{else}"));
        Assert.Equal("{if a ==}x", Render("{if a ==}x"));
    }

    [Fact]
    public void Survey_token_is_expanded_and_reported()
    {
        var id = Guid.NewGuid();
        var t = CompiledTemplate.Parse($"sig {{Csat:{id}}}");
        Assert.Contains(id, t.SurveyIds);
        Assert.Equal($"sig [survey {id:N}]", t.Render(new RenderContext { Values = new Dictionary<string, string?>(), SurveyHtml = g => $"[survey {g:N}]" }));
    }

    [Fact]
    public void Plain_text_fallback_keeps_structure_and_link_targets()
    {
        var text = HtmlConverter.ToPlainText(
            "<style>p{}</style><table><tr><td><b>Ann Lee</b><br>Sales</td></tr><tr><td><a href=\"mailto:ann@x.com\">ann@x.com</a> " +
            "<a href=\"https://x.com/a\">Website</a></td></tr></table><p>Bye&nbsp;now</p>");
        Assert.Equal("Ann Lee\nSales\n\nann@x.com Website <https://x.com/a>\n\nBye now", text);
    }

    [Fact]
    public void Rtf_fallback_escapes_and_formats()
    {
        var rtf = HtmlConverter.ToRtf("<p><b>Jürgen</b> {x}</p><a href=\"https://x.com\">site</a>");
        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.Contains(@"{\b J\u252?rgen}", rtf);
        Assert.Contains(@"\{x\}", rtf);
        Assert.Contains("HYPERLINK \"https://x.com\"", rtf);
    }
}

public class RuleEngineTests
{
    private static readonly HashSet<string> Domains = new() { "contoso.example" };

    private static DirectoryUser User(string id = "u1", string dept = "Sales", params string[] groups) =>
        new() { Id = id, Upn = id + "@contoso.example", Department = dept, GroupIds = groups.ToList(), CustomAttributes = { ["3"] = "VIP" } };

    private static MessageFacts Msg(string[]? to = null, MessageKind kind = MessageKind.New, string subject = "Hello", string? body = "text", DateTime? now = null) =>
        new()
        {
            SenderAddress = "u1@contoso.example", Recipients = to ?? new[] { "bob@fabrikam.example" }, Kind = kind, Subject = subject,
            BodyText = body == null ? null : () => body, NowUtc = now ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    private static Rule Rule(Action<RuleConditions>? configure = null, OnMatchAction onMatch = OnMatchAction.Stop, RuleChannels channels = RuleChannels.Both)
    {
        var r = new Rule { Name = "r", OnMatch = onMatch, Channels = channels };
        configure?.Invoke(r.Conditions);
        return r;
    }

    private static bool Matches(Rule r, MessageFacts? m = null, DirectoryUser? u = null, RuleChannels channel = RuleChannels.Server) =>
        RuleEngine.Evaluate(new[] { r }, m ?? Msg(), u ?? User(), Domains, channel).Count == 1;

    [Fact]
    public void Sender_conditions()
    {
        Assert.True(Matches(Rule()));
        Assert.False(Matches(Rule(c => c.Senders.Everyone = false)));
        Assert.True(Matches(Rule(c => { c.Senders.Everyone = false; c.Senders.UserIds.Add("u1"); })));
        Assert.True(Matches(Rule(c => { c.Senders.Everyone = false; c.Senders.GroupIds.Add("g1"); }), u: User(groups: "g1")));
        Assert.False(Matches(Rule(c => { c.Senders.Everyone = false; c.Senders.GroupIds.Add("g2"); }), u: User(groups: "g1")));
        Assert.False(Matches(Rule(c => c.Senders.ExcludeGroupIds.Add("g1")), u: User(groups: "g1")));
        Assert.False(Matches(Rule(c => c.Senders.ExcludeUserIds.Add("u1"))));
    }

    [Fact]
    public void Dynamic_attribute_filters()
    {
        Rule F(FilterMatch match, params AttributeFilter[] filters) => Rule(c =>
        {
            c.Senders.Everyone = false;
            c.Senders.FilterMatch = match;
            c.Senders.Filters.AddRange(filters);
        });
        var sales = new AttributeFilter { Attribute = "Department", Op = FilterOp.Equals, Value = "sales" };
        var vip = new AttributeFilter { Attribute = "CustomAttribute3", Op = FilterOp.StartsWith, Value = "VI" };
        var noCity = new AttributeFilter { Attribute = "City", Op = FilterOp.IsNotEmpty };
        Assert.True(Matches(F(FilterMatch.All, sales, vip)));
        Assert.False(Matches(F(FilterMatch.All, sales, noCity)));
        Assert.True(Matches(F(FilterMatch.Any, noCity, sales)));
        Assert.False(Matches(F(FilterMatch.All, sales), u: User(dept: "Finance")));
    }

    [Fact]
    public void Recipient_scope_domains_and_exclusions()
    {
        var internalOnly = new[] { "a@contoso.example" };
        var mixed = new[] { "a@contoso.example", "b@fabrikam.example" };
        var external = new[] { "b@fabrikam.example" };
        Rule Scope(RecipientScope s) => Rule(c => c.Recipients.Scope = s);

        Assert.True(Matches(Scope(RecipientScope.InternalOnly), Msg(internalOnly)));
        Assert.False(Matches(Scope(RecipientScope.InternalOnly), Msg(mixed)));
        Assert.True(Matches(Scope(RecipientScope.ExternalAny), Msg(mixed)));
        Assert.False(Matches(Scope(RecipientScope.ExternalAny), Msg(internalOnly)));
        Assert.True(Matches(Scope(RecipientScope.ExternalOnly), Msg(external)));
        Assert.False(Matches(Scope(RecipientScope.ExternalOnly), Msg(mixed)));
        Assert.True(Matches(Scope(RecipientScope.InternalOnly), Msg(Array.Empty<string>())));

        Assert.True(Matches(Rule(c => c.Recipients.Domains.Add("fabrikam.example")), Msg(mixed)));
        Assert.True(Matches(Rule(c => c.Recipients.Domains.Add("fabrikam.example")), Msg(new[] { "x@mail.fabrikam.example" })));
        Assert.False(Matches(Rule(c => c.Recipients.Domains.Add("fabrikam.example")), Msg(new[] { "x@notfabrikam.example" })));
        Assert.False(Matches(Rule(c => c.Recipients.Exclude.Add("B@Fabrikam.example")), Msg(mixed)));
        Assert.False(Matches(Rule(c => c.Recipients.Exclude.Add("@fabrikam.example")), Msg(mixed)));
    }

    [Fact]
    public void Message_type_keywords_and_schedule()
    {
        Assert.False(Matches(Rule(c => c.MessageTypes.Add(MessageKind.Reply))));
        Assert.True(Matches(Rule(c => c.MessageTypes.Add(MessageKind.Reply)), Msg(kind: MessageKind.Reply)));
        Assert.True(Matches(Rule(c => c.SubjectKeywords.Add("hell"))));
        Assert.False(Matches(Rule(c => c.SubjectKeywords.Add("invoice"))));
        Assert.True(Matches(Rule(c => c.BodyKeywords.Add("TEXT"))));
        Assert.False(Matches(Rule(c => c.SuppressKeywords.Add("#nosig")), Msg(body: "please #NoSig")));
        Assert.False(Matches(Rule(c => c.SuppressKeywords.Add("#nosig")), Msg(subject: "#nosig hi")));

        var june = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(Matches(Rule(c => { c.StartUtc = june.AddDays(-1); c.EndUtc = june.AddDays(1); })));
        Assert.False(Matches(Rule(c => c.StartUtc = june.AddDays(1))));
        Assert.False(Matches(Rule(c => c.EndUtc = june.AddDays(-1))));
    }

    [Fact]
    public void Client_channel_defers_body_dependent_rules_to_the_server()
    {
        var bodyRule = Rule(c => c.BodyKeywords.Add("text"));
        Assert.False(Matches(bodyRule, Msg(body: null), channel: RuleChannels.Client));
        Assert.True(Matches(Rule(c => c.SuppressKeywords.Add("#nosig")), Msg(body: null), channel: RuleChannels.Client));
        Assert.False(Matches(Rule(channels: RuleChannels.Server), channel: RuleChannels.Client));
    }

    [Fact]
    public void Pipeline_honours_stop_continue_and_client_applied_rules()
    {
        var disclaimer = Rule(onMatch: OnMatchAction.Continue);
        var noMatch = Rule(c => c.Senders.Everyone = false);
        var signature = Rule();
        var never = Rule();
        var rules = new[] { disclaimer, noMatch, signature, never };

        var result = RuleEngine.Evaluate(rules, Msg(), User(), Domains, RuleChannels.Server);
        Assert.Equal(new[] { disclaimer.Id, signature.Id }, result.Select(m => m.Rule.Id));

        // Combo mode: the add-in already inserted "signature"; it still stops the pipeline but is not stamped again.
        result = RuleEngine.Evaluate(rules, Msg(), User(), Domains, RuleChannels.Server, new HashSet<Guid> { signature.Id });
        Assert.Equal(new[] { false, true }, result.Select(m => m.AlreadyApplied));
        Assert.DoesNotContain(result, m => m.Rule.Id == never.Id);

        disclaimer.Enabled = false;
        Assert.Single(RuleEngine.Evaluate(rules, Msg(), User(), Domains, RuleChannels.Server));
    }
}

public class MimeStamperTests
{
    private static SignaturePart Sig(string html = "<b>SIG</b>", SignaturePlacement placement = SignaturePlacement.AboveQuotedText, params InlineImage[] images) =>
        new(Guid.NewGuid(), html, "SIG", placement, images);

    private static MimeMessage Roundtrip(MimeMessage m)
    {
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        ms.Position = 0;
        return MimeMessage.Load(ms);
    }

    private static MimeMessage Message(string? html, string? text, string subject = "Hi")
    {
        var m = new MimeMessage { Subject = subject };
        m.From.Add(MailboxAddress.Parse("ann@contoso.example"));
        m.To.Add(MailboxAddress.Parse("bob@fabrikam.example"));
        var builder = new BodyBuilder { HtmlBody = html, TextBody = text };
        builder.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes("attachment body"));
        m.Body = builder.ToMessageBody();
        return Roundtrip(m);
    }

    [Fact]
    public void Appends_to_html_and_text_alternatives_and_keeps_attachments()
    {
        var m = Message("<html><body><p>Hello</p></body></html>", "Hello");
        Assert.True(MimeStamper.Apply(m, new[] { Sig("<b>Grüße €</b>") }));
        m = Roundtrip(m);
        Assert.Matches(@"<p>Hello</p><div class=""s2e-signature""><br><b>Grüße €</b></div></body>", m.HtmlBody);
        Assert.Equal("Hello\r\n\r\nSIG\r\n", m.TextBody);
        Assert.Single(m.Attachments);
        Assert.Equal("attachment body", ((TextPart)m.Attachments.Single()).Text);
    }

    [Theory]
    [InlineData("<div id=\"divRplyFwdMsg\" dir=\"ltr\">From: x</div>")]
    [InlineData("<div style='border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0cm 0cm 0cm'><p>From: x</p></div>")]
    [InlineData("<div class=\"gmail_quote\">On Monday x wrote</div>")]
    [InlineData("<blockquote type=\"cite\">old</blockquote>")]
    [InlineData("<div id=\"mail-editor-reference-message-container\">old</div>")]
    public void Reply_signature_goes_above_the_quoted_message_and_disclaimer_at_the_bottom(string quote)
    {
        var html = MimeStamper.InsertHtml($"<html><body><p>My reply</p>{quote}</body></html>", "[SIG]", "[DISCLAIMER]");
        Assert.Equal($"<html><body><p>My reply</p>[SIG]{quote}[DISCLAIMER]</body></html>", html);
    }

    [Fact]
    public void Without_a_quote_everything_goes_to_the_bottom_in_rule_order() =>
        Assert.Equal("<p>x</p>[SIG][DISCLAIMER]", MimeStamper.InsertHtml("<p>x</p>", "[SIG]", "[DISCLAIMER]"));

    [Fact]
    public void Plain_text_reply_placement()
    {
        var body = "My reply\r\n\r\n-----Original Message-----\r\nFrom: Bob\r\nold text\r\n";
        Assert.Equal("My reply\r\n\r\nSIG\r\n\r\n-----Original Message-----\r\nFrom: Bob\r\nold text\r\n\r\nDISC\r\n",
            MimeStamper.InsertText(body, "\r\nSIG\r\n", "\r\nDISC\r\n"));
    }

    [Fact]
    public void Inline_images_become_related_parts()
    {
        var m = Message("<html><body>Hi</body></html>", "Hi");
        var image = new InlineImage("logo@sig2email", new byte[] { 1, 2, 3 }, "image/png", "logo.png");
        MimeStamper.Apply(m, new[] { Sig("<img src=\"cid:logo@sig2email\">", SignaturePlacement.Bottom, image) });
        m = Roundtrip(m);
        var related = m.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "logo@sig2email");
        Assert.Equal("image/png", related.ContentType.MimeType);
        Assert.Contains("cid:logo@sig2email", m.HtmlBody);
        Assert.Equal("Hi\r\n\r\nSIG\r\n", m.TextBody);
        Assert.Single(m.Attachments); // the file attachment is still a regular attachment
    }

    [Fact]
    public void Html_only_and_text_only_messages()
    {
        var htmlOnly = new MimeMessage { Body = new TextPart("html") { Text = "<p>x</p>" } };
        MimeStamper.Apply(htmlOnly, new[] { Sig() });
        Assert.Contains("<b>SIG</b>", Roundtrip(htmlOnly).HtmlBody);

        var textOnly = new MimeMessage { Body = new TextPart("plain") { Text = "x" } };
        MimeStamper.Apply(textOnly, new[] { Sig() });
        Assert.Equal("x\r\n\r\nSIG\r\n", Roundtrip(textOnly).TextBody);
    }

    [Fact]
    public void Signed_encrypted_and_auto_generated_messages_are_not_stampable()
    {
        Assert.True(MimeStamper.IsStampable(Message("<p>x</p>", "x")));

        var signed = new MimeMessage { Body = new MimeKit.Cryptography.MultipartSigned { new TextPart("plain") { Text = "x" } } };
        Assert.False(MimeStamper.IsStampable(signed));

        var smime = new MimeMessage { Body = new MimePart("application", "pkcs7-mime") { Content = new MimeContent(new MemoryStream(new byte[] { 1 })) } };
        Assert.False(MimeStamper.IsStampable(smime));

        var auto = Message("<p>x</p>", "x");
        auto.Headers.Add("Auto-Submitted", "auto-replied");
        Assert.False(MimeStamper.IsStampable(auto));
    }

    [Theory]
    [InlineData("Quarterly numbers", null, MessageKind.New)]
    [InlineData("RE: Quarterly numbers", null, MessageKind.Reply)]
    [InlineData("AW: Zahlen", null, MessageKind.Reply)]
    [InlineData("Fwd: Quarterly numbers", "<a@b>", MessageKind.Forward)]
    [InlineData("Quarterly numbers", "<a@b>", MessageKind.Reply)]
    public void Detects_message_kind(string subject, string? inReplyTo, MessageKind expected)
    {
        var m = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "x" } };
        if (inReplyTo != null) m.InReplyTo = inReplyTo;
        Assert.Equal(expected, MimeStamper.DetectKind(m));
    }

    [Fact]
    public void Keyword_text_excludes_quoted_history()
    {
        var m = new MimeMessage { Body = new TextPart("html") { Text = "<p>new text</p><div class=\"gmail_quote\">#nosig old</div>" } };
        Assert.Equal("new text", MimeStamper.GetNewBodyText(m));
    }

    [Fact]
    public void Client_marker_parsing()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var marker = ClientMarker.Parse($"manual; rules={a:N}, {b}");
        Assert.True(marker.Manual);
        Assert.Equal(new[] { a, b }.ToHashSet(), marker.RuleIds);
        Assert.Empty(ClientMarker.Parse(null).RuleIds);
        Assert.False(ClientMarker.Parse("rules=garbage").Manual);
    }
}

public class SecurityTests
{
    private static readonly TokenSigner Signer = new(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));

    [Fact]
    public void Signed_tokens_round_trip_and_reject_tampering()
    {
        var token = Signer.Pack("{\"u\":\"https://x.example\"}");
        Assert.Equal("{\"u\":\"https://x.example\"}", Signer.Unpack(token));
        Assert.Null(Signer.Unpack(token[..^1] + (token[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(Signer.Unpack("AAAA" + token[4..]));
        Assert.Null(Signer.Unpack("not-a-token"));
        Assert.Null(new TokenSigner(Encoding.UTF8.GetBytes("another-key-another-key-another-k")).Unpack(token));
        Assert.Throws<ArgumentException>(() => new TokenSigner(new byte[8]));
    }

    [Fact]
    public void Link_rewriter_adds_utm_and_tracks_only_external_http_links()
    {
        var rule = new Rule { TrackLinks = true, UtmSource = "sig", UtmCampaign = "spring sale" };
        var tenant = Guid.NewGuid();
        var html = "<a href=\"https://shop.example/p?id=1&amp;x=2#top\">Shop</a> <a href='mailto:a@b.c'>Mail</a> " +
                   "<a href=\"https://app.example/r/abc/5?k=x\">Rate</a>";
        var result = LinkRewriter.Rewrite(html, rule, tenant, "u1", "https://app.example", Signer);

        Assert.Contains("href='mailto:a@b.c'", result);
        Assert.Contains("href=\"https://app.example/r/abc/5?k=x\"", result);
        var token = System.Text.RegularExpressions.Regex.Match(result, "https://app.example/l/([^\"]+)").Groups[1].Value;
        var link = System.Text.Json.JsonSerializer.Deserialize<TrackedLink>(Signer.Unpack(token)!)!;
        Assert.Equal("https://shop.example/p?id=1&x=2&utm_source=sig&utm_campaign=spring%20sale#top", link.U);
        Assert.Equal(tenant, link.T);

        var untracked = LinkRewriter.Rewrite(html, new Rule { UtmMedium = "email" }, tenant, "u1", "https://app.example", Signer);
        Assert.Contains("href=\"https://shop.example/p?id=1&amp;x=2&amp;utm_medium=email#top\"", untracked);
        Assert.Equal(html, LinkRewriter.Rewrite(html, new Rule(), tenant, "u1", "https://app.example", Signer));
    }

    [Fact]
    public void Vcard_and_qr()
    {
        var user = new DirectoryUser { DisplayName = "Ann Lee", FirstName = "Ann", LastName = "Lee; Jr", Upn = "ann@x.example", MobilePhone = "+1 555" };
        var card = VCard.Build(user);
        Assert.Contains("N:Lee\\; Jr;Ann;;;", card);
        Assert.Contains("TEL;TYPE=CELL:+1 555", card);
        var png = VCard.QrPng(user);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }
}
