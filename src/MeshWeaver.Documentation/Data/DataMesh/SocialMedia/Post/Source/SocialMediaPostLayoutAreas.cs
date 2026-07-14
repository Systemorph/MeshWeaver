// <meshweaver>
// Id: SocialMediaPostLayoutAreas
// DisplayName: Social Media Post Views
// </meshweaver>

using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Web;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Workspace projection of a sibling Post node: the display order plus the raw
/// <see cref="MeshNode"/> the List view formats. Keyed by <see cref="Path"/> so
/// the virtual collection stays deduplicated as the query result set evolves.
/// </summary>
public record PostNode
{
    [Key]
    public string Path { get; init; } = string.Empty;
    public MeshNode Node { get; init; } = null!;
}

public static class SocialMediaPostLayoutAreas
{
    public const string ListArea = "List";
    public const string DetailArea = "Detail";

    public static LayoutDefinition AddSocialMediaPostLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(ListArea, List)
            .WithView(DetailArea, Detail);

    private static ImmutableDictionary<string, MeshNode> ApplyChanges(
        ImmutableDictionary<string, MeshNode> current, QueryResultChange<MeshNode> change)
    {
        var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
            ? ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            : current;
        foreach (var item in change.Items)
            result = change.ChangeType == QueryChangeType.Removed
                ? result.Remove(item.Path)
                : result.SetItem(item.Path, item);
        return result;
    }

