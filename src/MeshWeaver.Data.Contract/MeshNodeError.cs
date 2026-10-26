namespace MeshWeaver.Mesh;

/// <summary>
/// Structured error category for MeshNode read/write failures. Travels on the wire
/// inside a <see cref="MeshNodeError"/> so the consumer can render a typed UI
/// surface (different card per <c>Code</c>) instead of a generic "something went
/// wrong". Exceptions are synthesized from the wire payload only at the consumer
/// boundary (<see cref="MeshNodeStreamException"/>) — never thrown across silos.
/// </summary>
public enum MeshNodeErrorCode
{
    /// <summary>Reserved zero — should never appear on the wire.</summary>
    Unknown = 0,
    /// <summary>
    /// The owning hub rejected the operation because the caller's
    /// <c>AccessContext</c> lacks the required permission. The diagnostic
    /// carries the required permission name and the caller's principal.
    /// </summary>
    AccessDenied = 1,
    /// <summary>
    /// The node's <c>Content</c> JSON could not be materialized to the
    /// registered domain type. Typical cause: the discriminator (<c>$type</c>)
    /// names a type that isn't registered in the consumer hub's TypeRegistry,
    /// or the JSON shape doesn't match the registered type's contract. The
    /// diagnostic carries the content's shape (never its values) and the registered type name.
    /// </summary>
    Deserialization = 2,
    /// <summary>
    /// No node exists at the requested path on the owner. Different from a
    /// timeout: routing succeeded, the owner answered "not present".
    /// </summary>
    NotFound = 3,
    /// <summary>
    /// Optimistic-concurrency conflict — the owner's current version doesn't
    /// match the version the caller diffed against. Diagnostic carries the
    /// caller's expected version and the owner's current version.
    /// </summary>
    Conflict = 4,
    /// <summary>
    /// The owning per-node hub couldn't be reached (no route, activation
    /// failed, message delivery returned failure). Different from
    /// <see cref="NotFound"/>: this is an infrastructure problem, not a
    /// "node doesn't exist" answer.
    /// </summary>
    OwnerUnreachable = 5,
    /// <summary>
    /// The owner accepted the patch but the merged value failed validation
    /// (e.g. missing required field, value-out-of-range). Diagnostic carries
    /// the validation error.
    /// </summary>
    Validation = 6,
    /// <summary>
    /// The owning activation started disposing (idle-recycle / restart) after the
    /// patch was delivered but BEFORE its merge turn ran — the patch was provably
    /// NEVER applied. Unlike <see cref="OwnerUnreachable"/> (routing failed) or a
    /// silent ack loss (owner busy — the patch still applies when the queue drains),
    /// this code is the owner's explicit statement that the write is gone, so the
    /// mirror may safely re-enqueue the SAME update against the fresh activation:
    /// the re-enqueue re-diffs against the freshest state, making it idempotent and
    /// ordering-safe. Together with <see cref="OwnerNotReady"/> these are the only
    /// auto-retried NACK codes; every other code stays terminal.
    /// </summary>
    OwnerDisposing = 7,
    /// <summary>
    /// The owning activation is up but has NOT finished loading its durable state
    /// (activation seed in flight — a slow storage read, a NodeType assembly reload
    /// window) — the patch was provably NEVER applied. 🚨 Deliberately distinct from
    /// <see cref="NotFound"/>: answering "not found" for a node that exists poisons
    /// every existence check built on the read (and the stream cache's negative
    /// cache) — the #667 deploy-window write loss. Same retry contract as
    /// <see cref="OwnerDisposing"/>: the write never happened, so the mirror may
    /// safely re-enqueue the SAME update once the activation has loaded.
    /// </summary>
    OwnerNotReady = 8
}

