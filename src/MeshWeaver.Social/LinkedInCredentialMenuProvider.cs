using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Social;

/// <summary>
/// Adds two contextual menu items for the LinkedIn publishing integration:
///
///   - On a <b>User</b> node (viewer's own only): "Link LinkedIn account" —
///     visible only when no LinkedIn credential exists yet under
///     <c>{userPath}/_ApiCredentials/linkedin</c>. Once linked, the item
///     self-hides so the user isn't prompted to re-link.
///
///   - On an <b>ApiCredential</b> node whose content's Platform is LinkedIn:
///     "Download past posts" — triggers the pull endpoint rooted at the
///     credential's parent (i.e. the user path).
///
/// Both items require Update permission on the target node, which the viewer
/// has by definition on their own user + satellites.
///
/// <para>The credential-presence and node-shape predicates are beyond the closed
/// <c>UiContribution</c> vocabulary, so this stays a DI provider — registered by the
/// Social module's <see cref="SocialExtensions.AddSocial"/> configure path. It emits
/// nothing while <c>Social:LinkedIn:ClientId</c> is unconfigured, preserving the
/// pre-module behaviour where the provider was only registered when configured.</para>
/// </summary>
/// <param name="options">LinkedIn app options; an empty ClientId keeps the provider inert.</param>
public sealed class LinkedInCredentialMenuProvider(IOptions<LinkedInOptions> options) : INodeMenuProvider
{
    /// <inheritdoc />
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    /// <summary>
    /// Reactive: switches on the live own-node stream, then (for the User case) tracks the
    /// LinkedIn credential synced query so "Link LinkedIn account" appears/self-hides live as the
    /// credential is created — no <c>Channel</c> bridge, no one-shot read. Emits an empty slice
    /// for non-applicable nodes so the menu aggregator's <c>CombineLatest</c> never stalls.
    /// </summary>
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        if (string.IsNullOrEmpty(options.Value.ClientId))
            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>([]);

        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService(typeof(AccessService)) as AccessService;
        var viewerId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>([]);

        // Live own-node stream. StartWith(null) so the outer Switch emits before the node loads;
        // Catch degrades to "no node" on hubs without a MeshDataSource.
        var nodeStream = host.Workspace.GetMeshNodeStream()
            .Select(n => (MeshNode?)n)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .StartWith((MeshNode?)null);

        return nodeStream
            .Select(node => BuildItems(host, hubPath, viewerId!, node))
            .Switch();
    }

    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> BuildItems(
        LayoutAreaHost host, string hubPath, string viewerId, MeshNode? node)
    {
        IReadOnlyCollection<NodeMenuItemDefinition> empty = [];
        if (node is null)
            return Observable.Return(empty);

        // Case 1: viewer's own User node.
        if (hubPath.Equals($"User/{viewerId}", StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.NodeType, "User", StringComparison.OrdinalIgnoreCase))
        {
            // Only show "Link LinkedIn" when no credential exists yet. Synced query via
            // workspace.GetQuery — bypasses RLS, gated on Initial, deduped by path. Live, so the
            // item self-hides the moment the credential lands.
            var credentialPath = $"{hubPath}/_ApiCredentials/linkedin";
            return host.Workspace.GetQuery($"linkedin-credential:{credentialPath}", $"path:{credentialPath}")
                .Select(items => items.Any()
                    ? empty
                    : (IReadOnlyCollection<NodeMenuItemDefinition>)
                    [
                        new NodeMenuItemDefinition(
                            Label: "Link LinkedIn account",
                            Area: "ConnectLinkedIn",
                            Icon: "LinkSquare",
                            RequiredPermission: Permission.Update,
                            Order: 60,
                            Href: "/connect/linkedin/me"),
                    ])
                .StartWith(empty);
        }

        // Case 2: an ApiCredential node for LinkedIn.
        if (string.Equals(node.NodeType, PlatformCredential.ApiCredentialNodeType, StringComparison.OrdinalIgnoreCase))
        {
            // ContentAs, never a direct cast: on a hub without the type registered the content
            // degrades to a JsonElement, which the accessor recovers (ObjectAsExtensions).
            var platform = node.ContentAs<PlatformCredential>(host.Hub.JsonSerializerOptions)?.Platform;
            if (!string.Equals(platform, LinkedInPublisher.PlatformId, StringComparison.OrdinalIgnoreCase))
                return Observable.Return(empty);

            // The credential node lives at {userPath}/_ApiCredentials/{platform} — the
            // user path is the grandparent namespace (strip the last two path segments).
            var segments = hubPath.Split('/');
            if (segments.Length < 3)
                return Observable.Return(empty);
            var userPath = string.Join("/", segments.Take(segments.Length - 2));

            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>(
            [
                new NodeMenuItemDefinition(
                    Label: "Download past posts",
                    Area: "PullLinkedInPosts",
                    Icon: "ArrowDownload",
                    RequiredPermission: Permission.Update,
                    Order: 10,
                    Href: $"/connect/linkedin/pull?profile={Uri.EscapeDataString(userPath)}"),
                new NodeMenuItemDefinition(
                    Label: "Re-authorize",
                    Area: "ReAuthorizeLinkedIn",
                    Icon: "ArrowSync",
                    RequiredPermission: Permission.Update,
                    Order: 20,
                    Href: $"/connect/linkedin?profile={Uri.EscapeDataString(userPath)}"),
            ]);
        }

        return Observable.Return(empty);
    }
}
