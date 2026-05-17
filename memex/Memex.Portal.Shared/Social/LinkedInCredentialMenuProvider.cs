using System.Linq;
using System.Reactive.Linq;
using System.Threading.Channels;
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

        // Bridge IObservable -> IAsyncEnumerable via Channel per the canonical pattern
        // (UserNodeType.GetGlobalAdminTabAsync). No await on a hub round-trip; the
        // Subscribe is fire-and-forget into the Channel, and the only `await foreach`
        // is on the Channel reader (a synchronous queue, no hub bridge).
        // See Doc/Architecture/AsynchronousCalls.md.
        var node = await ReadNodeOnceAsync(host.Workspace, hubPath);
        if (node is null) yield break;

        // Case 1: viewer's own User node.
        if (hubPath.Equals($"User/{viewerId}", System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.NodeType, "User", System.StringComparison.OrdinalIgnoreCase))
        {
            // Only show "Link LinkedIn" when no credential exists yet. Synced query
            // via workspace.GetQuery — bypasses RLS, gated on Initial, deduped by path.
            var credentialExists = await CredentialExistsAsync(
                host.Workspace, $"{hubPath}/_ApiCredentials/linkedin");

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

    /// <summary>
    /// Channel bridge: subscribe to the workspace's MeshNode stream for
    /// <paramref name="path"/>, push the first emission into a bounded channel,
    /// then read it back via <c>await foreach</c>. No await on a hub round-trip —
    /// the await is on the Channel reader (a synchronous queue), so this body is
    /// safe to call from <c>IAsyncEnumerable</c> on the framework's iteration thread.
    /// </summary>
    private static async System.Threading.Tasks.Task<MeshNode?> ReadNodeOnceAsync(IWorkspace workspace, string path)
    {
        var channel = Channel.CreateBounded<MeshNode?>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var sub = workspace.GetMeshNodeStream(path)
            .Take(1)
            .Subscribe(
                n => { channel.Writer.TryWrite(n); channel.Writer.TryComplete(); },
                _ => channel.Writer.TryComplete(),
                () => channel.Writer.TryComplete());
        await foreach (var item in channel.Reader.ReadAllAsync())
            return item;
        return null;
    }

    /// <summary>
    /// Channel bridge for existence-check via a synced query — same pattern as
    /// <see cref="ReadNodeOnceAsync"/>. <c>workspace.GetQuery</c> runs as System
    /// (bypasses RLS) and is gated on Initial, so the first emission is the
    /// authoritative snapshot.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> CredentialExistsAsync(IWorkspace workspace, string path)
    {
        var channel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var sub = workspace.GetQuery($"linkedin-credential:{path}", $"path:{path}")
            .Take(1)
            .Subscribe(
                items => { channel.Writer.TryWrite(items.Any()); channel.Writer.TryComplete(); },
                _ => channel.Writer.TryComplete(),
                () => channel.Writer.TryComplete());
        await foreach (var item in channel.Reader.ReadAllAsync())
            return item;
        return false;
    }
}
