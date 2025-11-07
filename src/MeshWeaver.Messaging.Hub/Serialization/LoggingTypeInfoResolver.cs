using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// A custom JSON type info resolver that filters out properties marked with [PreventLogging] attribute.
/// This resolver wraps an existing resolver and removes properties that should not appear in logs.
/// </summary>
public class LoggingTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly IJsonTypeInfoResolver _innerResolver;

    public LoggingTypeInfoResolver(IJsonTypeInfoResolver innerResolver)
    {
        _innerResolver = innerResolver ?? throw new ArgumentNullException(nameof(innerResolver));
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = _innerResolver.GetTypeInfo(type, options);

        // Check if the type itself has [PreventLogging] attribute
        if (type.GetCustomAttribute<PreventLoggingAttribute>(inherit: true) != null)
        {
            // Return null to completely exclude this type from logging
            return null;
        }

        if (typeInfo?.Kind == JsonTypeInfoKind.Object && typeInfo.Properties.Count > 0)
        {
            // Find properties to remove (can't modify during enumeration)
            var propertiesToRemove = typeInfo.Properties
                .Where(ShouldExcludeFromLogging)
                .ToList();

            // Remove properties marked with [PreventLogging]
            foreach (var property in propertiesToRemove)
            {
                typeInfo.Properties.Remove(property);
            }
        }

        return typeInfo;
    }

    private static bool ShouldExcludeFromLogging(JsonPropertyInfo propertyInfo)
    {
        // Check if the underlying property/field has [PreventLogging] attribute
        if (propertyInfo.AttributeProvider is MemberInfo memberInfo)
        {
            return memberInfo.GetCustomAttribute<PreventLoggingAttribute>(inherit: true) != null;
        }

        return false;
    }
}
