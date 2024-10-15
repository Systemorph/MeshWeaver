using System.Text.Json;
using Json.Patch;
using MeshWeaver.Activities;

namespace MeshWeaver.Data;

public interface IChangeItem
{
    object Owner { get; }
    object ChangedBy { get; }
    ChangeType ChangeType { get; }
    object Value { get; }
    object Reference { get; }

}


public record ChangeItem<TStream>(
    object Owner,
    object Reference,
    TStream Value,
    object ChangedBy,
    ChangeType ChangeType,
    long Version
) : IChangeItem
{
    object IChangeItem.Value => Value;

    public ActivityLog Log { get; init; }

    private readonly Lazy<JsonPatch> patch;
    public JsonPatch Patch => patch?.Value;

    public ChangeItem(object Owner,
        object Reference,
        TStream Value,
        object ChangedBy,
        ChangeType ChangeType,
        long Version,
        JsonPatch patch) : this(Owner, Reference, Value, ChangedBy, ChangeType, Version)
    {
        this.patch = new(() => patch);
    }

    internal IReadOnlyCollection<EntityStoreUpdate> Updates { get; }
    public ChangeItem(object Owner,
        object Reference,
        TStream Value,
        object ChangedBy,
        ChangeType ChangeType,
        long Version,
        IReadOnlyCollection<EntityStoreUpdate> updates,
        JsonSerializerOptions options) : this(Owner, Reference, Value, ChangedBy, ChangeType, Version)
    {
        Updates = updates;
        patch = new(() => updates.CreatePatch(options));
    }
    internal ChangeItem(object Owner,
        object Reference,
        TStream Value,
        object ChangedBy,
        ChangeType ChangeType,
        long Version,
        Lazy<JsonPatch> patch,
        IReadOnlyCollection<EntityStoreUpdate> updates) : this(Owner, Reference, Value, ChangedBy, ChangeType, Version)
    {
        Updates = updates;
        this.patch = patch;
    }


}
