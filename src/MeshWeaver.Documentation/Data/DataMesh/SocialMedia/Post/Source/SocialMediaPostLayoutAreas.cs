// <meshweaver>
// Id: SocialMediaPostLayoutAreas
// DisplayName: Social Media Post Views
// </meshweaver>

using System.Globalization;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The Post's views — all TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later"): each is
/// on screen at the first render and binds its data instead of loading it on the hub.
///
/// <para><b>List</b> is a <see cref="MeshSearchControl"/>: the GUI runs the query, and every result
/// renders through that post's OWN <b>Row</b> area — so the list's hub reads no post at all.
/// <b>Row</b> and <b>Detail</b> bind the post's stored fields by pointer; the one value the hub has
/// to DERIVE — the Published / Scheduled / Draft status — comes from <see cref="StatusFeed"/>, a
/// function that builds no control, bound into a badge declared up front.</para>
/// </summary>
public static class SocialMediaPostLayoutAreas
{
    /// <summary>The list view.</summary>
    public const string ListArea = "List";

    /// <summary>One post as a list row — what <see cref="ListArea"/> renders per result.</summary>
    public const string RowArea = "Row";

    /// <summary>The post's detail view.</summary>
    public const string DetailArea = "Detail";

    /// <summary>Where the posts live.</summary>
    public const string PostsNamespace = "Doc/DataMesh/SocialMedia/Post";

    /// <summary>The <c>/data</c> id the status feed writes to.</summary>
    public const string StatusDataId = "postStatus";

    /// <summary>Registers the post views.</summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddSocialMediaPostLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(ListArea, List)
            .WithView(RowArea, Row)
            .WithView(DetailArea, Detail);

    /// <summary>Every post, newest schedule first.</summary>
    /// <param name="host">The area host; nothing of it is read.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The list template.</returns>
    public static UiControl List(LayoutAreaHost host, RenderingContext _) => ListTemplate();

    /// <summary>One post as a row: title, platform, dates and counts.</summary>
    /// <param name="host">The area host; the status feed reads its node.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The row template.</returns>
    public static UiControl Row(LayoutAreaHost host, RenderingContext _)
        => RowTemplate(host.Hub.Address.ToString(), StatusFeed(host));

    /// <summary>The post: title, platform and status, dates, counts and body.</summary>
    /// <param name="host">The area host; the status feed reads its node.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The detail template.</returns>
    public static UiControl Detail(LayoutAreaHost host, RenderingContext _)
        => DetailTemplate(host.Hub.Address.ToString(), StatusFeed(host));

    /// <summary>The list: a query the GUI runs, each result drawn by the post's own <see cref="RowArea"/>.</summary>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl ListTemplate() =>
        Controls.Stack
            .WithStyle("padding: 16px; gap: 12px;")
            .WithView(Controls.H2("Posts"))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery(PostsQuery)
                .WithShowSearchBox(false)
                .WithItemArea(RowArea)
                .WithGridBreakpoints(xs: 12, sm: 12, md: 12, lg: 12));

    /// <summary>The query the list runs: the posts, newest schedule first.</summary>
    public const string PostsQuery = $"namespace:{PostsNamespace} nodeType:{PostsNamespace} sort:content.scheduledAt-desc";

    /// <summary>The post at <paramref name="nodePath"/> as one row, bound by path.</summary>
    /// <param name="nodePath">The post node.</param>
    /// <param name="status">The derived status (<see cref="Status"/>), as it changes.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl RowTemplate(string nodePath, IObservable<string> status)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 16px; align-items: center; flex-wrap: wrap;")
            .WithView(Controls.Label(new JsonPointerReference("name")) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            })
            .WithView(Controls.Badge(new JsonPointerReference("platform")) with { DataContext = content })
            .WithView(status.Bind(text => Controls.Badge(text), StatusDataId))
            .WithView(Field("Scheduled", "scheduledAt", content))
            .WithView(Field("Likes", "likes", content))
            .WithView(Field("Impressions", "impressions", content));
    }

    /// <summary>The post at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The post node.</param>
    /// <param name="status">The derived status (<see cref="Status"/>), as it changes.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl DetailTemplate(string nodePath, IObservable<string> status)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack
            .WithStyle("padding: 16px; gap: 8px;")
            .WithView(Controls.H1(new JsonPointerReference("name")) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            })
            .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 12px;")
                .WithView(Controls.Badge(new JsonPointerReference("platform")) with { DataContext = content })
                .WithView(status.Bind(text => Controls.Badge(text), StatusDataId)))
            .WithView(Field("Scheduled", "scheduledAt", content))
            .WithView(Field("Published", "publishedAt", content))
            .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 24px;")
                .WithView(Field("Likes", "likes", content))
                .WithView(Field("Impressions", "impressions", content)))
            .WithView(Controls.Markdown(new JsonPointerReference("body")) with { DataContext = content });
    }

    /// <summary>
    /// Published once it has a publish time; Scheduled while its schedule lies ahead; else Draft.
    /// Pure. <c>now</c> is UTC: the stored times are instants, so comparing them to a local clock
    /// would flip the status by the host's offset.
    /// </summary>
    /// <param name="post">The post, or <c>null</c> when the node carries none.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The status text.</returns>
    public static string Status(SocialMediaPost? post, DateTimeOffset now) =>
        post is null ? "Draft"
        : post.PublishedAt.HasValue ? "Published"
        : post.ScheduledAt > now ? "Scheduled"
        : "Draft";

    /// <summary>
    /// The FEED half: the post's <see cref="Status"/> on every emission of its node. Builds no
    /// control. A failure is logged and shown as text — reported, never swallowed.
    /// </summary>
    /// <param name="host">The area host whose node is read.</param>
    /// <returns>The status text.</returns>
    public static IObservable<string> StatusFeed(LayoutAreaHost host) =>
        host.Workspace.GetMeshNodeStream()
            .Select(node => Status(node.ContentAs<SocialMediaPost>(host.Hub.JsonSerializerOptions), DateTimeOffset.UtcNow))
            .DistinctUntilChanged()
            .Catch<string, Exception>(ex =>
            {
                host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(SocialMediaPostLayoutAreas))
                    .LogWarning(ex, "The status of post {Path} could not be read", host.Hub.Address);
                return Observable.Return("Status could not be read");
            });

    /// <summary>A caption over a value bound into the content.</summary>
    private static UiControl Field(string caption, string pointer, string content) =>
        Controls.Stack
            .WithView(Controls.Label(caption).WithStyle("font-size: 11px; color: var(--neutral-foreground-hint);"))
            .WithView(Controls.Label(new JsonPointerReference(pointer)) with { DataContext = content });
}
