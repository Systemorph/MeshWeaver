// <meshweaver>
// Id: ArticleViewTests
// DisplayName: Article Views Tests — the views are templates
// </meshweaver>
#nullable enable
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

/// <summary>
/// The cases of <c>ACME/Article</c>'s views: each view is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later") — built without the node, its first emission is the whole page,
/// with no view deferred until data arrives — and it binds the node by PATH. Pure cases: they
/// build the templates from a path and THROW on failure. <see cref="Tests"/> runs them; a case not
/// listed there runs nowhere.
/// </summary>
public static class ArticleViewTests
{
    private const string NodePath = "ACME/Articles/Launch";

    /// <summary>The Tests view — registered in the NodeType's configuration as <c>WithView("Tests", …)</c>.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One verdict frame: the <c>N/M passed</c> title and a row per case.</returns>
    public static UiControl Tests(LayoutAreaHost host, RenderingContext context)
    {
        var cases = new (string Name, Action Body)[]
        {
            ("The overview is whole while its header feed is silent; title and body are pointers into the node.", Overview_IsATemplateBoundByPath),
            ("The metadata line: authors, the date in the viewer's format, tags and the thumbnail.", Header_ComposesTheMetadata),
        };
        var rows = cases.Select(c => Run(c.Name, c.Body)).ToImmutableList();
        return Controls.Stack
            .WithView(Controls.Markdown($"### Article views tests — {rows.Count(r => r.Passed)}/{rows.Count} passed"))
            .WithView(host.ToDataGrid(rows));
    }

    /// <summary>The overview is whole while its header feed is silent; title and body are pointers into the node.</summary>
    public static void Overview_IsATemplateBoundByPath()
    {
        var template = ArticleLayoutAreas.OverviewTemplate(NodePath, Observable.Never<string>());
        ExpectTemplate(template, "Overview");
        Expect(Bindings(template).Single() == ("name", LayoutAreaReference.GetMeshNodeDataContext(NodePath, bindContent: false)),
            "the title binds the node's own name");
        var markdown = LayoutTemplate.Descendants(template).OfType<MarkdownControl>().ToList();
        Expect(markdown.Any(m => m.Markdown is JsonPointerReference { Pointer: "content" }
                && m.DataContext == LayoutAreaReference.GetMeshNodeDataContext(NodePath)),
            "the body is a pointer into the node's MarkdownContent, never its text");
        Expect(markdown.Any(m => m.DataContext == LayoutAreaReference.GetDataPointer(ArticleLayoutAreas.HeaderDataId)),
            "the metadata line is declared up front and fed through /data");
    }

    /// <summary>The metadata line: authors, the date in the viewer's format, tags and the thumbnail.</summary>
    public static void Header_ComposesTheMetadata()
    {
        var node = new MeshNode("Launch", "ACME/Articles") { LastModified = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) };
        var content = new MarkdownContent { Content = "# Launch", Authors = ["Ada", "Alan"], Tags = ["news"], Thumbnail = "cover.png" };
        var header = ArticleLayoutAreas.Header(node, content, CultureInfo.GetCultureInfo("en"));
        Expect(header.Contains("Ada, Alan", StringComparison.Ordinal), header);
        Expect(header.Contains("June 1, 2026", StringComparison.Ordinal) || header.Contains("1 June 2026", StringComparison.Ordinal), header);
        Expect(header.Contains("`news`", StringComparison.Ordinal), header);
        Expect(header.Contains("![](/api/content/ACME/Articles/cover.png)", StringComparison.Ordinal), header);
        Expect(ArticleLayoutAreas.Header(null, null, CultureInfo.GetCultureInfo("en")) == "", "no node, no header");
    }

    /// <summary>The node-bound pointers of the tree's labels, as <c>(pointer, data context)</c>.</summary>
    private static ImmutableList<(string Pointer, string? Context)> Bindings(UiControl root) =>
        LayoutTemplate.Descendants(root)
            .OfType<LabelControl>()
            .Where(label => label.Data is JsonPointerReference)
            .Select(label => (((JsonPointerReference)label.Data).Pointer, label.DataContext))
            .ToImmutableList();

    private static void ExpectTemplate(UiControl template, string view)
    {
        var deferred = LayoutTemplate.DeferredViews(template);
        Expect(deferred.IsEmpty, $"{view} defers {deferred.Count} view(s) until data arrives: {string.Join(", ", deferred)}");
        Expect(!LayoutTemplate.Descendants(template).OfType<HtmlControl>().Any(),
            $"{view} renders hand-built HTML; compose platform controls instead");
    }

    private static ArticleViewTestsRow Run(string name, Action body)
    {
        try
        {
            body();
            return new(name, "✅ pass");
        }
        catch (Exception ex)
        {
            return new(name, "❌ " + ex.Message.Replace('\n', ' '));
        }
    }

    private static void Expect(bool condition, string because)
    {
        if (!condition)
            throw new InvalidOperationException(because);
    }
}

/// <summary>One executed case, as a grid row.</summary>
/// <param name="Case">What the case asserts.</param>
/// <param name="Result">✅, or ❌ with the failure message.</param>
public sealed record ArticleViewTestsRow(string Case, string Result)
{
    /// <summary>Whether the case passed.</summary>
    public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);
}
