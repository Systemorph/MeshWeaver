using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that adds "Connect LinkedIn" and
/// "Pull LinkedIn posts" menu items on the viewer's OWN User node page. These hit
/// the matching endpoints in <see cref="LinkedInConnectEndpoints"/>, which use the
/// User node's path as the profile under which the credential is stored.
///
/// Visibility rules: only shown on <c>User/{viewerId}</c>, i.e. a user can only
/// add their own credentials — never someone else's. On other User pages and on
/// non-User pages the provider yields nothing.
/// </summary>
public sealed class LinkedInCredentialMenuProvider : INodeMenuProvider
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

        // Only on the viewer's own user page.
        var ownPath = $"User/{viewerId}";
        if (!hubPath.Equals(ownPath, System.StringComparison.OrdinalIgnoreCase))
            yield break;

        var nodes = await (host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? System.Array.Empty<MeshNode>())
            ?? Observable.Return(System.Array.Empty<MeshNode>()))
            .FirstAsync();
        var node = nodes.FirstOrDefault(n => n.Path == hubPath);
        if (node is null) yield break;

        // Connect item: starts OAuth flow against the user's own node.
        yield return new NodeMenuItemDefinition(
            Label: "Connect LinkedIn",
            Area: "ConnectLinkedIn",
            Icon: "LinkSquare",
            RequiredPermission: Permission.Update,
            Order: 60,
            Href: "/connect/linkedin/me");

        // Pull-posts item: scrapes member's past posts into the mesh.
        yield return new NodeMenuItemDefinition(
            Label: "Pull LinkedIn posts",
            Area: "PullLinkedIn",
            Icon: "ArrowDownload",
            RequiredPermission: Permission.Update,
            Order: 61,
            Href: "/connect/linkedin/pull/me");
    }
}
