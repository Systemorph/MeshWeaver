using Json.Patch;
using MeshWeaver.Activities;

namespace MeshWeaver.Data.Serialization;

public interface IChangeItem
{
    object Owner { get; }
    object ChangedBy { get; }
    object Value { get; }
    object Reference { get; }
    ChangeItem<TReduced> SetValue<TReduced>(TReduced value);
}


public record ChangeItem<TStream>(
    object Owner,
    object Reference,
    TStream Value,
    object ChangedBy,
    Lazy<JsonPatch> Patch,
    long Version
) : IChangeItem
{
    object IChangeItem.Value => Value;
    public ChangeItem<TReduced> SetValue<TReduced>(TReduced value) =>
        new(Owner,  Reference, value, ChangedBy, Patch, Version);

    public ActivityLog Log { get; init; }
}
