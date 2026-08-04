using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for ApiToken nodes: Overview and Thumbnail.
/// </summary>
public static class ApiTokenLayoutAreas
{
    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the Thumbnail layout area.</summary>
    public const string ThumbnailArea = "Thumbnail";

    /// <summary>
    /// Registers the ApiToken layout-area views (Overview, Thumbnail) on the hub configuration.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register the views on.</param>
    /// <returns>The same configuration with the ApiToken views added, for chaining.</returns>
    public static MessageHubConfiguration AddApiTokenViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(ThumbnailArea, Thumbnail));

    /// <summary>
    /// Renders the Overview area for an ApiToken node: a markdown table of label, user, status, and timestamps.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the Overview view for the API token.</returns>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        // Stored timestamps are UTC; render them in the VIEWER's zone (AccessContext.TimeZoneId)
        // like every other node surface. Formatting the raw value showed UTC to everyone, so the
        // same instant read differently here than on the node's own Overview.
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ApiToken token)
                    return (UiControl?)Controls.Html("<div>No API token data.</div>");

                var status = token.IsRevoked ? "Revoked" : "Active";
                var expiry = token.ExpiresAt.HasValue
                    ? access.ToDisplayTime(token.ExpiresAt.Value).ToString("yyyy-MM-dd HH:mm")
                    : "Never";
                var lastUsed = token.LastUsedAt.HasValue
                    ? access.ToDisplayTime(token.LastUsedAt.Value).ToString("yyyy-MM-dd HH:mm")
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

    /// <summary>
    /// Renders the Thumbnail area for an ApiToken node: a compact label plus active/revoked status.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the Thumbnail view for the API token.</returns>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ApiToken token)
                    return (UiControl?)Controls.Html("<span>API Token</span>");

                var status = token.IsRevoked ? "Revoked" : "Active";
                return (UiControl?)Controls.Html(
                    $"<span><strong>{token.Label}</strong> — {status}</span>");
            });
    }
}
