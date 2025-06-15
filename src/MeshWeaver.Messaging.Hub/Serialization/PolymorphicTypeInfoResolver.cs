using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;


/// <summary>
/// Custom type info resolver that provides polymorphism configuration based on the type registry.
/// </summary>
public class PolymorphicTypeInfoResolver(ITypeRegistry typeRegistry) : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var jsonTypeInfo = base.GetTypeInfo(type, options);        // Only configure polymorphism for supported types that need it
        if (ShouldConfigurePolymorphism(type) && CanConfigurePolymorphism(jsonTypeInfo))
        {
            var derivedTypes = GetDerivedTypes(type);

            // Only configure polymorphism if we have derived types
            if (derivedTypes.Any())
            {
                try
                {
                    var polymorphismOptions = new JsonPolymorphismOptions
                    {
                        TypeDiscriminatorPropertyName = EntitySerializationExtensions.TypeProperty,
                        IgnoreUnrecognizedTypeDiscriminators = true,
                        UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType
                    };

                    foreach (var derivedType in derivedTypes)
                    {
                        polymorphismOptions.DerivedTypes.Add(derivedType);
                    }

                    jsonTypeInfo.PolymorphismOptions = polymorphismOptions;
                }
                catch (InvalidOperationException)
                {
                    // Some types don't support polymorphism configuration, ignore the error
                }
            }
        }

        return jsonTypeInfo;
    }
    private static bool CanConfigurePolymorphism(JsonTypeInfo jsonTypeInfo)
    {
        // Only Object types can have polymorphism configured
        // JsonTypeInfoKind.None will throw InvalidOperationException when setting PolymorphismOptions
        return jsonTypeInfo.Kind == JsonTypeInfoKind.Object;
    }
    private List<JsonDerivedType> GetDerivedTypes(Type baseType)
    {
        var derivedTypes = new List<JsonDerivedType>();

        // For non-abstract, non-interface types, add the type itself as a derived type
        // This ensures all concrete types get $type discriminators
        if (!baseType.IsAbstract && !baseType.IsInterface)
        {
            // Automatically register the type in the registry if not already present
            var typeName = typeRegistry.GetOrAddType(baseType);
            derivedTypes.Add(new JsonDerivedType(baseType, typeName));
        }

        // Also add any actual derived types from the registry
        foreach (var registeredType in typeRegistry.Types)
        {
            var derivedType = registeredType.Value.Type;

            // Skip if it's the same type (already added above if applicable)
            if (derivedType == baseType)
                continue;

            // For object type, include all registered types that can be serialized
            if (baseType == typeof(object))
            {
                if (CanBeSerializedPolymorphically(derivedType))
                {
                    derivedTypes.Add(new JsonDerivedType(derivedType, registeredType.Key));
                }
            }
            // For interfaces and abstract types, check if the registered type implements/inherits from the base type
            else if (baseType.IsInterface || baseType.IsAbstract)
            {
                if (IsValidDerivedTypeForInterface(baseType, derivedType))
                {
                    derivedTypes.Add(new JsonDerivedType(derivedType, registeredType.Key));
                }
            }
            // For other base types, check if it's a valid derived type
            else if (IsValidDerivedType(baseType, derivedType))
            {
                derivedTypes.Add(new JsonDerivedType(derivedType, registeredType.Key));
            }
        }

        return derivedTypes;
    }
    private static bool IsValidDerivedTypeForInterface(Type baseType, Type derivedType)
    {
        // Skip generic type definitions - they cannot be used as derived types in System.Text.Json polymorphism
        if (derivedType.IsGenericTypeDefinition)
        {
            return false;
        }

        // For concrete types, use normal assignability check
        if (baseType.IsAssignableFrom(derivedType))
            return true;

        return false;
    }
    private static bool IsValidDerivedType(Type baseType, Type derivedType)
    {
        // Must be assignable to base type
        if (!baseType.IsAssignableFrom(derivedType))
            return false;

        // Must not be generic type definition
        if (derivedType.IsGenericTypeDefinition)
            return false;

        // Must not be abstract or interface (unless we configure fallback)
        if (derivedType.IsAbstract || derivedType.IsInterface)
            return false;

        // Skip collections (except string)
        if (typeof(IEnumerable).IsAssignableFrom(derivedType) && derivedType != typeof(string))
            return false;

        // Skip types that are known to have custom converters that don't work with polymorphism
        if (HasIncompatibleCustomConverter(derivedType))
            return false;

        return true;
    }
    private static bool HasIncompatibleCustomConverter(Type type)
    {
        // Known types with custom converters that don't support polymorphism metadata
        var incompatibleTypes = new[]
        {
            "MeshWeaver.Messaging.RawJson",
            "System.Text.Json.Nodes.JsonNode",
            "System.Text.Json.Nodes.JsonObject",
            "System.Text.Json.Nodes.JsonArray",
            "System.Text.Json.Nodes.JsonValue"
        };

        return incompatibleTypes.Contains(type.FullName) ||
               type.FullName?.StartsWith("System.Text.Json.Nodes.") == true;
    }

    private static bool CanBeSerializedPolymorphically(Type type)
    {
        // Must not be generic type definition
        if (type.IsGenericTypeDefinition)
            return false;

        // Must not be abstract or interface for polymorphic serialization
        if (type.IsAbstract || type.IsInterface)
            return false;

        // Skip collections (except string)
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return false;

        // Skip types that are known to have custom converters that don't work with polymorphism
        if (HasIncompatibleCustomConverter(type))
            return false;

        // Skip primitive types and other system types that don't need polymorphic handling
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid))
            return false;

        return true;
    }
    private bool ShouldConfigurePolymorphism(Type type)
    {
        // Skip object type - System.Text.Json doesn't handle polymorphism for object well
        // The properties containing objects will be handled by their specific types
        if (type == typeof(object))
            return false;

        // Skip primitive types and other system types that don't need polymorphic handling
        if (IsPrimitiveOrSystemType(type))
            return false;

        // Skip types with custom converters that don't work with polymorphism
        if (HasIncompatibleCustomConverter(type))
            return false;

        // For interfaces and abstract types, only configure polymorphism if we have concrete derived types
        if (type.IsInterface || type.IsAbstract)
        {
            var derivedTypes = GetDerivedTypes(type);
            return derivedTypes.Any();
        }

        // Configure polymorphism for all other non-primitive types to ensure $type discriminators
        return true;
    }

    private static bool IsPrimitiveOrSystemType(Type type)
    {
        return type.IsPrimitive ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) ||
               type == typeof(Guid) ||
               type == typeof(TimeSpan) ||
               type.IsEnum ||
               (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string));
    }
}
