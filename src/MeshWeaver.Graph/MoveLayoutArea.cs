using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for moving a node and its subtree to a new location.
/// </summary>
[Browsable(false)]
public static class MoveLayoutArea
{
    /// <summary>
    /// Returns the Move menu item if the user has Delete permission (move requires delete on source).
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Delete))
            return null;
        return new("Move", MeshNodeLayoutAreas.MoveArea,
            RequiredPermission: Permission.Delete, Order: 3,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.MoveArea))
            { LabelKey = "menu.move" };
    }
}
