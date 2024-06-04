using OpenSmc.Activities;

namespace OpenSmc.Data.Serialization;

public interface IChangeItem
{
    object Address { get; }
    object ChangedBy { get; }
    object Value { get; }
    object Reference { get; }
    ChangeItem<TReduced> SetValue<TReduced>(TReduced value);
}

public record ChangeItem<TStream>(
    object Address,
    object Reference,
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
