using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for ApiToken nodes: Overview and Thumbnail.
/// </summary>
public static class ApiTokenLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string ThumbnailArea = "Thumbnail";

    public static MessageHubConfiguration AddApiTokenViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(ThumbnailArea, Thumbnail));

    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetStream<MeshNode>()!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                if (node?.Content is not ApiToken token)
                    return (UiControl?)Controls.Html("<div>No API token data.</div>");

                var status = token.IsRevoked ? "Revoked" : "Active";
                var expiry = token.ExpiresAt.HasValue
                    ? token.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm")
                    : "Never";
                var lastUsed = token.LastUsedAt.HasValue
                    ? token.LastUsedAt.Value.ToString("yyyy-MM-dd HH:mm")
                    : "Never";

                var md = $"""
                    ## {token.Label}

                    | Property | Value |
                    |---|---|
                    | **User** | {token.UserName} ({token.UserEmail}) |
                    | **Status** | {status} |
                    | **Created** | {token.CreatedAt:yyyy-MM-dd HH:mm} |
                    | **Expires** | {expiry} |
                    | **Last Used** | {lastUsed} |
                    """;

                return (UiControl?)Controls.Markdown(md);
            });
    }

    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetStream<MeshNode>()!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                if (node?.Content is not ApiToken token)
                    return (UiControl?)Controls.Html("<span>API Token</span>");

                var status = token.IsRevoked ? "Revoked" : "Active";
                return (UiControl?)Controls.Html(
                    $"<span><strong>{token.Label}</strong> — {status}</span>");
            });
    }
}
