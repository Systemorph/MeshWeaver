using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Request to create a new MeshNode in the catalog.
/// The node will be created with Transient state until the hub confirms.
/// Permission checked depends on node type: Thread/ThreadMessage → Thread,
/// Comment → Comment, everything else → Create.
/// </summary>
/// <param name="Node">The MeshNode to create</param>
[CreateNodePermission]
public record CreateNodeRequest(MeshNode Node) : IRequest<CreateNodeResponse>
{
    /// <summary>
    /// The user or system requesting the creation.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Optional initialization payload forwarded to the newly-created node's hub
    /// after persistence succeeds. Lets a single CreateNodeRequest atomically
    /// create the node AND queue the first message of work for it — e.g. a
    /// Thread's first <c>ThreadInput.AppendUserInput</c> —
    /// without a second client round-trip. The mesh hub posts <c>Argument</c>
    /// fire-and-forget to the new node's address with the original requester's
    /// AccessContext; the target hub processes it through its normal handler
    /// pipeline (including its own permission check).
    /// </summary>
    public object? Argument { get; init; }
}

/// <summary>
/// Response for node creation request.
/// </summary>
/// <param name="Node">The created node (with updated State) or null if failed</param>
public record CreateNodeResponse(MeshNode? Node)
{
    /// <summary>
    /// Inline <see cref="Data.ActivityLog"/> — creation is synchronous, so by the
    /// time the response lands the activity is complete. Carries validator
    /// decisions, persist outcome, access-control messages.
    /// </summary>
    public ActivityLog? Log { get; init; }

    /// <summary>
    /// Error message if the creation failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the creation was successful.
    /// </summary>
    public bool Success => Error == null && Node != null;

    /// <summary>
    /// The rejection reason if the node was rejected.
    /// </summary>
    public NodeCreationRejectionReason? RejectionReason { get; init; }

    /// <summary>
    /// Creates a successful response with the created node.
    /// </summary>
    public static CreateNodeResponse Ok(MeshNode node) => new(node);

    /// <summary>
    /// Creates a failed response with an error message.
    /// </summary>
    public static CreateNodeResponse Fail(string error, NodeCreationRejectionReason reason = NodeCreationRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node creation request can be rejected.
/// </summary>
public enum NodeCreationRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// A node with the same path already exists.
    /// </summary>
    NodeAlreadyExists,

    /// <summary>
    /// The specified node type is invalid or not found.
    /// </summary>
    InvalidNodeType,

    /// <summary>
    /// The specified path is invalid.
    /// </summary>
    InvalidPath,

    /// <summary>
    /// Node content validation failed.
    /// </summary>
    ValidationFailed
}

/// <summary>
/// Request to delete a MeshNode from the catalog.
/// </summary>
/// <param name="Path">The path of the node to delete</param>
[RequiresPermission(Permission.Delete)]
public record DeleteNodeRequest(string Path) : IRequest<DeleteNodeResponse>
{
    /// <summary>
    /// If true, also delete all descendant nodes.
    /// </summary>
    public bool Recursive { get; init; }

    /// <summary>
    /// If true, also delete satellite nodes (e.g. _Comment, _Activity, _Thread). By default
    /// satellites are preserved so an undo / restore can find prior activity. Set to <c>true</c>
    /// for hard-delete operations such as the Delete step inside a Move.
    /// </summary>
    public bool IncludeSatellites { get; init; }

    /// <summary>
    /// The user or system requesting the deletion.
    /// </summary>
    public string? DeletedBy { get; init; }

    /// <summary>
    /// If true, deletions proceed even when <see cref="ValidateDeleteRequest"/> responses carry
    /// warnings. Without this flag, any warning blocks the delete and is surfaced back to the
    /// caller (as <see cref="NodeDeletionRejectionReason.WarningsRequireConfirmation"/>) so the
    /// UI can render a confirmation dialog. The second call — with this flag set — proceeds.
    /// </summary>
    public bool ConfirmWarnings { get; init; }
}

/// <summary>
/// Response for node deletion request.
/// </summary>
public record DeleteNodeResponse
{
    /// <summary>Inline <see cref="Data.ActivityLog"/> — deletion completes synchronously.</summary>
    public ActivityLog? Log { get; init; }

    /// <summary>
    /// Error message if the deletion failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the deletion was successful.
    /// </summary>
    public bool Success => Error == null;

