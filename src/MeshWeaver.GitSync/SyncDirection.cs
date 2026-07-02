namespace MeshWeaver.GitSync;

/// <summary>
/// The direction a sync source is allowed to sync in. A source is the pairing of a Space
/// with one configured repository (<see cref="GitHubSyncConfig"/>); the direction gates
/// which operations <see cref="GitHubSyncService"/> permits against it:
/// export = mesh → repo ("Sync now" commit), import = repo → mesh ("Update to latest" /
/// "Re-import at commit").
/// </summary>
public enum SyncDirection
{
    /// <summary>
    /// Default. Both directions are allowed: the Space can be committed to the repo AND
    /// re-imported from it.
    /// </summary>
    Bidirectional = 0,

    /// <summary>
    /// Unidirectional mesh → repo. The Space exports (commits) to the repo; importing from
    /// the repo is rejected so repo-side edits can never overwrite the mesh.
    /// </summary>
    ExportOnly,

    /// <summary>
    /// Unidirectional repo → mesh. The Space imports (checks out) from the repo; exporting
    /// is rejected so the repo — the source of truth — is never overwritten from the mesh.
    /// </summary>
    ImportOnly,
}
