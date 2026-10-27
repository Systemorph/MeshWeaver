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
    /// Returns the Move menu item if the user may delete the node from where it is — a move is a
    /// delete at the source plus a create at the target.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        // The same demand the server makes (DeletePermission.RequiredFor): Update for ordinary
        // content, Delete for a partition root or a governance satellite.
        var required = DeletePermission.RequiredFor(hubPath);
        if (!perms.HasFlag(required))
            return null;
        return new("Move", MeshNodeLayoutAreas.MoveArea,
            RequiredPermission: required, Order: 3,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.MoveArea))
            { LabelKey = "menu.move" };
    }
}