    /// <summary>
    /// The rejection reason if the node deletion was rejected.
    /// </summary>
    public NodeDeletionRejectionReason? RejectionReason { get; init; }

    /// <summary>
    /// Creates a successful deletion response.
    /// </summary>
    public static DeleteNodeResponse Ok() => new();

    /// <summary>
    /// Creates a failed deletion response with an error message.
    /// </summary>
    public static DeleteNodeResponse Fail(string error, NodeDeletionRejectionReason reason = NodeDeletionRejectionReason.Unknown)
        => new() { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node deletion request can be rejected.
/// </summary>
public enum NodeDeletionRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// The node to delete was not found.
    /// </summary>
    NodeNotFound,

    /// <summary>
    /// The node has children and recursive deletion was not requested.
    /// </summary>
    HasChildren,

    /// <summary>
    /// Deletion validation failed.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// A child node could not be deleted, so the parent was not deleted either.
    /// </summary>
    ChildDeletionFailed,

    /// <summary>
    /// The caller lacks <see cref="Security.Permission.Delete"/> permission on the node (or on
    /// one of its descendants for recursive deletes). The error text lists every path that was
    /// denied so the UI can show exactly what the user cannot delete.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// One or more <see cref="ValidateDeleteRequest"/> responses carried warnings and
    /// <see cref="DeleteNodeRequest.ConfirmWarnings"/> was not set. Caller should surface the
    /// warnings (from <see cref="DeleteNodeResponse.Log"/>) and re-issue the request with
    /// <c>ConfirmWarnings=true</c> to proceed.
    /// </summary>
    WarningsRequireConfirmation
}

/// <summary>
/// Upsert a MeshNode at <see cref="Node"/>'s path. Single global verb that
/// dispatches to the create path when the node is missing and to the update
/// path when it already exists, so callers don't replay the existence dance
/// (delete-then-create races the per-node hub's disposal — this is the
/// designed alternative). Two payload modes:
///
/// <list type="number">
/// <item><b>Full instance</b>: leave <see cref="Patch"/> null. The handler
/// upserts the supplied <see cref="Node"/> as-is — Create on missing,
/// Update on existing. Used by node-copy / move / import flows where the
/// caller has the complete shape.</item>
/// <item><b>JSON Patch</b>: set <see cref="Patch"/>. The handler applies the
/// patch to the existing node (or to <see cref="Node"/> as the seed if the
/// target is missing) and writes the result. Used for incremental edits
/// where multiple writers may race on the same node — log lines, view-count
/// bumps, status-flip patterns.</item>
/// </list>
///
/// <para>Permission resolution is dynamic: missing target → <see cref="Permission.Create"/>
/// is checked; existing target → <see cref="Permission.Update"/> is checked.
/// Both checks run through the standard permission pipeline.</para>
/// </summary>
[CreateOrUpdateNodePermission]
public record CreateOrUpdateNodeRequest(MeshNode Node) : IRequest<CreateOrUpdateNodeResponse>
{
    /// <summary>Optional JSON Patch payload (Json.Patch.JsonPatch) to apply to
    /// the existing node. When null, <see cref="Node"/> is the full instance
    /// to upsert. Typed as <c>object?</c> so the patch type is owned by the
    /// caller's package (Json.Patch.Net) rather than pulling that dependency
    /// into Mesh.Contract — handlers cast on receipt.</summary>
    public object? Patch { get; init; }

    /// <summary>The user or system requesting the upsert.</summary>
    public string? RequestedBy { get; init; }
}

/// <summary>
/// Response for <see cref="CreateOrUpdateNodeRequest"/>.
/// </summary>
/// <param name="Node">The upserted node, or null on failure.</param>
public record CreateOrUpdateNodeResponse(MeshNode? Node)
{
    /// <summary>Activity log for the upsert.</summary>
    public ActivityLog? Log { get; init; }

    /// <summary>Error message if the upsert failed.</summary>
    public string? Error { get; init; }

    /// <summary>True when the upsert succeeded.</summary>
    public bool Success => Error == null && Node != null;

    /// <summary>True when the node was newly created; false when it already
    /// existed and was updated. Undefined when <see cref="Success"/> is false.</summary>
    public bool WasCreated { get; init; }

    /// <summary>Rejection reason if the upsert failed.</summary>
    public NodeUpsertRejectionReason? RejectionReason { get; init; }

