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
using MeshWeaver.Social;

namespace Memex.Portal.Shared.Social;

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

        var mesh = host.Hub.ServiceProvider.GetService(typeof(IMeshService)) as IMeshService;
        if (mesh is null) yield break;

        // Load the current node.
        var nodes = await (host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? System.Array.Empty<MeshNode>())
            ?? Observable.Return(System.Array.Empty<MeshNode>()))
            .FirstAsync();
        var node = nodes.FirstOrDefault(n => n.Path == hubPath);
        if (node is null) yield break;

        // Case 1: viewer's own User node.
        if (hubPath.Equals($"User/{viewerId}", System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.NodeType, "User", System.StringComparison.OrdinalIgnoreCase))
        {
            // Only show "Link LinkedIn" when no credential exists yet.
            var credentialExists = false;
            await foreach (var _ in mesh.QueryAsync<MeshNode>(
                $"path:{hubPath}/_ApiCredentials/linkedin"))
            {
                credentialExists = true;
                break;
            }

            if (!credentialExists)
            {
                yield return new NodeMenuItemDefinition(
                    Label: "Link LinkedIn account",
                    Area: "ConnectLinkedIn",
                    Icon: "LinkSquare",
                    RequiredPermission: Permission.Update,
                    Order: 60,
                    Href: "/connect/linkedin/me");
            }
            yield break;
        }

        // Case 2: an ApiCredential node for LinkedIn.
        if (string.Equals(node.NodeType, ApiCredentialNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase))
        {
            var platform = ExtractPlatform(node);
            if (!string.Equals(platform, LinkedInPublisher.PlatformId, System.StringComparison.OrdinalIgnoreCase))
                yield break;

            // The credential node lives at {userPath}/_ApiCredentials/{platform} — the
            // user path is the grandparent namespace (strip the last two path segments).
            var segments = hubPath.Split('/');
            if (segments.Length < 3) yield break;
            var userPath = string.Join("/", segments.Take(segments.Length - 2));

            yield return new NodeMenuItemDefinition(
                Label: "Download past posts",
                Area: "PullLinkedInPosts",
                Icon: "ArrowDownload",
                RequiredPermission: Permission.Update,
                Order: 10,
                Href: $"/connect/linkedin/pull?profile={System.Uri.EscapeDataString(userPath)}");

            yield return new NodeMenuItemDefinition(
                Label: "Re-authorize",
                Area: "ReAuthorizeLinkedIn",
                Icon: "ArrowSync",
                RequiredPermission: Permission.Update,
                Order: 20,
                Href: $"/connect/linkedin?profile={System.Uri.EscapeDataString(userPath)}");
        }
    }

    private static string? ExtractPlatform(MeshNode node)
    {
        if (node.Content is PlatformCredential typed) return typed.Platform;
        if (node.Content is System.Text.Json.JsonElement je
            && je.TryGetProperty("platform", out var p)
            && p.ValueKind == System.Text.Json.JsonValueKind.String)
            return p.GetString();
        return null;
    }
}
