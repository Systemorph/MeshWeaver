using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Persistence;

/// <summary>
/// The well-known address of the per-silo persistence coordinator hub. All
/// writes to durable storage flow through the hub at this address. In Orleans
/// it resolves to the local-silo <c>[StatelessWorker(1)]</c> activation; in
/// monolith it resolves to a singleton hosted hub.
///
/// See <c>Doc/Architecture/PersistencePipeline.md</c> for the full design.
/// </summary>
public static class PersistenceCoordinator
{
    /// <summary>
    /// Address segment for the coordinator. Reserved — application code must not
    /// use this name for other addresses.
    /// </summary>
    public const string AddressType = "_persistence";

    /// <summary>
    /// The per-silo coordinator address. Producers post <see cref="WriteRequest"/>
    /// to this address; routing always finds an activation on the local silo.
    /// </summary>
    public static readonly Address Address = new(AddressType);
}

/// <summary>
/// The kind of write the producer is requesting.
/// </summary>
public enum WriteOp
{
    /// <summary>Create a new node. Fails if the node already exists.</summary>
    Create,

    /// <summary>Update an existing node. The full node payload replaces the previous version.</summary>
    Update,

    /// <summary>Delete the node at <see cref="WriteRequest.Path"/>. Recursive when configured.</summary>
    Delete
}

/// <summary>
/// One write to durable storage. The producer posts this to
/// <see cref="PersistenceCoordinator.Address"/> and walks away — fire-and-forget.
/// The coordinator processes serially, applies retries on transient failures,
/// publishes the result on <see cref="IMeshChangeFeed"/>.
///
/// Consumers that need read-after-write semantics subscribe to the change feed
/// for the path; they do NOT block on a response from this request.
/// </summary>
public record WriteRequest
{
    /// <summary>Which kind of write this is.</summary>
    public required WriteOp Op { get; init; }

    /// <summary>
    /// The node payload. Required for <see cref="WriteOp.Create"/> and
    /// <see cref="WriteOp.Update"/>; null is acceptable for <see cref="WriteOp.Delete"/>
    /// (only <see cref="Path"/> is needed there).
    /// </summary>
    public MeshNode? Node { get; init; }

    /// <summary>
    /// The node path. For Create/Update this MUST equal <c>Node.Path</c>; for
    /// Delete it identifies the target. Set explicitly so the coordinator does
    /// not have to dereference <see cref="Node"/>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Optional caller identity for audit logging + access stamps. The coordinator
    /// applies this as the <c>LastModifiedBy</c> when <see cref="Node.LastModifiedBy"/>
    /// is null.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// For <see cref="WriteOp.Delete"/>: when true, the coordinator removes
    /// descendants too. Default false.
    /// </summary>
    public bool Recursive { get; init; }
}