    private static string? GetProp(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        if (json.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
        return json.TryGetProperty(pascal, out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
    }

    private static int GetInt(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return 0;
        if (!json.TryGetProperty(prop, out var p))
        {
            var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
            if (!json.TryGetProperty(pascal, out p)) return 0;
        }
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;
    }

    private static DateTimeOffset? GetDate(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        if (!json.TryGetProperty(prop, out var p))
        {
            var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
            if (!json.TryGetProperty(pascal, out p)) return null;
        }
        return p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var dt) ? dt : null;
    }

    /// <summary>
    /// Virtual-source loader: the <c>namespace:Doc/DataMesh/SocialMedia/Post</c>
    /// mesh query, folded to a path-keyed snapshot and projected to
    /// <see cref="PostNode"/>. Registered in <c>Post.json</c>'s configuration
    /// via <c>WithVirtualDataSource</c> so the framework subscribes it ONCE at
    /// hub initialization on a managed scheduler — never from inside a view
    /// render (a query subscription in a layout-area render runs on the grain's
    /// activation thread and deadlocks the hub — see AsynchronousCalls). The
    /// List view then composes only the hub's OWN workspace stream.
    /// </summary>
    public static IObservable<IEnumerable<PostNode>> LoadPosts(IWorkspace workspace)
        => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery("namespace:Doc/DataMesh/SocialMedia/Post"))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase), ApplyChanges)
            .Select(dict => dict.Values
                .Select(n => new PostNode { Path = n.Path, Node = n })
                .ToImmutableList()
                .AsEnumerable());

    public static IObservable<UiControl?> List(LayoutAreaHost host, RenderingContext _)
    {
        var postsStream = host.Workspace.GetStream<PostNode>()
            ?? Observable.Return<PostNode[]?>(null);

        return postsStream.Select(posts => (UiControl?)BuildList(
            (posts ?? Enumerable.Empty<PostNode>())
                .Select(p => p.Node)
                .ToImmutableList()));
    }

    private static UiControl BuildList(ImmutableList<MeshNode> posts)
    {
        var ordered = posts
            .OrderByDescending(p => GetDate(p, "scheduledAt") ?? DateTimeOffset.MinValue)
            .ToImmutableList();

        if (ordered.Count == 0)
            return Controls.Stack
                .WithStyle("padding: 16px;")
                .WithView(Controls.Markdown("*No posts yet.*"));

        var rows = string.Join("", ordered.Select(p =>
        {
            var title = p.Name ?? GetProp(p, "title") ?? "(untitled)";
            var platformId = GetProp(p, "platform") ?? "LinkedIn";
            var platform = Platform.GetById(platformId);
            var scheduled = GetDate(p, "scheduledAt")?.ToString("yyyy-MM-dd HH:mm") ?? "\u2014";
            var published = GetDate(p, "publishedAt") is { } d ? d.ToString("yyyy-MM-dd HH:mm") : "\u2014";
            var likes = GetInt(p, "likes");
            var impressions = GetInt(p, "impressions");
            return $"""
                <tr>
                  <td style="padding:8px 12px;"><a href="/{HttpUtility.HtmlAttributeEncode(p.Path)}">{HttpUtility.HtmlEncode(title)}</a></td>
                  <td style="padding:8px 12px;"><span style="background:{platform.Color};color:white;padding:2px 8px;border-radius:10px;font-size:12px;">{platform.Emoji} {HttpUtility.HtmlEncode(platform.Name)}</span></td>
                  <td style="padding:8px 12px;color:#666;">{scheduled}</td>
                  <td style="padding:8px 12px;color:#666;">{published}</td>
                  <td style="padding:8px 12px;text-align:right;">{likes:N0}</td>
                  <td style="padding:8px 12px;text-align:right;">{impressions:N0}</td>
                </tr>
                """;
        }));

        var table = $"""
            <table style="border-collapse:collapse;width:100%;font-family:var(--body-font);">
              <thead>
                <tr style="text-align:left;border-bottom:2px solid #e5e5e5;">
                  <th style="padding:8px 12px;">Title</th>
                  <th style="padding:8px 12px;">Platform</th>
                  <th style="padding:8px 12px;">Scheduled</th>
                  <th style="padding:8px 12px;">Published</th>
                  <th style="padding:8px 12px;text-align:right;">Likes</th>
                  <th style="padding:8px 12px;text-align:right;">Impressions</th>
                </tr>
              </thead>
              <tbody>{rows}</tbody>
            </table>
            """;

        return Controls.Stack
            .WithStyle("padding: 16px; gap: 12px;")
            .WithView(Controls.Html($"<h2 style=\"margin:0;\">Posts</h2>"))
            .WithView(Controls.Html(table));
    }

    public static IObservable<UiControl?> Detail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.Workspace.GetStream<MeshNode>()!
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
            .Select(node =>
            {
                if (node is null)
                    return (UiControl?)Controls.Markdown("*Post not found.*");

                var title = node.Name ?? GetProp(node, "title") ?? "(untitled)";
                var body = GetProp(node, "body");
                var platformId = GetProp(node, "platform") ?? "LinkedIn";
                var platform = Platform.GetById(platformId);
                var scheduled = GetDate(node, "scheduledAt");
                var published = GetDate(node, "publishedAt");
                var status = published.HasValue ? "Published"
                    : (scheduled.HasValue && scheduled.Value > DateTimeOffset.Now ? "Scheduled" : "Draft");
                var statusColor = published.HasValue ? "#2e7d32" : "#ed6c02";
                var impressions = GetInt(node, "impressions");
                var likes = GetInt(node, "likes");

                var header = $$"""
                    <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;padding:8px 0;">
                      <span style="background:{{platform.Color}};color:white;padding:4px 10px;border-radius:12px;font-size:12px;font-weight:600;">{{platform.Emoji}} {{HttpUtility.HtmlEncode(platform.Name)}}</span>
                      <span style="background:{{statusColor}};color:white;padding:4px 10px;border-radius:12px;font-size:12px;font-weight:600;">{{status}}</span>
                    </div>
                    """;

                var dates = $$"""
                    <table style="border-collapse:collapse;margin:4px 0;font-size:14px;">
                      <tr><td style="color:#666;padding:2px 12px 2px 0;">Scheduled</td><td>{{HttpUtility.HtmlEncode(scheduled?.ToString("yyyy-MM-dd HH:mm") ?? "\u2014")}}</td></tr>
                      <tr><td style="color:#666;padding:2px 12px 2px 0;">Published</td><td>{{HttpUtility.HtmlEncode(published?.ToString("yyyy-MM-dd HH:mm") ?? "\u2014")}}</td></tr>
                    </table>
                    """;

                var stats = $$"""
                    <div style="display:flex;gap:24px;padding:12px;background:#f5f7fa;border-radius:6px;">
                      <div><div style="font-size:11px;color:#666;text-transform:uppercase;">Likes</div><div style="font-size:20px;font-weight:600;">{{likes:N0}}</div></div>
                      <div><div style="font-size:11px;color:#666;text-transform:uppercase;">Impressions</div><div style="font-size:20px;font-weight:600;">{{impressions:N0}}</div></div>
                    </div>
                    """;

                var stack = Controls.Stack
                    .WithStyle("padding: 16px; gap: 8px;")
                    .WithView(Controls.Html($"<h1 style=\"margin:0;\">{HttpUtility.HtmlEncode(title)}</h1>"))
                    .WithView(Controls.Html(header))
                    .WithView(Controls.Html(dates))
                    .WithView(Controls.Html(stats));
                if (!string.IsNullOrWhiteSpace(body))
                    stack = stack.WithView(Controls.Markdown(body));
                return (UiControl?)stack;
            });
    }
}