    /// <summary>Success response for a newly-created node.</summary>
    public static CreateOrUpdateNodeResponse Created(MeshNode node, ActivityLog? log = null)
        => new(node) { WasCreated = true, Log = log };

    /// <summary>Success response for a node that already existed and was updated.</summary>
    public static CreateOrUpdateNodeResponse Updated(MeshNode node, ActivityLog? log = null)
        => new(node) { WasCreated = false, Log = log };

    /// <summary>Failure response with an explanatory message and rejection reason.</summary>
    public static CreateOrUpdateNodeResponse Fail(string error,
        NodeUpsertRejectionReason reason = NodeUpsertRejectionReason.Unknown,
        ActivityLog? log = null)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason, Log = log };
}

/// <summary>Rejection reasons for <see cref="CreateOrUpdateNodeRequest"/>.</summary>
public enum NodeUpsertRejectionReason
{
    /// <summary>Unknown / unclassified rejection.</summary>
    Unknown,
    /// <summary>The supplied node Path is invalid (empty, malformed, or otherwise unroutable).</summary>
    InvalidPath,
    /// <summary>The supplied <c>NodeType</c> is unknown or not allowed at this path.</summary>
    InvalidNodeType,
    /// <summary>A registered <c>INodeValidator</c> rejected the create/update.</summary>
    ValidationFailed,
    /// <summary>The caller does not have permission to create or update at this path.</summary>
    Unauthorized,
    /// <summary>The JSON Patch on an existing node failed to apply (e.g. test operation mismatch).</summary>
    PatchFailed,
}

/// <summary>
/// Permission attribute for <see cref="CreateOrUpdateNodeRequest"/>. Checks
/// <see cref="Permission.Create"/> on missing targets and <see cref="Permission.Update"/>
/// on existing targets — read by the handler at dispatch time, since the
/// existence answer requires a persistence read.
/// </summary>
public class CreateOrUpdateNodePermissionAttribute() : RequiresPermissionAttribute(Permission.Update)
{
    /// <inheritdoc />
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        // Static check at the routing layer cannot know "exists?" without a
        // persistence read — that's the handler's job. Bound the static check
        // to BOTH Create and Update on hubPath; the handler's actual read +
        // write (CreateNodeRequest for missing, stream.Update for existing)
        // re-checks permissions authoritatively. This static surface is just
        // for the "deny if neither permission" gate at the routing layer.
        yield return (hubPath, Permission.Create);
        yield return (hubPath, Permission.Update);
    }
}

/// <summary>
/// Request to copy a MeshNode (and its subtree) to a new path.
/// By default copies descendants but NOT satellites — explicitly set <see cref="IncludeSatellites"/>
/// to <c>true</c> to also copy satellite subtrees (e.g. for Move which is Copy+Delete).
/// Requires Read permission on the source and Create permission on the target.
/// </summary>
/// <param name="SourcePath">The path to copy from.</param>
/// <param name="TargetPath">The path to copy to.</param>
public record CopyNodeRequest(string SourcePath, string TargetPath) : IRequest<CopyNodeResponse>
{
    /// <summary>If <c>true</c>, copies all descendant nodes (subtree) under the source.</summary>
    public bool IncludeDescendants { get; init; } = true;

    /// <summary>If <c>true</c>, copies satellite nodes (e.g. _Comment, _Activity) attached to the source.</summary>
    public bool IncludeSatellites { get; init; }
}

/// <summary>
/// Response for <see cref="CopyNodeRequest"/>.
/// </summary>
/// <param name="Node">The root node at its new target path, or <c>null</c> if failed.</param>
public record CopyNodeResponse(MeshNode? Node)
{
    /// <summary>Inline activity log for the copy operation.</summary>
    public ActivityLog? Log { get; init; }

    /// <summary>Error message if the copy failed.</summary>
    public string? Error { get; init; }

    /// <summary>True if the copy succeeded.</summary>
    public bool Success => Error == null && Node != null;

    /// <summary>Rejection reason if the copy failed.</summary>
    public NodeCopyRejectionReason? RejectionReason { get; init; }

    /// <summary>Number of descendant nodes copied (excluding the root).</summary>
    public int DescendantsCopied { get; init; }

    /// <summary>Number of satellite nodes copied.</summary>
    public int SatellitesCopied { get; init; }

    /// <summary>Creates a successful copy response.</summary>
    public static CopyNodeResponse Ok(MeshNode node, int descendantsCopied = 0, int satellitesCopied = 0)
        => new(node) { DescendantsCopied = descendantsCopied, SatellitesCopied = satellitesCopied };

