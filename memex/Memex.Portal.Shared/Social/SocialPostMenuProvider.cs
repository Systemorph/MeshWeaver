using System;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// Adds LinkedIn publish/engagement actions to the node menu of a <b>social-media Post</b> node
/// (content <c>$type == "SocialMediaPost"</c>, platform LinkedIn):
///
///   - "Publish to LinkedIn" — while the post is NOT yet published (no <c>publishedUrn</c>): an
///     <c>Href</c> to <c>GET /linkedin/publish?postPath=…</c>, which publishes via the member's
///     stored credential and writes <c>status</c>/<c>publishedUrn</c>/<c>publishedAt</c> back.
///   - "Refresh engagement" — once published: an <c>Href</c> to <c>GET /linkedin/engagement?postPath=…</c>,
///     which pulls like/comment counts and writes <c>likes</c>/<c>comments</c> back.
///
/// The action is a plain GET navigation (same shape as <see cref="LinkedInCredentialMenuProvider"/>'s
/// "Download past posts") — no hand-rolled HTML, no async hub code. The item self-swaps as the node's
/// <c>publishedUrn</c> lands because the provider composes the live own-node stream. Both require Update
/// permission, which the post owner has by definition.
/// </summary>
public sealed class SocialPostMenuProvider : INodeMenuProvider
{
    public string Context => "Node";

    /// <summary>
    /// Reactive: switches on the live own-node stream so the "Publish" ↔ "Refresh engagement" swap
    /// happens the moment the endpoint writes <c>publishedUrn</c> back — no one-shot read. Emits an
    /// empty slice for non-post nodes so the menu aggregator's <c>CombineLatest</c> never stalls.
    /// </summary>
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetMeshNodeStream()
            .Select(n => (MeshNode?)n)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .StartWith((MeshNode?)null)
            .Select(node => BuildItems(hubPath, node));
    }

    private static IReadOnlyCollection<NodeMenuItemDefinition> BuildItems(string hubPath, MeshNode? node)
    {
        IReadOnlyCollection<NodeMenuItemDefinition> empty = [];
        if (node?.Content is null || !IsLinkedInPost(node))
            return empty;

        var encoded = Uri.EscapeDataString(hubPath);
        var publishedUrn = Prop(node, "publishedUrn");
        var status = Prop(node, "status");
        var isPublished = !string.IsNullOrEmpty(publishedUrn)
            || string.Equals(status, "Published", StringComparison.OrdinalIgnoreCase);

        if (!isPublished)
            return
            [
                new NodeMenuItemDefinition(
                    Label: "Publish to LinkedIn",
                    Area: "PublishLinkedIn",
                    Icon: "Send",
                    RequiredPermission: Permission.Update,
                    Order: 15,
                    Href: $"/linkedin/publish?postPath={encoded}",
                    Tooltip: "Publish this post to LinkedIn on your behalf"),
            ];

        return
        [
            new NodeMenuItemDefinition(
                Label: "Refresh engagement",
                Area: "RefreshLinkedInEngagement",
                Icon: "ArrowSync",
                RequiredPermission: Permission.Update,
                Order: 15,
                Href: $"/linkedin/engagement?postPath={encoded}",
                Tooltip: "Pull the latest like + comment counts from LinkedIn"),
        ];
    }

    private static bool IsLinkedInPost(MeshNode node)
    {
        var type = Prop(node, "$type");
        var isPost = string.Equals(type, "SocialMediaPost", StringComparison.OrdinalIgnoreCase)
            || (node.NodeType?.EndsWith("Post", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isPost)
            return false;
        var platform = Prop(node, "platform");
        return string.IsNullOrEmpty(platform)
            || string.Equals(platform, "LinkedIn", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Prop(MeshNode node, string name)
    {
        if (node.Content is null)
            return null;
        var je = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, node.Content.GetType());
        if (je.ValueKind != JsonValueKind.Object)
            return null;
        if (TryString(je, name, out var v))
            return v;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return TryString(je, pascal, out var v2) ? v2 : null;
    }

    private static bool TryString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var p))
            return false;
        value = p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            _ => null,
        };
        return value is not null;
    }
}
