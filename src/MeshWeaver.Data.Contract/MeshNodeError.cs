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
    /// diagnostic carries (truncated) raw JSON and the registered type name.
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
    Validation = 6
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
/// a truncated JSON snippet; for <see cref="MeshNodeErrorCode.AccessDenied"/>
/// the missing permission + principal; for <see cref="MeshNodeErrorCode.Conflict"/>
/// the expected/actual version pair. May be null when the code itself is
/// self-describing.
/// </param>
public sealed record MeshNodeError(
    MeshNodeErrorCode Code,
    string Path,
    string Message,
    string? Diagnostic = null);

/// <summary>
/// Consumer-side exception synthesized from a wire <see cref="MeshNodeError"/>.
/// Lives only on the calling silo — never serialized across boundaries.
/// The Blazor layout-area boundary catches this and renders the typed error
/// card (different card per <see cref="MeshNodeError.Code"/>); test asserts
/// match on <see cref="Error"/>.<see cref="MeshNodeError.Code"/>.
/// </summary>
public sealed class MeshNodeStreamException : System.Exception
{
    /// <summary>The structured error this exception was synthesized from.</summary>
    public MeshNodeError Error { get; }

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
