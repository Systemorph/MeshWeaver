using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// Injects a single "Social Media" menu item on the viewer's own <c>User</c> node.
/// The item navigates to <c>{userPath}/SocialMedia</c>, a dynamic
/// <c>Systemorph/SocialMediaHub</c> NodeType that lists all connected platform
/// profiles, offers connect shortcuts for missing platforms, and provides
/// disconnect/manage actions via its own menu.
///
/// The hub node is created lazily the first time this menu is rendered, so any
/// existing user gets it automatically without a backfill step.
/// </summary>
public sealed class SocialMediaUserMenuProvider : INodeMenuProvider
{
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    public async IAsyncEnumerable<NodeMenuItemDefinition> GetItemsAsync(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var accessService = host.Hub.ServiceProvider.GetService(typeof(AccessService)) as AccessService;
        var viewerId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId)) yield break;
        if (!hubPath.Equals($"User/{viewerId}", System.StringComparison.OrdinalIgnoreCase))
            yield break;

        var mesh = host.Hub.ServiceProvider.GetService(typeof(IMeshService)) as IMeshService;
        if (mesh is null) yield break;

        var nodes = await (host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? System.Array.Empty<MeshNode>())
            ?? Observable.Return(System.Array.Empty<MeshNode>()))
            .FirstAsync();
        var node = nodes.FirstOrDefault(n => n.Path == hubPath);
        if (node is null || !string.Equals(node.NodeType, "User", System.StringComparison.OrdinalIgnoreCase))
            yield break;

        // Lazy-create the hub node if it doesn't exist yet. Any failure is non-fatal —
        // we still yield the menu item; the target page will show the empty state.
        var hubNodePath = $"{hubPath}/SocialMedia";
        var exists = false;
        await foreach (var _ in mesh.QueryAsync<MeshNode>($"path:{hubNodePath}"))
        { exists = true; break; }

        if (!exists)
        {
            try
            {
                await mesh.CreateNodeAsync(new MeshNode("SocialMedia", hubPath)
                {
                    Name = "Social Media",
                    NodeType = "Systemorph/SocialMediaHub",
                    State = MeshNodeState.Active,
                    Content = new Dictionary<string, object?>
                    {
                        ["$type"] = "SocialMediaHub",
                        ["createdAt"] = System.DateTimeOffset.UtcNow
                    }
                });
            }
            catch { /* race / already exists / perms — menu still shows, target page handles missing */ }
        }

        yield return new NodeMenuItemDefinition(
            Label: "Social Media",
            Area: "SocialMedia",
            Icon: "Share",
            RequiredPermission: Permission.Read,
            Order: 50,
            Href: $"/{hubNodePath}");
    }
}
