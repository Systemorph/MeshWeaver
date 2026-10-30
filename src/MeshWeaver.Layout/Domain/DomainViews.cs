using System.Reactive.Linq;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Layout.Domain;

/// <summary>
/// The header pieces the domain views (Catalog, Edit, Data Model) share, composed from the
/// platform's controls only: a title with its navigation glyphs on the right, and the type's
/// XML-doc summary as a hint line.
/// </summary>
internal static class DomainViews
{
    /// <summary>
    /// A heading with navigation links pushed to the right edge — the Data Model ⧉, Base 🔝 and
    /// Catalog 🗃️ glyphs. Each link carries its hover text, which is also its accessible name.
    /// </summary>
    /// <param name="title">The heading control (an H1 or H2).</param>
    /// <param name="links">The navigation links, in display order.</param>
    /// <returns>A horizontal stack: the title, a spacer, the links.</returns>
    internal static UiControl TitleRow(UiControl title, params LinkControl[] links)
    {
        var row = Controls.Stack
            .WithWidth("100%")
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("align-items: center; margin-bottom: 1rem;")
            .WithView(title)
            .WithView(Controls.Spacer);
        foreach (var link in links)
            row = row.WithView(link);
        return row;
    }

    /// <summary>A navigation glyph link with its hover text.</summary>
    /// <param name="glyph">The glyph shown.</param>
    /// <param name="tooltip">The hover text.</param>
    /// <param name="href">The target.</param>
    /// <returns>A <see cref="LinkControl"/>.</returns>
    internal static LinkControl NavGlyph(string glyph, string tooltip, string href) =>
        Controls.Link(glyph, href).WithTooltip(tooltip).WithStyle("text-decoration: none; font-size: 1.5em; line-height: 1;");

    /// <summary>The type's XML-doc summary as a hint line, or null when there is none.</summary>
    /// <param name="summary">The summary text (plain text, from the mesh's <c>XmlDocsReader</c>).</param>
    /// <returns>A <see cref="Controls.Hint"/> label, or null.</returns>
    internal static UiControl? Description(string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? null : Controls.Hint(summary).WithStyle("margin-bottom: 1rem;");

    /// <summary>
    /// The type's XML-doc summary as a DATA-BOUND hint line: the page renders without waiting for it,
    /// and the line binds once the summary is in memory — the documentation file is read on the mesh's
    /// I/O pool, never on the thread building the view (Doc/Architecture/XmlDocumentationReader).
    /// </summary>
    /// <param name="hub">The hub rendering the view.</param>
    /// <param name="type">The documented type.</param>
    /// <returns>The hint line, or null when the type has no summary.</returns>
    internal static IObservable<UiControl?> DescriptionOf(IMessageHub hub, Type type) =>
        hub.ObserveXmlDocsSummary(type).Select(Description);
}
