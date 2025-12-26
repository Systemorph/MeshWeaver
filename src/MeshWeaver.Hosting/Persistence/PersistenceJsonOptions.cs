using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Helper for creating JSON serializer options that exclude [NotMapped] properties.
/// Used by persistence adapters to ensure certain properties are not persisted
/// while still being serializable for mesh communication.
/// </summary>
public static class PersistenceJsonOptions
{
    /// <summary>
    /// Creates a copy of JsonSerializerOptions that excludes [NotMapped] properties.
    /// </summary>
    public static JsonSerializerOptions CreateForPersistence(JsonSerializerOptions source)
    {
        var copy = new JsonSerializerOptions(source)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { ExcludeNotMappedProperties }
            }
        };
        return copy;
    }

    private static void ExcludeNotMappedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var prop in typeInfo.Properties)
        {
            var clrProperty = typeInfo.Type.GetProperty(
                prop.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
            );
            if (clrProperty?.GetCustomAttribute<NotMappedAttribute>() != null)
            {
                prop.ShouldSerialize = (_, _) => false;
            }
        }
    }
}
