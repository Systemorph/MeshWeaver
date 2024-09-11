using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization
{
    public static class EntitySerializationExtensions
    {
        public const string IdProperty = "$id";
        public const string TypeProperty = "$type";


        public static JsonObject SerializeEntityAndId(this ITypeDefinition typeDefinition, object entity, JsonSerializerOptions options)
        {
            var serialized = (JsonObject)JsonSerializer.SerializeToNode(entity, options)!;
            serialized[IdProperty] = JsonSerializer.SerializeToNode(typeDefinition.GetKey(entity), options);
            return serialized;
        }

    }
}
