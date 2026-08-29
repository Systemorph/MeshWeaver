using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The BASE TYPE for the "Stop synchronization" / "Resume synchronization" toggle: the area name
/// and the menu descriptor the platform assembles the node menu from. The VIEW lives in
/// <c>StopSyncViews</c> in the MeshWeaver.Graph.Views module.
///
/// <para>"Stop synchronization" / "Resume synchronization" toggle — flips a node's
/// <see cref="MeshNode.SyncBehavior"/> so the static-repo import leaves it (and its subtree)
/// alone. This is how a user CLAIMS an imported node: once stopped, the next import won't
/// overwrite their edits. See <c>Doc/Architecture/StaticRepoImport.md</c>.</para>
/// </summary>
public static class StopSyncLayoutArea
{
    /// <summary>Area name for the stop/resume-synchronization toggle action.</summary>
    public const string StopSyncArea = "StopSync";

    /// <summary>
    /// Returns the toggle menu item, or null when the caller can't write the node. Shown to
    /// callers with Update — or with <see cref="Permission.Sync"/>, the privileged sync-write
    /// permission, so a read-only catalog node (Agent/Model) can still be claimed by an admin.
    /// The label reflects the node's current state: synced → "Stop synchronization"; excluded →
    /// "Resume synchronization".
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(MeshNode? node, string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Update) && !perms.HasFlag(Permission.Sync))
            return null;
        var excluded = node is { SyncBehavior: not SyncBehavior.Include };
        return new(
            excluded ? "Resume synchronization" : "Stop synchronization",
            StopSyncArea,
            Icon: excluded ? "PlugConnected" : "PlugDisconnected",
            Order: 75,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, StopSyncArea))
            { LabelKey = excluded ? "menu.resumeSync" : "menu.stopSync" };
    }
}
