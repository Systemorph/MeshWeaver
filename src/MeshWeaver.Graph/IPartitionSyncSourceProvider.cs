using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// DI seam between the partition administration GUI (<see cref="PartitionSyncAdminLayoutArea"/>,
/// in this assembly) and a sync-source backend living in a HIGHER layer (e.g.
/// <c>MeshWeaver.GitSync</c>'s per-space GitHub sources — GitSync references Graph, so Graph
/// cannot call it directly). Each implementation exposes one KIND of sync source as config
/// MeshNodes: the overview page lists them, binds the standard node-content editor
/// (<see cref="MeshNodeContentEditorControl.ForType"/> over <see cref="ConfigContentType"/>)
/// to each node's path, and adds/removes sources through this contract. Register as a
/// mesh-scoped singleton; the page resolves all implementations and simply hides the
/// section when none are registered.
/// </summary>
public interface IPartitionSyncSourceProvider
{
    /// <summary>Display name of this source kind (e.g. "GitHub").</summary>
    string Kind { get; }

    /// <summary>
    /// The content type of a source's config node — drives the generated editor fields
    /// (<see cref="MeshNodeEditorField.FromType"/>).
    /// </summary>
    Type ConfigContentType { get; }

    /// <summary>
    /// Live stream of the partition's sync-source config nodes. Re-emits when a source is
    /// added, removed, or edited. Emits an empty list (never stalls) when the partition has
    /// no sources.
    /// </summary>
    IObservable<IReadOnlyList<MeshNode>> WatchSyncSources(string partition);

    /// <summary>
    /// One-line summary of a source for list/grid display, e.g.
    /// <c>"owner/repo@main (Bidirectional)"</c> or <c>"not configured"</c>.
    /// </summary>
    string Describe(MeshNode source);

    /// <summary>Whether <paramref name="source"/> may be removed (a provider may protect
    /// its primary/default source).</summary>
    bool CanRemove(string partition, MeshNode source);

    /// <summary>Adds a sync source named <paramref name="name"/> to the partition and emits
    /// its config node. Idempotent on the derived source id.</summary>
    IObservable<MeshNode> AddSyncSource(string partition, string name);

    /// <summary>Removes the sync source backed by <paramref name="source"/>.</summary>
    IObservable<bool> RemoveSyncSource(string partition, MeshNode source);
}
