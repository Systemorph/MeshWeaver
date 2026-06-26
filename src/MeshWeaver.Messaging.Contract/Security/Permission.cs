namespace MeshWeaver.Mesh.Security;

/// <summary>
/// CRUD permissions for mesh node operations.
/// Flags allow combining permissions (e.g., Read | Update).
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>
    /// No permissions granted.
    /// </summary>
    None = 0,

    /// <summary>
    /// Permission to read/view nodes.
    /// </summary>
    Read = 1,

    /// <summary>
    /// Permission to create new nodes.
    /// </summary>
    Create = 2,

    /// <summary>
    /// Permission to update existing nodes.
    /// </summary>
    Update = 4,

    /// <summary>
    /// Permission to delete nodes.
    /// </summary>
    Delete = 8,

    /// <summary>
    /// Permission to create comments and reply to threads.
    /// </summary>
    Comment = 16,

    /// <summary>
    /// Permission to execute code (e.g., launch interactive kernels).
    /// </summary>
    Execute = 32,

    /// <summary>
    /// Permission to create and use threads (chat conversations).
    /// </summary>
    Thread = 64,

    /// <summary>
    /// Permission to access nodes via API tokens (MCP / programmatic access).
    /// Included in all built-in roles by default.
    /// </summary>
    Api = 128,

    /// <summary>
    /// Permission to export nodes (download as files).
    /// Granted to Editor and Admin roles, not to Viewer.
    /// </summary>
    Export = 256,

    /// <summary>
    /// Permission to run static-repo SYNC (import/export) — to <see cref="Permission"/>-overwrite
    /// nodes in a partition that is read-only to ordinary users. Sync is NOT a user write: a
    /// partition whose <c>_Policy</c> denies Create/Update/Delete (e.g. <c>Agent</c>, <c>Model</c>)
    /// still admits a sync overwrite when the caller holds this permission. Granted ONLY by an
    /// explicit sync grant (or the System identity, which bypasses RLS) — deliberately NOT part of
    /// <see cref="All"/>, so an ordinary Admin/Editor does NOT silently gain it and the read-only
    /// <c>_Policy</c> cap (which doesn't strip Sync) can't leak write access to them. Decoupled from
    /// the per-node <c>SyncBehavior</c> content opt-out. See <c>Doc/Architecture/StaticRepoImport.md</c>.
    /// </summary>
    Sync = 512,

    /// <summary>
    /// Permission to CREATE A RELEASE of a NodeType — i.e. kick off the user-facing
    /// "Create Release" operation that compiles a NodeType's source and writes a
    /// <c>Release</c> MeshNode. Granted to <c>Editor</c> (and above) by default so Space
    /// editors can ship releases; absent from <c>Viewer</c>/<c>Commenter</c>.
    /// <para>
    /// Deliberately EXCLUDED from <see cref="All"/> (same reasoning as <see cref="Sync"/>):
    /// <c>HubPermissionExtensions.IsGlobalAdmin</c> is <c>HasFlag(All)</c>, and a
    /// read-only-capped Admin's effective set is folded against the role int — folding a new
    /// bit into <c>All</c> would silently demand it be re-materialized into the PG
    /// <c>user_effective_permissions</c> table before any admin check passes again (the
    /// 2026-06-08 lock-out shape). Granting <c>Compile</c> explicitly on the built-in roles
    /// keeps <c>All</c> — and therefore every <c>HasFlag(All)</c> gate — byte-stable.
    /// </para>
    /// <para>
    /// This gates the USER's request to create a release; the subsequent "pure" compilation
    /// that fills the assembly cache is INFRASTRUCTURE and runs under the System identity
    /// (<c>accessService.ImpersonateAsSystem()</c>), never the caller — so it succeeds even on
    /// a read-only partition (e.g. <c>Doc</c>) where the caller has no Update right.
    /// </para>
    /// </summary>
    Compile = 1024,

    /// <summary>
    /// All standard permissions (Read, Create, Update, Delete, Comment, Execute, Thread, Api,
    /// Export). NOTE: <see cref="Sync"/> and <see cref="Compile"/> are intentionally excluded —
    /// each is a privileged grant added explicitly to the roles that need it, never implied by
    /// "all", so a read-only-capped Admin can't write and <c>HasFlag(All)</c> stays byte-stable.
    /// </summary>
    All = Read | Create | Update | Delete | Comment | Execute | Thread | Api | Export
}
