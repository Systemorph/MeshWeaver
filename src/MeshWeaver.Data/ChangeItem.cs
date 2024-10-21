using MeshWeaver.Activities;

namespace MeshWeaver.Data;

public interface IChangeItem
{
    string ChangedBy { get; }
    ChangeType ChangeType { get; }
    object Value { get; }

}


public record ChangeItem<TStream>(
    TStream Value,
    string ChangedBy,
    ChangeType ChangeType,
    long Version,
    IReadOnlyCollection<EntityUpdate> Updates
) : IChangeItem
{
    object IChangeItem.Value => Value;

    public ActivityLog Log { get; init; }

    public ChangeItem(
        TStream Value,
        long Version
    )
        :this(Value, null, ChangeType.Full, Version, null)
    { }

}
