using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Pin / Unpin actions for a node, plus a <c>PinnedThumbnail</c> renderer that shows a
/// node as a compact card with an overlay unpin icon.
/// Pin state lives in <see cref="User.PinnedPaths"/> on the current user's MeshNode.
/// </summary>
public static class PinLayoutArea
{
    /// <summary>Area name for the Pin action (adds this node's path to the viewer's pinned list).</summary>
    public const string PinArea = "Pin";

    /// <summary>Area name for the Unpin action (removes this node's path from the viewer's pinned list).</summary>
    public const string UnpinArea = "Unpin";

    /// <summary>Area name for the compact pinned-card renderer (used as MeshSearch ItemArea).</summary>
    public const string PinnedThumbnailArea = "PinnedThumbnail";

    /// <summary>
    /// Returns the Pin menu item. Always yields — pinning is idempotent.
    /// Hidden on the viewer's own User node (pinning your own profile is pointless).
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, string? viewerId)
    {
        if (string.IsNullOrEmpty(viewerId))
            return null;
        if (hubPath.Equals($"User/{viewerId}", StringComparison.OrdinalIgnoreCase))
            return null;
        return new("Pin", PinArea,
            Icon: "Bookmark",
            Order: 50,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, PinArea));
    }

    /// <summary>
    /// Pin layout area — performs an idempotent add of the current node's path to the
    /// viewer's <see cref="User.PinnedPaths"/>, then renders a confirmation with a back link.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Pin(LayoutAreaHost host, RenderingContext _)
        => TogglePinAndRender(host, unpin: false);

    /// <summary>
    /// Unpin layout area — removes the current node's path from the viewer's pinned list.
    /// Used by the unpin icon on pinned cards via a Href link, and by the Unpin menu item.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Unpin(LayoutAreaHost host, RenderingContext _)
        => TogglePinAndRender(host, unpin: true);

    /// <summary>
    /// Compact pinned card: standard node thumbnail with an overlay unpin button.
    /// Rendered per search result when the enclosing MeshSearch sets
    /// <see cref="MeshSearchControl.ItemArea"/> to <see cref="PinnedThumbnailArea"/>.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> PinnedThumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId
                       ?? "";

        return host.StreamView<MeshNode>(
            (nodes, _) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildPinnedCard(host, node, hubPath, viewerId);
            },
            hubPath);
    }

    private static UiControl BuildPinnedCard(LayoutAreaHost host, MeshNode? node, string hubPath, string viewerId)
    {
        var thumbnail = MeshNodeThumbnailControl.FromNode(node, hubPath);

        var stack = Controls.Stack
            .WithStyle("position: relative; width: 100%; height: 100%;")
            .WithView(thumbnail);

        if (string.IsNullOrEmpty(viewerId))
            return stack;

        // Overlay unpin button, top-right corner.
        // The click handler mutates PinnedPaths on the viewer's User node via workspace.UpdateMeshNode,
        // which dispatches remotely since the viewer's hub differs from this item's hub.
        // The viewer's dashboard observes the user stream and re-renders — the card disappears.
        var userPath = $"User/{viewerId}";
        var userAddress = new Address(userPath);
        var unpinButton = Controls.Button("")
            .WithIconStart(FluentIcons.Dismiss())
            .WithAppearance(Appearance.Stealth)
            .WithStyle("position: absolute; top: 4px; right: 4px; z-index: 5; " +
                       "min-width: 24px; width: 24px; height: 24px; padding: 0; " +
                       "border-radius: 50%; background: rgba(0,0,0,0.55); color: #fff;")
            .WithClickAction(ctx =>
            {
                // Remote write to a different hub's MeshNode (the User node lives at
                // userAddress, not this host's hub). Single-op write → read current via
                // GetMeshNodeStream(path), apply transform, post DataChangeRequest. The
                // owning hub's data layer applies the patch and broadcasts to subscribers.
                ctx.Host.Workspace.GetMeshNodeStream(userPath)
                    .Take(1).Timeout(TimeSpan.FromSeconds(10))
                    .Subscribe(n =>
                    {
                        if (n.Content is not User user) return;
                        var paths = user.PinnedPaths?.ToImmutableList() ?? ImmutableList<string>.Empty;
                        var updated = paths.RemoveAll(p =>
                            string.Equals(p, hubPath, StringComparison.OrdinalIgnoreCase));
                        var newNode = n with { Content = user with { PinnedPaths = updated } };
                        ctx.Host.Hub.Post(
                            new DataChangeRequest { Updates = [newNode] },
                            o => o.WithTarget(userAddress));
                    });
                return Task.CompletedTask;
            });

        return stack.WithView(unpinButton);
    }

    private static IObservable<UiControl?> TogglePinAndRender(LayoutAreaHost host, bool unpin)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId;

        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return<UiControl?>(BuildSimpleMessage(
                "Sign-in required",
                "You must be signed in to pin nodes.",
                backHref));

        var userPath = $"User/{viewerId}";
        var userAddress = new Address(userPath);

        // Single-op remote write: read current user via GetMeshNodeStream(path), apply
        // the pin/unpin transform, post DataChangeRequest to the owning user hub. The
        // viewer's dashboard subscribes to that user node and re-renders on the echo.
        host.Workspace.GetMeshNodeStream(userPath)
            .Take(1).Timeout(TimeSpan.FromSeconds(10))
            .Subscribe(node =>
            {
                if (node.Content is not User user) return;
                var paths = user.PinnedPaths?.ToImmutableList() ?? ImmutableList<string>.Empty;
                var updated = unpin
                    ? paths.RemoveAll(p => string.Equals(p, hubPath, StringComparison.OrdinalIgnoreCase))
                    : (paths.Any(p => string.Equals(p, hubPath, StringComparison.OrdinalIgnoreCase))
                        ? paths
                        : paths.Add(hubPath));
                var newNode = node with { Content = user with { PinnedPaths = updated } };
                host.Hub.Post(
                    new DataChangeRequest { Updates = [newNode] },
                    o => o.WithTarget(userAddress));
            });

        var title = unpin ? "Unpinned" : "Pinned";
        var message = unpin
            ? $"Removed <code>{System.Net.WebUtility.HtmlEncode(hubPath)}</code> from your pinned items."
            : $"Added <code>{System.Net.WebUtility.HtmlEncode(hubPath)}</code> to your pinned items.";

        return Observable.Return<UiControl?>(BuildSimpleMessage(title, message, backHref));
    }

    private static UiControl BuildSimpleMessage(string title, string messageHtml, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center; margin-bottom: 16px;")
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref))
                .WithView(Controls.H2(title).WithStyle("margin: 0;")))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{messageHtml}</p>"));
}
