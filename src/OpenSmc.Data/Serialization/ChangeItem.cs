using OpenSmc.Activities;

namespace OpenSmc.Data.Serialization;

public interface IChangeItem
{
    object Address { get; }
    object ChangedBy { get; }
    object Value { get; }
    WorkspaceReference Reference { get; }
    ChangeItem<TReduced> SetValue<TReduced>(TReduced value);
}

public record ChangeItem<TStream>(
    object Address,
    WorkspaceReference Reference,
    TStream Value,
    object ChangedBy,
    long Version
) : IChangeItem
{
    object IChangeItem.Value => Value;
    public ChangeItem<TReduced> SetValue<TReduced>(TReduced value) =>
        new(Address, Reference, value, ChangedBy, Version);

    public ActivityLog Log { get; init; }
}
