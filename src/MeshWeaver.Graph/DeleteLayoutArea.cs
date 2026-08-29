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

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for deleting a node and its descendants.
/// Shows descendant count and requires typing DELETE to confirm.
/// </summary>
public static class DeleteLayoutArea
{

    /// <summary>
    /// Query parameter (<c>q</c>) selecting the QUERY-SET mode of the Delete area. Its value is one
    /// or more mesh queries (newline-separated, URL-escaped — see <c>DeleteViews.BuildQueryDeleteUrl</c>)
    /// whose combined result set is offered for deletion. This makes <c>/{path}/Delete</c> a CLEAR
    /// URL that can name a whole SET of nodes: an agent whose own delete was refused hands the user
    /// this link, the user reviews exactly what matches, and confirms under their OWN identity —
    /// the server stays the authority on every single path.
    /// </summary>
    public const string QueriesParam = "q";

    /// <summary>
    /// Returns the Delete menu item if the user has Delete permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Delete))
            return null;
        return new("Delete", MeshNodeLayoutAreas.DeleteArea,
            RequiredPermission: Permission.Delete, Order: 100,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.DeleteArea))
            { LabelKey = "menu.delete" };
    }

}
