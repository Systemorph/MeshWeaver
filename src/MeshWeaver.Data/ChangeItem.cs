using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Non-generic view over a change emitted by a synchronization stream, exposing the
/// authorship, kind, and (boxed) value of the change without knowing its stream type.
/// </summary>
public interface IChangeItem
{
    /// <summary>Identifier of the principal that authored the change, or <c>null</c> if unattributed.</summary>
    string? ChangedBy { get; }
    /// <summary>Whether the change is a full snapshot or an incremental patch.</summary>
    ChangeType ChangeType { get; }
    /// <summary>The changed state as a boxed object.</summary>
    object? Value { get; }

}


/// <summary>
/// An immutable change carried by a synchronization stream: the new state together with
/// authorship, the originating stream, the kind of change, a monotonic version, and the
/// set of entity-level updates it represents.
/// </summary>
/// <typeparam name="TStream">Type of the state carried by the stream.</typeparam>
/// <param name="Value">The new state after the change. Excluded from logging.</param>
/// <param name="ChangedBy">Identifier of the principal that authored the change, or <c>null</c> if unattributed.</param>
/// <param name="StreamId">Identifier of the stream the change originated from.</param>
/// <param name="ChangeType">Whether the change is a full snapshot or an incremental patch.</param>
/// <param name="Version">Monotonically increasing version of the stream after this change.</param>
/// <param name="Updates">The entity-level updates that make up the change; defaults to empty when <c>null</c>.</param>
public record ChangeItem<TStream>(
    [property: PreventLogging] TStream? Value,
    string? ChangedBy,
    string? StreamId,
    ChangeType ChangeType,
    long Version,
    IReadOnlyCollection<EntityUpdate>? Updates
) : IChangeItem
{
    object? IChangeItem.Value => Value;

    /// <summary>Optional activity log describing how the change was produced.</summary>
    public ActivityLog? Log { get; init; }
    /// <summary>The entity-level updates that make up the change; never <c>null</c> (empty when none).</summary>
    public IReadOnlyCollection<EntityUpdate> Updates { get; init; } = Updates ?? [];

    /// <summary>
    /// Creates a full-snapshot change (ChangeType.Full) with no authorship and no entity updates.
    /// </summary>
    /// <param name="Value">The full state snapshot.</param>
    /// <param name="StreamId">Identifier of the stream the change originated from.</param>
    /// <param name="Version">Monotonically increasing version of the stream after this change.</param>
    public ChangeItem(
        TStream? Value,
        string? StreamId,
        long Version
    )
        : this(Value, null, StreamId, ChangeType.Full, Version, null)
    { }

}
