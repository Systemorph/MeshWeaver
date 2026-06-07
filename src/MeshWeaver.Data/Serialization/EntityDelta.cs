using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Entity-level wiring around <see cref="StringDeltaPatch"/>: turns a full
/// <c>old → new</c> entity change into a compact <see cref="EntityDeltaUpdate"/>
/// (subscriber side) and reconstructs the full entity by replaying the splice onto
/// the owner's CURRENT value (owner side). Serialisation uses the caller's
/// <see cref="JsonSerializerOptions"/> so polymorphic <c>$type</c> discriminators
/// round-trip unchanged (and so drop out of the delta).
/// </summary>
public static class EntityDelta
{
    /// <summary>
    /// SUBSCRIBER: compute the compact delta for one entity. The big string fields
    /// (including nested ones) become splices; everything unchanged is omitted.
    /// </summary>
    public static EntityDeltaUpdate Compute(
        string collection, object id, object? partition,
        object oldValue, object newValue, JsonSerializerOptions options)
    {
        var oldJson = (JsonObject)JsonSerializer.SerializeToNode(oldValue, oldValue.GetType(), options)!;
        var newJson = (JsonObject)JsonSerializer.SerializeToNode(newValue, newValue.GetType(), options)!;
        var delta = StringDeltaPatch.Compute(oldJson, newJson);
        return new EntityDeltaUpdate(collection, id, new RawJson(delta.ToJsonString())) { Partition = partition };
    }

    /// <summary>
    /// OWNER: reconstruct the full entity by replaying the delta onto
    /// <paramref name="currentEntity"/> (the owner's current value). Because the
    /// splice replays onto CURRENT, a disjoint concurrent edit on the owner survives
    /// — the same merge semantics as <see cref="StringDeltaPatch"/>.
    /// </summary>
    public static object Apply(object currentEntity, EntityDeltaUpdate update, JsonSerializerOptions options)
    {
        var currentJson = (JsonObject)JsonSerializer.SerializeToNode(currentEntity, currentEntity.GetType(), options)!;
        var delta = (JsonObject)JsonNode.Parse(update.Delta.Content)!;
        var updatedJson = StringDeltaPatch.Apply(currentJson, delta);
        return updatedJson.Deserialize(currentEntity.GetType(), options)!;
    }
}
