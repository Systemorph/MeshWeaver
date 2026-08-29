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
        // Don't show "Pin" on the user's own home page — they don't pin
        // themselves to themselves.
        if (hubPath.Equals(viewerId, StringComparison.OrdinalIgnoreCase))
            return null;
        return new("Pin", PinArea,
            Icon: "Bookmark",
            Order: 50,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, PinArea))
            { LabelKey = "menu.pin" };
    }
}
