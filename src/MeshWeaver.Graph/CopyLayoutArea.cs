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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for copying a node and its subtree to a new location.
/// </summary>
[Browsable(false)]
public static class CopyLayoutArea
{
    /// <summary>
    /// Returns the Copy menu item if the user has Create permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Create))
            return null;
        return new("Copy", MeshNodeLayoutAreas.CopyArea,
            RequiredPermission: Permission.Create, Order: 2,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.CopyArea))
            { LabelKey = "menu.copy" };
    }
}
