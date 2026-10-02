// <meshweaver>
// Id: PostViewTests
// DisplayName: Post Views Tests — the views are templates
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

/// <summary>
/// The cases of <c>Doc/DataMesh/SocialMedia/Post</c>'s views: each view is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later") — built without the node, its first emission is the whole page,
/// with no view deferred until data arrives — and it binds the node by PATH. Pure cases: they
/// build the templates from a path and THROW on failure. <see cref="Tests"/> runs them; a case not
/// listed there runs nowhere.
/// </summary>
public static class PostViewTests
{
    private const string NodePath = "Doc/DataMesh/SocialMedia/Post/Post-001";

    /// <summary>The Tests view — registered in the NodeType's configuration as <c>WithView("Tests", …)</c>.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One verdict frame: the <c>N/M passed</c> title and a row per case.</returns>
    public static UiControl Tests(LayoutAreaHost host, RenderingContext context)
    {
        var cases = new (string Name, Action Body)[]
        {
            ("The list is a query the GUI runs; each post draws its own row.", List_IsAQueryTheGuiRuns),
            ("A row is whole while its status feed is silent, and binds the post by path.", Row_IsATemplateBoundByPath),
            ("The detail is whole while its status feed is silent, and binds the post by path.", Detail_IsATemplateBoundByPath),
            ("The status: published wins, a future schedule is scheduled, anything else a draft.", Status_IsDerivedFromTheDates),
        };
        var rows = cases.Select(c => Run(c.Name, c.Body)).ToImmutableList();
        return Controls.Stack
            .WithView(Controls.Markdown($"### Post views tests — {rows.Count(r => r.Passed)}/{rows.Count} passed"))
            .WithView(host.ToDataGrid(rows));
    }

    /// <summary>The list is a query the GUI runs; each post draws its own row.</summary>
    public static void List_IsAQueryTheGuiRuns()
    {
        var template = SocialMediaPostLayoutAreas.ListTemplate();
        ExpectTemplate(template, "List");
        var search = LayoutTemplate.Descendants(template).OfType<MeshSearchControl>().SingleOrDefault();
        Expect(search is not null, "the posts are a MeshSearch, not rows built on the hub");
        Expect(Equals(search!.HiddenQuery, SocialMediaPostLayoutAreas.PostsQuery), $"the list runs the posts query, got {search.HiddenQuery}");
        Expect(Equals(search.ItemArea, SocialMediaPostLayoutAreas.RowArea), "each result renders through the post's own Row area");
    }

    /// <summary>A row is whole while its status feed is silent, and binds the post by path.</summary>
    public static void Row_IsATemplateBoundByPath()
    {
        var template = SocialMediaPostLayoutAreas.RowTemplate(NodePath, Observable.Never<string>());
        ExpectTemplate(template, "Row");
        var content = LayoutAreaReference.GetMeshNodeDataContext(NodePath);
        var bindings = Bindings(template);
        Expect(bindings.Contains(("name", LayoutAreaReference.GetMeshNodeDataContext(NodePath, bindContent: false))), "the title is the node's name");
        foreach (var pointer in new[] { "scheduledAt", "likes", "impressions" })
            Expect(bindings.Contains((pointer, content)), $"the row binds '{pointer}' in the content");
        Expect(LayoutTemplate.Descendants(template).OfType<BadgeControl>().Any(b => b.DataContext == LayoutAreaReference.GetDataPointer(SocialMediaPostLayoutAreas.StatusDataId)),
            "the status badge is declared up front and fed through /data");
    }

    /// <summary>The detail is whole while its status feed is silent, and binds the post by path.</summary>
    public static void Detail_IsATemplateBoundByPath()
    {
        var template = SocialMediaPostLayoutAreas.DetailTemplate(NodePath, Observable.Never<string>());
        ExpectTemplate(template, "Detail");
        var content = LayoutAreaReference.GetMeshNodeDataContext(NodePath);
        var bindings = Bindings(template);
        foreach (var pointer in new[] { "scheduledAt", "publishedAt", "likes", "impressions" })
            Expect(bindings.Contains((pointer, content)), $"the detail binds '{pointer}' in the content");
        Expect(LayoutTemplate.Descendants(template).OfType<MarkdownControl>().Any(m => m.Markdown is JsonPointerReference { Pointer: "body" } && m.DataContext == content),
            "the body is a pointer into the content, never its text");
    }

    /// <summary>The status: published wins, a future schedule is scheduled, anything else a draft.</summary>
    public static void Status_IsDerivedFromTheDates()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var post = new SocialMediaPost { ScheduledAt = now.AddHours(1) };
        Expect(SocialMediaPostLayoutAreas.Status(post, now) == "Scheduled", "a schedule ahead is scheduled");
        Expect(SocialMediaPostLayoutAreas.Status(post with { ScheduledAt = now.AddHours(-1) }, now) == "Draft", "a lapsed schedule without a publish is a draft");
        Expect(SocialMediaPostLayoutAreas.Status(post with { PublishedAt = now }, now) == "Published", "a publish time wins");
        Expect(SocialMediaPostLayoutAreas.Status(null, now) == "Draft", "no content is a draft");
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

    private static PostViewTestsRow Run(string name, Action body)
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
public sealed record PostViewTestsRow(string Case, string Result)
{
    /// <summary>Whether the case passed.</summary>
    public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);
}
