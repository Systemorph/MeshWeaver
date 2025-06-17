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
                        UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor
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
        return ComputeDerivedTypes(baseType);
    }
    private List<JsonDerivedType> ComputeDerivedTypes(Type baseType)
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

        // First, check if the base type has JsonPolymorphic and JsonDerivedType attributes
        var jsonPolymorphicAttr = baseType.GetCustomAttributes(typeof(JsonPolymorphicAttribute), false).FirstOrDefault();
        if (jsonPolymorphicAttr != null)
        {
            var jsonDerivedTypeAttrs = baseType.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false)
                .Cast<JsonDerivedTypeAttribute>();

            foreach (var attr in jsonDerivedTypeAttrs)
            {
                // Skip if it's the same type (already added above if applicable)
                if (attr.DerivedType == baseType)
                    continue;

                // Use the type discriminator from the attribute if available, otherwise use the type name
                var typeDiscriminator = attr.TypeDiscriminator?.ToString() ?? attr.DerivedType.FullName;

                // Only add if it's a valid derived type for the base
                if (IsValidDerivedTypeForBase(baseType, attr.DerivedType))
                {
                    derivedTypes.Add(new JsonDerivedType(attr.DerivedType, typeDiscriminator));

                    // Also register in the type registry for consistency
                    typeRegistry.GetOrAddType(attr.DerivedType);
                }
            }
        }

        // Find all derived types from the registry for ANY type
        foreach (var registeredType in typeRegistry.Types)
        {
            var derivedType = registeredType.Value.Type;

            // Skip if it's the same type (already added above if applicable)
            if (derivedType == baseType)
                continue;

            // Skip if we already added this type from JsonDerivedType attributes
            if (derivedTypes.Any(dt => dt.DerivedType == derivedType))
                continue;

            // Check if this registered type inherits from or implements the base type
            if (IsValidDerivedTypeForBase(baseType, derivedType))
            {
                derivedTypes.Add(new JsonDerivedType(derivedType, registeredType.Key));
            }
        }

        return derivedTypes;
    }
    private static bool IsValidDerivedTypeForBase(Type baseType, Type derivedType)
    {
        // For object type, include all registered types that can be serialized polymorphically
        if (baseType == typeof(object))
        {
            return CanBeSerializedPolymorphically(derivedType);
        }

        // Check if the derived type is actually assignable from the base type
        if (!baseType.IsAssignableFrom(derivedType))
            return false;

        // Must not be generic type definition (but allow constructed generic types)
        if (derivedType.IsGenericTypeDefinition)
            return false;

        // Must not be abstract or interface for polymorphic serialization
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
        // Must not be generic type definition (but allow constructed generic types)
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
               type.IsEnum || (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string)) ||
               type.IsValueType || // All structs cannot support polymorphism
               type.IsSealed ||    // Sealed types cannot support polymorphism
               (type.IsGenericType && !type.IsGenericTypeDefinition && !ShouldAllowPolymorphismForGenericType(type)); // Some generic types can support polymorphism
    }
    private static bool ShouldAllowPolymorphismForGenericType(Type type)
    {
        if (!type.IsGenericType)
            return false;

        var genericTypeDefinition = type.GetGenericTypeDefinition();

        // Allow polymorphism for Option<T> types
        if (genericTypeDefinition.FullName == "MeshWeaver.Layout.Option`1")
            return true;

        // Add other generic types that should support polymorphism here as needed

        return false;
    }
}