    /// <summary>Creates a failed copy response.</summary>
    public static CopyNodeResponse Fail(string error,
        NodeCopyRejectionReason reason = NodeCopyRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a copy request can be rejected.
/// </summary>
public enum NodeCopyRejectionReason
{
    /// <summary>Unknown or unspecified reason.</summary>
    Unknown,
    /// <summary>The source node was not found.</summary>
    SourceNotFound,
    /// <summary>A node already exists at the target path.</summary>
    TargetAlreadyExists,
    /// <summary>The target namespace does not exist.</summary>
    TargetNamespaceNotFound,
    /// <summary>The user does not have permission for the operation.</summary>
    Unauthorized,
    /// <summary>A validation rule rejected the copy.</summary>
    ValidationFailed,
}

/// <summary>
/// Request to move a MeshNode to a new path.
/// Implemented as Copy(IncludeSatellites=true) + DeleteNode(source) — the handler at the
/// mesh hub orchestrates the two operations and posts the response.
/// Requires Delete permission on the source namespace and Create permission on the target namespace.
/// </summary>
/// <param name="SourcePath">The current path of the node</param>
/// <param name="TargetPath">The new path for the node</param>
[MoveNodePermission]
public record MoveNodeRequest(string SourcePath, string TargetPath) : IRequest<MoveNodeResponse>;

/// <summary>
/// Custom permission attribute for MoveNodeRequest.
/// Checks Delete on source namespace and Create on target namespace.
/// </summary>
public class MoveNodePermissionAttribute() : RequiresPermissionAttribute(Permission.Update)
{
    /// <inheritdoc />
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        if (delivery.Message is MoveNodeRequest move)
        {
            yield return (GetNamespace(move.SourcePath), Permission.Delete);
            yield return (GetNamespace(move.TargetPath), Permission.Create);
        }
        else
        {
            yield return (hubPath, Permission.Update);
        }
    }

    private static string GetNamespace(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : path;
    }
}

/// <summary>
/// Response for node move request.
/// </summary>
/// <param name="Node">The moved node at its new path, or null if failed</param>
public record MoveNodeResponse(MeshNode? Node)
{
    /// <summary>Inline <see cref="Data.ActivityLog"/> — move completes synchronously.</summary>
    public ActivityLog? Log { get; init; }

    /// <summary>
    /// Error message if the move failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the move was successful.
    /// </summary>
    public bool Success => Error == null && Node != null;

    /// <summary>
    /// The rejection reason if the move was rejected.
    /// </summary>
    public NodeMoveRejectionReason? RejectionReason { get; init; }

    /// <summary>
    /// Creates a successful move response.
    /// </summary>
    public static MoveNodeResponse Ok(MeshNode node) => new(node);

    /// <summary>
    /// Creates a failed move response with an error message.
    /// </summary>
    public static MoveNodeResponse Fail(string error, NodeMoveRejectionReason reason = NodeMoveRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node move request can be rejected.
/// </summary>
public enum NodeMoveRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// The source node was not found.
    /// </summary>
    SourceNotFound,

    /// <summary>
    /// A node already exists at the target path.
    /// </summary>
    TargetAlreadyExists,

    /// <summary>
    /// Move validation failed.
    /// </summary>
    ValidationFailed
}

/// <summary>
/// Permission attribute for CreateNodeRequest that maps the required permission
/// based on the node type being created. Thread/ThreadMessage nodes require
/// Thread permission; Comment nodes require Comment permission; all others require Create.
/// </summary>
public class CreateNodePermissionAttribute() : RequiresPermissionAttribute(Permission.Create)
{
    /// <summary>
    /// Maps node types to their required permission for creation.
    /// Thread and ThreadMessage → Thread; Comment → Comment; default → Create.
    /// </summary>
    public static Permission GetPermissionForNodeType(string? nodeType)
        => nodeType switch
        {
            "Thread" or "ThreadMessage" => Permission.Thread,
            "Comment" => Permission.Comment,
            "ApiToken" or "ModelProvider" => Permission.Api,
            _ => Permission.Create
        };

    /// <inheritdoc />
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        var permission = delivery.Message is CreateNodeRequest req
            ? GetPermissionForNodeType(req.Node.NodeType)
            : Permission.Create;

        yield return (hubPath, permission);
    }
}
