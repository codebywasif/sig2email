using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Sig2Email.Core.Rendering;

public sealed class RenderContext
{
    public required IReadOnlyDictionary<string, string?> Values { get; init; }
    /// <summary>Produces the HTML for a <c>{Csat:&lt;surveyId&gt;}</c> token. Null → the token renders empty.</summary>
    public Func<Guid, string>? SurveyHtml { get; init; }
}

/// <summary>
/// Signature template language:
/// <list type="bullet">
/// <item><c>{FirstName}</c> – placeholder (HTML-encoded). Unknown names are left untouched so CSS braces survive.</item>
/// <item><c>{if MobilePhone}…{else}…{/if}</c> – conditional block; supports <c>!</c>, <c>==</c>, <c>!=</c>,
/// <c>contains</c>, <c>and</c>, <c>or</c>.</item>
/// <item><c>{Csat:GUID}</c> – embedded one-click survey widget.</item>
/// </list>
/// Templates are parsed once and are safe to render concurrently.
/// </summary>
public sealed partial class CompiledTemplate
{
    [GeneratedRegex(@"\{(?:(if)\s+([^{}]+)|(else)|(/if)|([A-Za-z][A-Za-z0-9_]*)(?::([0-9a-fA-F\-]{32,36}))?)\}")]
    private static partial Regex TokenRegex();

    private abstract class Node { }
    private sealed class TextNode(string text) : Node { public string Text = text; }
    private sealed class ValueNode(string name) : Node { public string Name = name; }
    private sealed class SurveyNode(Guid id) : Node { public Guid Id = id; }
    private sealed class IfNode(Condition cond) : Node
    {
        public Condition Cond = cond;
        public List<Node> Then = new();
        public List<Node>? Else;
    }

    private readonly List<Node> _root;
    public string Source { get; }
    public IReadOnlySet<Guid> SurveyIds { get; }

    private CompiledTemplate(string source, List<Node> root, HashSet<Guid> surveys)
    {
        Source = source;
        _root = root;
        SurveyIds = surveys;
    }

    public static CompiledTemplate Parse(string source)
    {
        var root = new List<Node>();
        var surveys = new HashSet<Guid>();
        var stack = new Stack<(IfNode node, bool inElse)>();
        List<Node> Current() => stack.Count == 0 ? root : stack.Peek().inElse ? stack.Peek().node.Else! : stack.Peek().node.Then;

        var pos = 0;
        foreach (Match m in TokenRegex().Matches(source))
        {
            void Literal() => Current().Add(new TextNode(source[pos..(m.Index + m.Length)]));
            void Before()
            {
                if (m.Index > pos) Current().Add(new TextNode(source[pos..m.Index]));
            }

            if (m.Groups[1].Success)
            {
                var cond = Condition.TryParse(m.Groups[2].Value);
                if (cond == null) { Literal(); }
                else
                {
                    Before();
                    var node = new IfNode(cond);
                    Current().Add(node);
                    stack.Push((node, false));
                }
            }
            else if (m.Groups[3].Success)
            {
                if (stack.Count == 0 || stack.Peek().inElse) { Literal(); }
                else
                {
                    Before();
                    var (node, _) = stack.Pop();
                    node.Else = new List<Node>();
                    stack.Push((node, true));
                }
            }
            else if (m.Groups[4].Success)
            {
                if (stack.Count == 0) { Literal(); }
                else { Before(); stack.Pop(); }
            }
            else
            {
                var name = m.Groups[5].Value;
                if (m.Groups[6].Success)
                {
                    if (name.Equals("Csat", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(m.Groups[6].Value, out var id))
                    {
                        Before();
                        Current().Add(new SurveyNode(id));
                        surveys.Add(id);
                    }
                    else Literal();
                }
                else if (Placeholders.IsKnown(name))
                {
                    Before();
                    Current().Add(new ValueNode(name));
                }
                else Literal();
            }
            pos = m.Index + m.Length;
        }
        if (pos < source.Length) Current().Add(new TextNode(source[pos..]));
        // Unclosed {if} blocks are tolerated: they simply run to the end of the template.
        return new CompiledTemplate(source, root, surveys);
    }

    public string Render(RenderContext ctx)
    {
        var sb = new StringBuilder(Source.Length + 256);
        Render(_root, ctx, sb);
        return sb.ToString();
    }

    private static void Render(List<Node> nodes, RenderContext ctx, StringBuilder sb)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case TextNode t: sb.Append(t.Text); break;
                case ValueNode v:
                    if (ctx.Values.TryGetValue(v.Name, out var val) && !string.IsNullOrEmpty(val))
                        sb.Append(WebUtility.HtmlEncode(val));
                    break;
                case SurveyNode s:
                    if (ctx.SurveyHtml != null) sb.Append(ctx.SurveyHtml(s.Id));
                    break;
                case IfNode i:
                    if (i.Cond.Evaluate(ctx.Values)) Render(i.Then, ctx, sb);
                    else if (i.Else != null) Render(i.Else, ctx, sb);
                    break;
            }
        }
    }

    /// <summary>or-of-ands of simple terms.</summary>
    internal sealed partial class Condition
    {
        private enum Op { NotEmpty, Empty, Eq, Neq, Contains }
        private readonly record struct Term(string Name, Op Op, string? Value);
        private readonly List<List<Term>> _anyOf = new();

        [GeneratedRegex(@"^\s*(!)?\s*([A-Za-z][A-Za-z0-9_]*)\s*(?:(==|!=|contains)\s*(?:""([^""]*)""|'([^']*)'))?\s*$")]
        private static partial Regex TermRegex();

        [GeneratedRegex(@"\s+or\s+", RegexOptions.IgnoreCase)]
        private static partial Regex OrRegex();

        [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase)]
        private static partial Regex AndRegex();

        public static Condition? TryParse(string expr)
        {
            var c = new Condition();
            foreach (var orPart in OrRegex().Split(expr))
            {
                var all = new List<Term>();
                foreach (var andPart in AndRegex().Split(orPart))
                {
                    var m = TermRegex().Match(andPart);
                    if (!m.Success) return null;
                    var name = m.Groups[2].Value;
                    var neg = m.Groups[1].Success;
                    if (m.Groups[3].Success)
                    {
                        if (neg) return null;
                        var value = m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value;
                        all.Add(new Term(name, m.Groups[3].Value switch { "==" => Op.Eq, "!=" => Op.Neq, _ => Op.Contains }, value));
                    }
                    else all.Add(new Term(name, neg ? Op.Empty : Op.NotEmpty, null));
                }
                c._anyOf.Add(all);
            }
            return c;
        }

        public bool Evaluate(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var all in _anyOf)
            {
                var ok = true;
                foreach (var t in all)
                {
                    values.TryGetValue(t.Name, out var v);
                    v = v?.Trim() ?? "";
                    var r = t.Op switch
                    {
                        Op.NotEmpty => v.Length > 0,
                        Op.Empty => v.Length == 0,
                        Op.Eq => string.Equals(v, t.Value, StringComparison.OrdinalIgnoreCase),
                        Op.Neq => !string.Equals(v, t.Value, StringComparison.OrdinalIgnoreCase),
                        _ => v.Contains(t.Value ?? "", StringComparison.OrdinalIgnoreCase),
                    };
                    if (!r) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }
    }
}
