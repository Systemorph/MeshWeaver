using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization
{
    /// <summary>
    /// Helpers for serializing domain entities together with the key (identity)
    /// derived from their <see cref="ITypeDefinition"/>, emitting the standard
    /// "$id" and "$type" discriminator properties used across the mesh.
    /// </summary>
    public static class EntitySerializationExtensions
    {
        /// <summary>
        /// JSON property name ("$id") under which an entity's serialized key is written.
        /// </summary>
        public const string IdProperty = "$id";
        /// <summary>
        /// JSON property name ("$type") under which an entity's type discriminator is written.
        /// </summary>
        public const string TypeProperty = "$type";

        /// <summary>
        /// Serializes <paramref name="entity"/> to a JSON object whose first property is "$id"
        /// (the entity's key, computed from <paramref name="typeDefinition"/>) followed by all of
        /// the entity's own serialized properties; any "$id" produced by the entity itself is dropped.
        /// </summary>
        /// <param name="typeDefinition">Type definition used to compute the entity's key.</param>
        /// <param name="entity">The entity instance to serialize.</param>
        /// <param name="options">Serializer options controlling the JSON output.</param>
        /// <returns>A JSON object with "$id" first, followed by the entity's serialized properties.</returns>
        public static JsonObject SerializeEntityAndId(this ITypeDefinition typeDefinition, object entity, JsonSerializerOptions options)
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
