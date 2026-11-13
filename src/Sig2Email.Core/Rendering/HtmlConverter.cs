using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Sig2Email.Core.Rendering;

/// <summary>Derives the plain-text and RTF fallbacks of an HTML signature.</summary>
public static partial class HtmlConverter
{
    private static readonly HashSet<string> Block = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "tr", "table", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "hr", "section",
    };

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "style", "script", "head", "title" };

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public static string ToPlainText(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var sb = new StringBuilder();
        if (doc.Body != null) WalkText(doc.Body, sb);
        var lines = sb.ToString().Replace("\r", "").Split('\n').Select(l => Spaces().Replace(l, " ").Trim());
        return BlankLines().Replace(string.Join("\n", lines), "\n\n").Trim('\n');
    }

    private static void WalkText(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                sb.Append(t.Data.Replace('\n', ' ').Replace('\r', ' '));
                continue;
            }
            if (child is not IElement el || Skip.Contains(el.LocalName)) continue;
            var name = el.LocalName;
            if (name == "br") { sb.Append('\n'); continue; }
            if (name == "img")
            {
                var alt = el.GetAttribute("alt");
                if (!string.IsNullOrWhiteSpace(alt)) sb.Append(alt);
                continue;
            }
            var block = Block.Contains(name);
            if (block) sb.Append('\n');
            if (name == "hr") { sb.Append("----------\n"); continue; }
            if (name == "li") sb.Append("- ");
            var start = sb.Length;
            WalkText(el, sb);
            if (name == "a")
            {
                var href = el.GetAttribute("href") ?? "";
                var text = sb.ToString(start, sb.Length - start).Trim();
                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) href = href[7..];
                else if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) href = "";
                // Append the target unless the link text already is the address (e.g. "www.contoso.com").
                if (text.Length == 0) sb.Append(href);
                else if (href.Length > 0 && !SameAddress(text, href)) sb.Append(" <").Append(href).Append('>');
                sb.Append(' ');
            }
            if (name is "td" or "th") sb.Append(' ');
            if (block) sb.Append('\n');
        }
    }

    private static bool SameAddress(string text, string href)
    {
        static string Norm(string v)
        {
            v = v.Trim().TrimEnd('/');
            foreach (var prefix in new[] { "https://", "http://", "www." })
                if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) v = v[prefix.Length..];
            return v;
        }
        return Norm(text).Equals(Norm(href), StringComparison.OrdinalIgnoreCase);
    }

    public static string ToRtf(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var sb = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Arial;}}{\colortbl;\red0\green0\blue238;}\f0\fs20 ");
        if (doc.Body != null) WalkRtf(doc.Body, sb);
        sb.Append('}');
        return sb.ToString();
    }

    private static void WalkRtf(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                AppendRtfText(sb, Spaces().Replace(t.Data.Replace('\n', ' ').Replace('\r', ' '), " "));
                continue;
            }
            if (child is not IElement el || Skip.Contains(el.LocalName)) continue;
            var name = el.LocalName;
            switch (name)
            {
                case "br": sb.Append(@"\line "); continue;
                case "img": AppendRtfText(sb, el.GetAttribute("alt") ?? ""); continue;
                case "hr": sb.Append(@"\par ----------\par "); continue;
            }
            var (open, close) = name switch
            {
                "b" or "strong" => (@"{\b ", "}"),
                "i" or "em" => (@"{\i ", "}"),
                "u" => (@"{\ul ", "}"),
                _ => ("", ""),
            };
            if (name == "a" && el.GetAttribute("href") is { Length: > 0 } href)
            {
                sb.Append(@"{\field{\*\fldinst HYPERLINK """);
                AppendRtfText(sb, href);
                sb.Append(@"""}{\fldrslt{\ul\cf1 ");
                WalkRtf(el, sb);
                sb.Append("}}}");
                continue;
            }
            sb.Append(open);
            WalkRtf(el, sb);
            sb.Append(close);
            if (name is "td" or "th") sb.Append(' ');
            if (Block.Contains(name)) sb.Append(@"\par ");
        }
    }

    private static void AppendRtfText(StringBuilder sb, string text)
    {
        foreach (var ch in text)
        {
            if (ch is '\\' or '{' or '}') sb.Append('\\').Append(ch);
            else if (ch < 128) sb.Append(ch);
            else sb.Append(@"\u").Append((short)ch).Append('?');
        }
    }
}
