using System.Reactive;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Unified validator interface for all node operations.
/// Replaces INodeReadValidator, INodeCreationValidator, INodeUpdateValidator, INodeDeletionValidator.
/// </summary>
public interface INodeValidator
{
    /// <summary>
    /// Validates a node operation. Returns an observable that emits exactly one
    /// <see cref="NodeValidationResult"/> and completes — reactive surface, no
    /// <c>await</c>/<c>ToTask</c> in consumers (composes via SelectMany / Concat).
    /// </summary>
    IObservable<NodeValidationResult> Validate(NodeValidationContext context);

    /// <summary>
    /// Operations this validator handles. Empty collection means all operations.
    /// Use this to create validators that only handle specific operations.
    /// </summary>
    IReadOnlyCollection<NodeOperation> SupportedOperations { get; }
}

/// <summary>
/// Marker for <see cref="INodeValidator"/>s whose enforcement is authoritative ONLY on
/// the owning per-node hub — RLS and structural-partition guards, re-checked there via the
/// <c>[RequiresPermission]</c> pipeline and the Create/Delete handlers. The client-side
/// update pipeline (<c>IMeshService.UpdateNode</c>) SKIPS these: running a permission /
/// partition check on the <em>caller's</em> hub is both redundant with the owner's check
/// and unreliable (the caller may not resolve the owner's effective permissions — the
/// cause of the cache-only-write-gate CI flakes). App-integrity validators (version,
/// name, …) do NOT implement this marker and therefore run client-side, so
/// <c>UpdateNode</c> surfaces their rejection before issuing the write.
/// </summary>
public interface IOwnerEnforcedNodeValidator { }

/// <summary>
/// Context for node validation containing all relevant information.
/// </summary>
public record NodeValidationContext
{
    /// <summary>
    /// The operation being validated.
    /// </summary>
    public required NodeOperation Operation { get; init; }

    /// <summary>
    /// The node being operated on.
    /// For Create: the new node being created.
    /// For Read: the node being read.
    /// For Update: the updated node (new state).
    /// For Delete: the node being deleted.
    /// </summary>
    public required MeshNode Node { get; init; }

    /// <summary>
    /// For Update operations, the existing node before modification.
    /// Null for other operations.
    /// </summary>
    public MeshNode? ExistingNode { get; init; }

    /// <summary>
    /// The original request object (CreateNodeRequest, DeleteNodeRequest).
    /// Null for Read operations and for the canonical stream.Update write path
    /// (which carries no request object — RLS runs on the patch via RlsDataValidator).
    /// </summary>
    public object? Request { get; init; }

    /// <summary>
    /// The current user's access context.
    /// May be null for anonymous operations.
    /// </summary>
    public AccessContext? AccessContext { get; init; }

    /// <summary>
    /// For Delete operations, the root path of the cascade this node is being deleted as
    /// part of — the original <c>DeleteNodeRequest.Path</c>. Equal to <see cref="Node"/>'s
    /// path when the node is the delete root; an ancestor path when the node is a descendant
    /// pulled in by a recursive delete; null when unknown (e.g. a standalone
    /// <c>ValidateDeleteRequest</c> with no cascade context). Invariant validators use this
    /// to tell "the whole partition is going away" apart from "this single node is being
    /// removed while its partition stays".
    /// </summary>
    public string? DeleteCascadeRootPath { get; init; }
}

/// <summary>
/// Unified validation result for all node operations. A result carries either an error
/// (<see cref="IsValid"/>=false + <see cref="ErrorMessage"/>) or an advisory warning
/// (<see cref="IsValid"/>=true + non-null <see cref="Warning"/>) — the latter is used
/// by delete handlers to require explicit user confirmation via
/// <c>DeleteNodeRequest.ConfirmWarnings</c>.
/// </summary>
public record NodeValidationResult(bool IsValid, string? ErrorMessage = null, NodeRejectionReason Reason = NodeRejectionReason.Unknown)
{
    /// <summary>
    /// Advisory message from a validator that accepts the operation but wants the caller
    /// to be aware of a condition (e.g., "the subtree contains 50 nodes — proceed?").
    /// Only meaningful when <see cref="IsValid"/> is true. Delete handlers gate warnings
    /// behind <c>DeleteNodeRequest.ConfirmWarnings</c> — first request returns the
    /// warnings so the UI can confirm, second request (with flag set) proceeds.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static NodeValidationResult Valid() => new(true);

    /// <summary>
    /// Creates a successful validation result carrying an advisory warning.
    /// </summary>
    public static NodeValidationResult ValidWithWarning(string warning) =>
        new(true) { Warning = warning };

