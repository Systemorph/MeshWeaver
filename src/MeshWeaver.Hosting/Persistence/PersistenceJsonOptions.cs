using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Helper for creating JSON serializer options for persistence.
/// Uses the same base configuration as the hub's JsonSerializationOptions,
/// but excludes [NotMapped] properties from serialization.
/// </summary>
public static class PersistenceJsonOptions
{
    /// <summary>
    /// Creates JsonSerializerOptions for persistence with the same base configuration
    /// as the hub's JsonSerializationOptions, excluding [NotMapped] properties.
    /// </summary>
    public static JsonSerializerOptions CreateForPersistence(ITypeRegistry? typeRegistry = null)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReferenceHandler = null,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            IncludeFields = true
        };

        options.Converters.Add(new EnumMemberJsonStringEnumConverter());

        if (typeRegistry != null)
        {
            options.Converters.Add(new ObjectPolymorphicConverter(typeRegistry));
        }

        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { ExcludeNotMappedProperties }
        };

        return options;
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
