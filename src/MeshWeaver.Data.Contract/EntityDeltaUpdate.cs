using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// A minimal-bytes alternative to shipping a full updated entity inside a
/// <see cref="DataChangeRequest"/>. Carries only the recursive
/// <c>StringDeltaPatch</c> (string splices for changed fields, including those
/// buried in nested objects) computed <c>old → new</c>, plus the addressing the
/// owner needs to (a) route the update to the right data source / partition
/// <em>without</em> the full entity in hand and (b) replay the splice onto its
/// CURRENT value to reconstruct the full entity.
///
/// <para>Used for large string content (markdown bodies, prerendered HTML) where
/// re-sending the whole value on every edit is the cost to avoid. A subscriber
/// emits one of these in <see cref="DataChangeRequest.Updates"/>; the owning hub
/// resolves it against its current store before the normal apply. Registered in
/// the TypeRegistry so it survives the cross-hub hop.</para>
/// </summary>
public record EntityDeltaUpdate(string Collection, string Id, RawJson Delta)
{
    /// <summary>
    /// The entity's partition, resolved by the subscriber from the full value, so the
    /// owner can route the update without deserialising the (absent) full entity.
    /// </summary>
    public object? Partition { get; init; }
}
