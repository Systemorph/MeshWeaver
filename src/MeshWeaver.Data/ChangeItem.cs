using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface IChangeItem
{
    string? ChangedBy { get; }
    ChangeType ChangeType { get; }
    object? Value { get; }

}


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

    public ActivityLog? Log { get; init; }
    public IReadOnlyCollection<EntityUpdate> Updates { get; init; } = Updates ?? [];

    public ChangeItem(
        TStream? Value,
        string? StreamId,
        long Version
    )
        : this(Value, null, StreamId, ChangeType.Full, Version, null)
    { }

}
