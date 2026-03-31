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
    /// All permissions (Read, Create, Update, Delete, Comment, Execute, Thread, Api, Export).
    /// </summary>
    All = Read | Create | Update | Delete | Comment | Execute | Thread | Api | Export
}
