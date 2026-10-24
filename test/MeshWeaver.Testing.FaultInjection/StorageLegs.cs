using System;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// The legs of <see cref="MeshWeaver.Mesh.Services.IStorageAdapter"/> a
/// <see cref="FaultInjectingStorageAdapter.FailOperations"/> fault can be applied to. A closed,
/// test-only set handled exhaustively inside this assembly — never persisted or serialised — so it
/// is an enum, not an open vocabulary.
/// </summary>
[Flags]
public enum StorageLegs
{
    /// <summary>No leg — a fault over it never applies.</summary>
    None = 0,

    /// <summary><c>Read</c>, and a <c>ReadMany</c> whose batch names a matching path (the batch faults as a whole).</summary>
    Read = 1,

    /// <summary><c>Exists</c> and <c>ExistsInWritableStorage</c>.</summary>
    Exists = 2,

    /// <summary><c>Write</c>, <c>WriteIfVersion</c>, and a <c>WriteMany</c> naming a matching path (the batch faults as a whole, writing nothing).</summary>
    Write = 4,

    /// <summary><c>Delete</c>, <c>DeleteIfExists</c>, both compare-and-deletes, and a <c>DeleteMany</c> naming a matching path (faults as a whole, deleting nothing).</summary>
    Delete = 8,

    /// <summary>Subtree enumerations: <c>ListChildPaths</c> (matched on its parent) and <c>ListDescendantPaths</c> (matched on its root).</summary>
    Enumerate = 16,

    /// <summary>Every leg above.</summary>
    All = Read | Exists | Write | Delete | Enumerate,
}
