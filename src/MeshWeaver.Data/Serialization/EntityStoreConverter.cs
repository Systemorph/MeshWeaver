using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using MeshWeaver.Domain;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// <see cref="JsonConverter{T}"/> for <see cref="EntityStore"/>: writes each collection as a property of a
/// JSON object (prefixed with a polymorphic <c>$type</c> discriminator) and reads it back, resolving
/// collection names through the supplied <paramref name="typeRegistry"/>.
/// </summary>
/// <param name="typeRegistry">The type registry used to resolve collection names from entity types.</param>
public class EntityStoreConverter(ITypeRegistry typeRegistry) : JsonConverter<EntityStore>
{
    /// <summary>
    /// Reads an <see cref="EntityStore"/> from JSON.
    /// </summary>
    /// <param name="reader">The reader positioned at the start of the entity store object.</param>
    /// <param name="typeToConvert">The type being converted (always <see cref="EntityStore"/>).</param>
    /// <param name="options">The serializer options to use for nested values.</param>
    /// <returns>The deserialized entity store.</returns>
    public override EntityStore Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode()!, options);
    }

    /// <summary>
    /// Writes an <see cref="EntityStore"/> to JSON.
    /// </summary>
    /// <param name="writer">The writer to emit the entity store to.</param>
    /// <param name="value">The entity store to serialize.</param>
    /// <param name="options">The serializer options to use for nested values.</param>
    public override void Write(Utf8JsonWriter writer, EntityStore value, JsonSerializerOptions options)
    {
        Serialize(value, options).WriteTo(writer);
    }

    private JsonNode Serialize(EntityStore store, JsonSerializerOptions options)
    {
        var ret = new JsonObject();

        // $type MUST be the first property — STJ polymorphic deserializer requires it
        ret["$type"] = typeof(EntityStore).FullName;

        foreach (var x in store.Collections)
        {
            ret[x.Key] = JsonSerializer.SerializeToNode(x.Value, typeof(InstanceCollection), options);
        }

        return ret;
    }



    /// <summary>
    /// Deserializes an <see cref="EntityStore"/> from a parsed JSON node, building one collection per
    /// property (excluding the <c>$type</c> discriminator).
    /// </summary>
    /// <param name="serializedWorkspace">The JSON node holding the serialized entity store; must be a JSON object.</param>
    /// <param name="options">The serializer options to use for nested values.</param>
    /// <returns>The reconstructed entity store.</returns>
    public EntityStore Deserialize(JsonNode serializedWorkspace, JsonSerializerOptions options)
    {
        if (serializedWorkspace is not JsonObject obj)
            throw new ArgumentException("Invalid serialized workspace");

        var newStore =
            new EntityStore()
            {
                Collections = obj.Where(kvp => kvp.Key != "$type").Select(kvp => DeserializeCollection(kvp.Key, kvp.Value!, options)).ToImmutableDictionary(),
                GetCollectionName = valueType => typeRegistry.GetOrAddType(valueType, valueType.Name)
            };

        return newStore;
    }

    private KeyValuePair<string, InstanceCollection> DeserializeCollection(string collection, JsonNode node, JsonSerializerOptions options)
    {
        node[InstanceCollectionConverter.CollectionProperty] = collection;
        return
            new(
                collection,
                node.Deserialize<InstanceCollection>(options) ?? new InstanceCollection()
            );
    }

}