/// <summary>
/// Wire-serializable error payload for MeshNode read/write failures. Embedded
/// in response messages (e.g. <c>PatchDataResponse.NodeError</c>) so the
/// failure travels across silo / Orleans / SignalR boundaries as a plain
/// record. The consumer-side <c>MeshNodeStreamHandle</c> inspects the
/// response, synthesizes a <see cref="MeshNodeStreamException"/>, and surfaces
/// it via <c>OnError</c> — that's the only place an exception exists.
/// <para>
/// Never throw a raw exception across a silo boundary — it serializes badly
/// (or not at all) and loses the structured <see cref="Code"/> a GUI catch
/// needs to render the right error card.
/// </para>
/// </summary>
/// <param name="Code">Structured category (see <see cref="MeshNodeErrorCode"/>).</param>
/// <param name="Path">MeshNode path the operation targeted.</param>
/// <param name="Message">One-line human-readable summary.</param>
/// <param name="Diagnostic">
/// Code-specific payload: for <see cref="MeshNodeErrorCode.Deserialization"/>
/// the content's shape (<see cref="ContentShapes.Describe"/> — never a value); for <see cref="MeshNodeErrorCode.AccessDenied"/>
/// the missing permission + principal; for <see cref="MeshNodeErrorCode.Conflict"/>
/// the expected/actual version pair. May be null when the code itself is
/// self-describing.
/// </param>
public sealed record MeshNodeError(
    MeshNodeErrorCode Code,
    string Path,
    string Message,
    string? Diagnostic = null)
{
    /// <summary>
    /// 🚨 <b>The owner's fault was a TRANSIENT INFRASTRUCTURE fault</b> — the store could not be
    /// reached (a connection refused, reset or timed out; a name that did not resolve), as
    /// classified on the owner by <see cref="MeshWeaver.Messaging.InfrastructureFault.IsTransient"/> /
    /// <see cref="MeshWeaver.Data.StorageFaults.IsTransientConnectFault"/>, where the exception
    /// still has its type (MeshWeaver#7066).
    ///
    /// <para><b>Why it travels on the wire.</b> The exception never crosses a hub boundary — only
    /// this record does, and the consumer synthesizes a <see cref="MeshNodeStreamException"/> with
    /// no inner exception. So a caller that has to tell "the store was away for a moment" from "the
    /// owner answered with a defect" could only read the MESSAGE text, which is not a contract. The
    /// incident it exists for: a pod's warm-up sweep met
    /// <c>MeshNode Unknown at 'Admin/Build': NpgsqlException: Failed to connect</c> during a
    /// 40-second database restart, could not classify it, treated it as final, and held the pod
    /// unready for 74 minutes after the database was back.</para>
    ///
    /// <para>An init-only property rather than a positional parameter, so the record's primary
    /// constructor keeps its signature (a positional addition is binary-breaking for every module
    /// built against the old shape — <c>scripts/check-record-signatures.py</c>). It says nothing
    /// about the <see cref="Code"/>: a transient fault keeps its code (usually
    /// <see cref="MeshNodeErrorCode.Unknown"/>), and the code's own retry contract is unchanged.
    /// <c>false</c> when the owner did not classify the fault, which is also what an older owner
    /// sends.</para>
    /// </summary>
    public bool Transient { get; init; }
}

/// <summary>
/// Consumer-side exception synthesized from a wire <see cref="MeshNodeError"/>.
/// Lives only on the calling silo — never serialized across boundaries.
/// The Blazor layout-area boundary catches this and renders the typed error
/// card (different card per <see cref="MeshNodeError.Code"/>); test asserts
/// match on <see cref="Error"/>.<see cref="MeshNodeError.Code"/>.
/// </summary>
public sealed class MeshNodeStreamException : System.Exception, MeshWeaver.Messaging.IClassifiedFault
{
    /// <summary>The structured error this exception was synthesized from.</summary>
    public MeshNodeError Error { get; }

    /// <summary>A transient store fault when the OWNER classified it so
    /// (<see cref="MeshNodeError.Transient"/>); otherwise a fault — only the owner's own
    /// classification counts, never the message text.</summary>
    public string ClassifiedKind => Error.Transient
        ? MeshWeaver.Messaging.FaultKind.Transient
        : MeshWeaver.Messaging.FaultKind.Fault;

    /// <summary><see cref="MeshWeaver.Messaging.FaultLayer.OwnerStore"/> for an owner-classified
    /// transient fault.</summary>
    public string? ClassifiedLayer => Error.Transient ? MeshWeaver.Messaging.FaultLayer.OwnerStore : null;

    /// <summary>Creates an exception from the given structured error.</summary>
    /// <param name="error">The structured mesh-node error.</param>
    public MeshNodeStreamException(MeshNodeError error)
        : base($"MeshNode {error.Code} at '{error.Path}': {error.Message}")
    {
        Error = error;
    }

    /// <summary>Creates an exception from the given structured error and inner exception.</summary>
    /// <param name="error">The structured mesh-node error.</param>
    /// <param name="inner">The underlying exception that caused this error.</param>
    public MeshNodeStreamException(MeshNodeError error, System.Exception inner)
        : base($"MeshNode {error.Code} at '{error.Path}': {error.Message}", inner)
    {
        Error = error;
    }
}
