using System.Text.Json;
using Json.Patch;
using MeshWeaver.Activities;

namespace MeshWeaver.Data.Serialization;

public interface IChangeItem
{
    object Owner { get; }
    object ChangedBy { get; }
    ChangeType ChangeType { get; }
    object Value { get; }
    object Reference { get; }

    ChangeItem<TReduced> SetValue<TReduced>(
        TReduced reduced, 
        ref TReduced currentValue, 
        object reference,
        JsonSerializerOptions options);
}


public record ChangeItem<TStream>(
    object Owner,
    object Reference,
    TStream Value,
    object ChangedBy,
    ChangeType ChangeType,
    Lazy<JsonPatch> Patch,
    long Version
) : IChangeItem
{
    object IChangeItem.Value => Value;



    public ActivityLog Log { get; init; }

    public ChangeItem<TReduced> SetValue<TReduced>(TReduced reduced, ref TReduced currentValue, object reference, JsonSerializerOptions options)
    {
        var (changeType, patch) = GetChangeTypeAndPatch(reduced, currentValue, options);
        ChangeItem<TReduced> ret = changeType switch
        {
            ChangeType.NoUpdate => null,
            ChangeType.Full => new(Owner, reference, reduced, ChangedBy, ChangeType.Full, null, Version),
            ChangeType.Patch => new(Owner, reference, reduced, ChangedBy, ChangeType.Patch, patch, Version),

            _ => throw new ArgumentException("Unknown change type}"),
        };
        currentValue = reduced;
        return ret;
    }


    private (ChangeType Type, Lazy<JsonPatch> Patch) GetChangeTypeAndPatch<TReduced>(TReduced change, TReduced current, JsonSerializerOptions options)
    {
        if (ChangeType == ChangeType.Full)
            return (ChangeType.Full, null);
        if (change is null)
            return (current is null ? ChangeType.NoUpdate : ChangeType.Full, null);
        if (current is null)
            return (ChangeType.Full, null);
        if (current.Equals(change))
            return (ChangeType.NoUpdate, null);
        if (change is EntityStore store && Patch?.Value is JsonPatch patch)
        {
            return (ChangeType.Patch, new(() => new JsonPatch(patch.Operations.Where(op =>
                store.Collections.ContainsKey(op.Path.Segments.First().Value)))));
        }


        return (ChangeType.Patch, new(() => current.CreatePatch(change)));
    }

}
