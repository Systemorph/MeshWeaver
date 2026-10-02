using System.Reactive.Linq;

namespace MeshWeaver.Layout;

/// <summary>
/// Display controls whose text is a PROJECTION computed on the hub and BOUND by pointer — the
/// "rows computed on the hub" row of Doc/GUI/DataBinding → "Templates first, data later".
///
/// <para>The control is returned AT ONCE, with its value a <c>JsonPointerReference</c> into
/// <c>/data/{id}</c>; the projection stream feeds that slot (<see cref="Template"/>'s stream
/// <c>Bind</c>, which subscribes in the control's buildup and is disposed with its area). So the
/// area's first emission is the finished template — nothing waits on the read — and every later
/// emission of the source updates the bound text in place, where a baked-in string would have
/// been a snapshot.</para>
///
/// <para>Use this only where the displayed text genuinely has to be COMPUTED from the data
/// (a serialization, a rendered fragment). A field the node already carries binds straight to the
/// node instead (<c>LayoutAreaReference.GetMeshNodeDataContext</c> + a pointer), with no hub work
/// at all.</para>
/// </summary>
public static class BoundProjections
{
    /// <summary>The <c>/data</c> payload behind <see cref="BoundMarkdown"/>.</summary>
    /// <param name="Markdown">The markdown text to show.</param>
    public sealed record MarkdownProjection(string Markdown);

    /// <summary>The <c>/data</c> payload behind <see cref="BoundHtml"/>.</summary>
    /// <param name="Html">The HTML fragment to show.</param>
    public sealed record HtmlProjection(string Html);

    /// <summary>
    /// A <see cref="MarkdownControl"/> bound to <c>/data/{id}</c>, fed by <paramref name="markdown"/>.
    /// </summary>
    /// <param name="markdown">The live markdown source — typically a projection of a node stream.</param>
    /// <param name="id">The data id; unique within the area's layout stream.</param>
    /// <returns>The bound control, returned before <paramref name="markdown"/> has emitted.</returns>
    public static MarkdownControl BoundMarkdown(this IObservable<string> markdown, string id)
        => markdown
            .Select(text => new MarkdownProjection(text))
            .Bind(p => new MarkdownControl(p.Markdown), id);

    /// <summary>
    /// An <see cref="HtmlControl"/> bound to <c>/data/{id}</c>, fed by <paramref name="html"/>.
    /// </summary>
    /// <param name="html">The live HTML source — typically a projection of a node stream.</param>
    /// <param name="id">The data id; unique within the area's layout stream.</param>
    /// <returns>The bound control, returned before <paramref name="html"/> has emitted.</returns>
    public static HtmlControl BoundHtml(this IObservable<string> html, string id)
        => html
            .Select(text => new HtmlProjection(text))
            .Bind(p => new HtmlControl(p.Html), id);
}
