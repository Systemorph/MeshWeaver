using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization
{
    public static class EntitySerializationExtensions
    {
        public const string IdProperty = "$id";
        public const string TypeProperty = "$type"; public static JsonObject SerializeEntityAndId(this ITypeDefinition typeDefinition, object entity, JsonSerializerOptions options)
        {
            var serialized = (JsonObject)JsonSerializer.SerializeToNode(entity, options)!;
            var result = new JsonObject();

            // Add $id first
            result[IdProperty] = JsonSerializer.SerializeToNode(typeDefinition.GetKey(entity), options);

            // Then add all other properties from the serialized object
            foreach (var kvp in serialized)
            {
                if (kvp.Key != IdProperty) // Avoid duplicating $id if it already exists
                {
                    result[kvp.Key] = kvp.Value?.DeepClone();
                }
            }

            return result;
        }

    }
}
