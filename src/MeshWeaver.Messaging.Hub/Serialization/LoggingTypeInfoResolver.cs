using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// A custom JSON type info resolver that filters out properties marked with [PreventLogging] attribute.
/// This resolver wraps an existing resolver and removes properties that should not appear in logs.
/// Note: Entire types marked with [PreventLogging] are excluded from logging at a higher level
/// (in MessageService.ShouldLogMessage) to avoid serialization issues.
/// </summary>
public class LoggingTypeInfoResolver(IJsonTypeInfoResolver innerResolver) : IJsonTypeInfoResolver
{
    private readonly IJsonTypeInfoResolver _innerResolver = innerResolver ?? throw new ArgumentNullException(nameof(innerResolver));

    /// <summary>
    /// Resolves type info via the wrapped resolver, then strips any object properties
    /// annotated with [PreventLogging] so they are omitted from serialized log output.
    /// </summary>
    /// <param name="type">The type whose metadata is requested.</param>
    /// <param name="options">The active serializer options.</param>
    /// <returns>
    /// The (possibly filtered) <see cref="JsonTypeInfo"/> from the inner resolver, or
    /// <c>null</c> if the inner resolver returns none.
    /// </returns>
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = _innerResolver.GetTypeInfo(type, options);

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
