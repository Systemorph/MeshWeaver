namespace MeshWeaver.Mesh;

/// <summary>
/// How a <see cref="MeshNode"/> participates in static-repo synchronization (import/export).
/// A node carries this so a user can "claim" an imported node — by editing it — and keep the
/// next import from clobbering it. See <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public enum SyncBehavior
{
    /// <summary>
    /// Default. The node is fully synced: import overwrites it from the static repo and export
    /// includes it.
    /// </summary>
    Include = 0,

    /// <summary>
    /// This node is left untouched by sync (import will not overwrite it, export will not include
    /// it), but its children continue to sync normally.
    /// </summary>
    ExcludeThisOnly,

    /// <summary>
    /// This node and its entire descendant subtree are left untouched by sync. Use this to claim
    /// a whole branch (e.g. an edited doc section and everything beneath it).
    /// </summary>
    ExcludeThisAndChildren
}
