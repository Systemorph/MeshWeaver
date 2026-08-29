using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for importing mesh nodes.
/// Shows a form with destination namespace picker, source type selector
/// (Mesh Node / File / Folder), and the appropriate source input.
/// </summary>
[Browsable(false)]
public static class ImportLayoutArea
{

    /// <summary>
    /// Returns the Import menu item if the user has Create permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Create))
            return null;
        return new("Import", MeshNodeLayoutAreas.ImportMeshNodesArea,
            RequiredPermission: Permission.Create, Order: 1,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.ImportMeshNodesArea))
            { LabelKey = "menu.import" };
    }
}
