using System.Collections.Immutable;
using System.Net;
using System.Text;

namespace MeshWeaver.Markdown.Export.Html;

/// <summary>
/// An immutable node in the email-HTML output tree, and the ONE place that turns a tree into a
/// string of HTML.
///
/// <para>Email HTML is a SERIALIZATION TARGET, not application UI: it must survive mail clients
/// that support neither the framework's controls nor a stylesheet, so it genuinely has to be
/// emitted as markup. What is forbidden is doing that with string interpolation scattered across
/// the code — that is how escaping bugs and unbalanced tags get in. So the renderers build a
/// tree of these nodes and exactly one method (<see cref="Render()"/>) writes markup, escaping
/// text and attribute values on the single path out.</para>
/// </summary>
public abstract record MarkupNode
{
    /// <summary>Writes this node (and its subtree) as HTML.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    internal abstract void Write(StringBuilder sb);

    /// <summary>A text node — HTML-escaped on write, so callers never escape by hand.</summary>
    public static MarkupNode Text(string? value) => new MarkupTextNode(value ?? string.Empty);

    /// <summary>
    /// Pre-rendered markup passed through verbatim. Used for the markdown body, which the ONE
    /// server-side Markdig pipeline already rendered, and which the sanitizer has already
    /// stripped of script/style/SVG. Never use it for values that came from user input without
    /// going through <see cref="HtmlDowngrade"/> first.
    /// </summary>
    public static MarkupNode Raw(string? html) => new MarkupRawNode(html ?? string.Empty);

    /// <summary>An element with attributes and children.</summary>
    public static MarkupElement El(string tag, params MarkupNode[] children) =>
        new(tag, ImmutableArray<KeyValuePair<string, string>>.Empty, [.. children]);

    /// <summary>A sequence of nodes with no wrapping element.</summary>
    public static MarkupNode Fragment(IEnumerable<MarkupNode> children) =>
        new MarkupFragmentNode([.. children]);

    /// <summary>Nothing at all — the identity node, for a control that contributes no output.</summary>
    public static MarkupNode Empty { get; } = new MarkupFragmentNode(ImmutableArray<MarkupNode>.Empty);
}

/// <summary>An HTML element: tag, attributes, children.</summary>
public sealed record MarkupElement(
    string Tag,
    ImmutableArray<KeyValuePair<string, string>> Attributes,
    ImmutableArray<MarkupNode> Children) : MarkupNode
{
    /// <summary>
    /// Elements that must be written self-closing and never given a closing tag.
    /// </summary>
    private static readonly ImmutableHashSet<string> VoidElements =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "img", "br", "hr", "meta", "link");

    /// <summary>Adds an attribute (skipped entirely when the value is null/empty).</summary>
    public MarkupElement With(string name, string? value) =>
        string.IsNullOrEmpty(value)
            ? this
            : this with { Attributes = Attributes.Add(new KeyValuePair<string, string>(name, value)) };

    /// <summary>Adds an integer-valued attribute.</summary>
    public MarkupElement With(string name, int value) =>
        With(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Adds the inline <c>style</c> attribute — the only styling mechanism email allows.</summary>
    public MarkupElement Style(string? css) => With("style", css);

    /// <summary>Appends children.</summary>
    public MarkupElement Add(params MarkupNode[] children) =>
        this with { Children = Children.AddRange(children) };

    /// <summary>Appends children.</summary>
    public MarkupElement Add(IEnumerable<MarkupNode> children) =>
        this with { Children = Children.AddRange(children) };

    internal override void Write(StringBuilder sb)
    {
        sb.Append('<').Append(Tag);
        foreach (var (name, value) in Attributes)
            sb.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');

        if (VoidElements.Contains(Tag))
        {
            sb.Append(" />");
            return;
        }

        sb.Append('>');
        foreach (var child in Children)
            child.Write(sb);
        sb.Append("</").Append(Tag).Append('>');
    }
}

internal sealed record MarkupTextNode(string Value) : MarkupNode
{
    internal override void Write(StringBuilder sb) => sb.Append(WebUtility.HtmlEncode(Value));
}

internal sealed record MarkupRawNode(string Html) : MarkupNode
{
    internal override void Write(StringBuilder sb) => sb.Append(Html);
}

internal sealed record MarkupFragmentNode(ImmutableArray<MarkupNode> Children) : MarkupNode
{
    internal override void Write(StringBuilder sb)
    {
        foreach (var child in Children)
            child.Write(sb);
    }
}