    /// <summary>
    /// Creates a failed validation result with an error message.
    /// </summary>
    public static NodeValidationResult Invalid(string error, NodeRejectionReason reason = NodeRejectionReason.ValidationFailed)
        => new(false, error, reason);

    /// <summary>
    /// Creates an unauthorized validation result.
    /// </summary>
    public static NodeValidationResult Unauthorized(string? message = null)
        => new(false, message ?? "Access denied", NodeRejectionReason.Unauthorized);

    /// <summary>
    /// Creates a node not found validation result.
    /// </summary>
    public static NodeValidationResult NotFound(string? path = null)
        => new(false, path != null ? $"Node not found at path: {path}" : "Node not found", NodeRejectionReason.NodeNotFound);
}

/// <summary>
/// Per-node-type access rule that replaces the standard RLS permission check.
/// Register via DI to provide custom access logic for specific node types.
/// When RlsNodeValidator encounters a node whose type has a registered access rule,
/// it delegates to the rule instead of checking AccessAssignment permissions.
/// </summary>
public interface INodeTypeAccessRule
{
    /// <summary>
    /// The node type this rule applies to (e.g. "VUser").
    /// </summary>
    string NodeType { get; }

    /// <summary>
    /// Operations this rule handles. Must be a subset of the RLS validator's operations.
    /// </summary>
    IReadOnlyCollection<NodeOperation> SupportedOperations { get; }

    /// <summary>
    /// Checks whether the given user/context has access for the operation.
    /// Returns an observable that emits <c>true</c> to allow / <c>false</c> to deny.
    /// Reactive surface — composes with the rest of the data-layer chain without
    /// awaiting hub round-trips.
    /// </summary>
    IObservable<bool> HasAccess(NodeValidationContext context, string? userId);
}

/// <summary>
/// Unified rejection reasons for all node operations.
/// </summary>
public enum NodeRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// General validation failure.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// User is not authorized to perform the operation.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The requested node was not found.
    /// </summary>
    NodeNotFound,

    /// <summary>
    /// A node already exists at the specified path.
    /// </summary>
    NodeAlreadyExists,

    /// <summary>
    /// The specified NodeType is invalid or not registered.
    /// </summary>
    InvalidNodeType,

    /// <summary>
    /// The node path is invalid.
    /// </summary>
    InvalidPath,

    /// <summary>
    /// Cannot delete a node that has children (when recursive=false).
    /// </summary>
    HasChildren,

    /// <summary>
    /// Concurrent modification conflict.
    /// </summary>
    ConcurrencyConflict,

    /// <summary>
    /// The node is hidden from the user.
    /// </summary>
    NodeHidden
}

/// <summary>
/// Post-creation handler invoked after a node is successfully persisted.
/// Used for side effects like granting initial access to the creator.
/// Register via DI as a singleton; matched by NodeType.
/// </summary>
public interface INodePostCreationHandler
{
    /// <summary>
    /// The node type this handler applies to (e.g. "Organization").
    /// </summary>
    string NodeType { get; }

    /// <summary>
    /// Executes after the node has been saved to persistence. Reactive — returns
    /// <see cref="IObservable{T}"/> (never Task); compose any mesh writes with the reactive
    /// primitives. Failures are logged; whether they also FAIL the create is controlled by
    /// <see cref="FailsCreateOnError"/> (default best-effort).
    /// </summary>
    /// <param name="createdNode">The persisted node</param>
    /// <param name="createdBy">The ObjectId of the creating user (may be null)</param>
    IObservable<Unit> Handle(MeshNode createdNode, string? createdBy);

    /// <summary>
    /// When <c>true</c>, a failure of <see cref="Handle"/> FAULTS the create — the
    /// <c>CreateNodeResponse</c> comes back as a failure instead of a silent Ok. Use it when the
    /// side effect is a REQUIRED part of the create's contract: e.g. granting the creator
    /// ownership of a brand-new partition — a Space that "created successfully" but left its
    /// creator without the owner <c>AccessAssignment</c> is a broken, un-navigable node, and
    /// swallowing that error (the old <c>.Catch(Observable.Empty)</c>) is exactly the band-aid
    /// that shipped ownerless spaces. Default <c>false</c> keeps best-effort seeds
    /// (composer/settings defaults) log-and-continue. Only <see cref="Handle"/> is gated;
    /// <see cref="GetAdditionalNodes"/> writes stay best-effort regardless.
    /// </summary>
    bool FailsCreateOnError => false;

    /// <summary>
    /// Returns additional nodes that should be created as side effects of the primary node creation.
    /// These are persisted directly (bypassing the hub message pipeline) to avoid deadlocks.
    /// Default implementation returns empty.
    /// </summary>
    /// <param name="createdNode">The persisted node</param>
    /// <returns>Additional nodes to persist</returns>
    IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode) => [];
}
