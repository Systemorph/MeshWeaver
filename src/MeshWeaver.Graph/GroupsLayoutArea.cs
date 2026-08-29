using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using Microsoft.Extensions.Logging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for managing group memberships on a mesh node.
/// Inherited memberships are loaded via IMeshService from ancestor nodes (merged per member).
/// Local memberships are rendered via GroupMembershipControlBuilder (reactive).
/// </summary>
public static class GroupsLayoutArea
{

    /// <summary>
    /// Deserializes a <see cref="GroupMembership"/> from a node's content. Public for the same
    /// reason as <c>AccessControlLayoutArea.DeserializeAssignment</c>: the consuming view ships in
    /// the MeshWeaver.Graph.Views module, the content helper stays platform-side.
    /// </summary>
    /// <param name="node">The membership node.</param>
    /// <returns>The membership, or null when the content is absent or unreadable.</returns>
    public static GroupMembership? DeserializeMembership(MeshNode node)
    {
        if (node.Content is GroupMembership gm)
            return gm;
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<GroupMembership>(je.GetRawText());
        return null;
    }
}
