using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Domain;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging.Serialization;


/// <summary>
/// Custom type info resolver that provides polymorphism configuration based on the type registry.
/// </summary>
/// <param name="typeRegistry">The hub's type registry — the source of $type discriminators.</param>
/// <param name="owner">A human-readable identity for the hub these options belong to (its address),
/// used only to attribute the "serialized an unregistered type" warning to the publishing hub.</param>
/// <param name="logger">Optional logger for the unregistered-type diagnostic.</param>
public class PolymorphicTypeInfoResolver(ITypeRegistry typeRegistry, string? owner = null, ILogger? logger = null) : DefaultJsonTypeInfoResolver
{
    // Warn at most once per unregistered type (per hub/options instance) so the diagnostic can never
    // itself become a storm. Instance field — dies with the hub's options; never static.
    private readonly ConcurrentDictionary<Type, byte> warnedUnregistered = new();
    /// <summary>
    /// Resolves the <see cref="JsonTypeInfo"/> for <paramref name="type"/> and, for eligible object
    /// types, augments it with polymorphism options whose derived types are discovered from the type
    /// registry (and any <see cref="JsonPolymorphicAttribute"/>/<see cref="JsonDerivedTypeAttribute"/>),
    /// using the mesh's $type discriminator property.
    /// </summary>
    /// <param name="type">The type to resolve metadata for.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The type info, with polymorphism configured when derived types are available.</returns>
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
    private List<JsonDerivedType> GetDerivedTypes(Type baseType) => 
        ComputeDerivedTypes(baseType);

    private List<JsonDerivedType> ComputeDerivedTypes(Type baseType)
    {
        var derivedTypes = new List<JsonDerivedType>();

        // For non-abstract, non-interface types, add the type itself as a derived type
        // This ensures all concrete types get $type discriminators
        if (!baseType.IsAbstract && !baseType.IsInterface)
        {
            // A type that is registered in THIS hub's registry resolves to its short collection name;
            // an UNregistered type falls back to GetOrAddType → FormatType → the namespace-qualified
            // full name. Probe registration BEFORE the auto-add so we can tell the two apart (generic
            // names legitimately contain '.', so a naive '.'-scan would false-positive).
            var wasRegistered = typeRegistry.TryGetCollectionName(baseType, out _);
            // Automatically register the type in the registry if not already present
            var typeName = typeRegistry.GetOrAddType(baseType);
            if (!wasRegistered)
                WarnUnregisteredSerialization(baseType, typeName);
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
                var typeDiscriminator = attr.TypeDiscriminator?.ToString() ?? attr.DerivedType.FullName!;

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

    /// <summary>
    /// The type is NOT registered in this hub's <see cref="ITypeRegistry"/>, so its <c>$type</c>
    /// discriminator is the namespace-qualified full name instead of a stable short name. A node
    /// (de)serialised this way — when read by another hub that registered the type under its short name
    /// (or not at all) — comes back as an untyped <see cref="JsonElement"/>: every <c>Content is X</c>
    /// soft-cast fails, the value "renders empty", and reactive waits time out (the <c>_Provider/_Policy</c>
    /// storm). The fix is one of two things this warning is meant to make actionable: register the type on
    /// the hub (<c>WithType(typeof(T), nameof(T))</c>), OR serialise the node from a hub that HAS it.
    /// Deduped per type (instance dict, never static) so the diagnostic itself can never storm; this runs
    /// during JsonTypeInfo resolution, which STJ caches per type, so it is at most once-per-type-per-hub.
    /// </summary>
    private void WarnUnregisteredSerialization(Type type, string discriminator)
    {
        if (logger is null || !warnedUnregistered.TryAdd(type, 0))
            return;
        logger.LogWarning(
            "Unregistered type {Type} (de)serialised on hub {Hub} with full-name $type='{Discriminator}': "
            + "this hub's TypeRegistry lacks it, so a hub that registered it under its short name reads it "
            + "back as an untyped JsonElement (renders empty / reactive waits time out). Register it via "
            + "WithType(typeof(...), nameof(...)) where this hub is configured, or serialise it from a hub that has it.",
            type.FullName, owner ?? "(unknown)", discriminator);
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
               type.IsSealed; // Some generic types can support polymorphism
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
