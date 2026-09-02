using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            // an UNregistered type falls back to GetOrAddType → FormatType → the short type name.
            // Probe registration BEFORE the auto-add so we can tell the two apart (generic
            // names legitimately contain '.', so a naive '.'-scan would false-positive).
            var wasRegistered = typeRegistry.TryGetCollectionName(baseType, out var registeredName);
            string typeName;
            if (wasRegistered)
            {
                typeName = registeredName!;
            }
            else if (baseType.Assembly.IsCollectible)
            {
                // 🚨 NEVER auto-register a type from a COLLECTIBLE assembly (dynamic node / kernel-script
                // compilations — every recompile is a NEW assembly with a NEW CLR identity for the "same"
                // class). Adopting such a type into a long-lived hub's registry as a serialization side
                // effect poisons that hub for the rest of the process: every later $type resolution on the
                // hub (e.g. mesh-query row deserialization) then yields THAT foreign/stale CLR type, and a
                // consumer holding its OWN compilation of the class (`Content is T` / `ContentAs<T>`) reads
                // null — the prod "BalanceSheet dashboards render empty" outage (agentic-pensions#12). It
                // also pins the collectible assembly, defeating unload. Format the discriminator WITHOUT
                // registering: THIS serialization still writes the short-name $type; deserialization on
                // this hub politely degrades to JsonElement, which ContentAs<T> recovers at the consumer.
                // Hubs that legitimately OWN the type register it explicitly (WithType / WithContentType /
                // GetTypeDefinition) — those paths are unaffected.
                typeName = typeRegistry is TypeRegistry concreteRegistry
                    ? concreteRegistry.FormatType(baseType)
                    : baseType.Name;
                WarnCollectibleSerialization(baseType, typeName);
            }
            else
            {
                // Automatically register the type in the registry if not already present
                typeName = typeRegistry.GetOrAddType(baseType);
                WarnUnregisteredSerialization(baseType, typeName);
            }
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

                    // Also register in the type registry for consistency — but never adopt a
                    // collectible-assembly type as a side effect (see the guard above).
                    if (!attr.DerivedType.Assembly.IsCollectible)
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

        WarnMissingDerivedRegistrations(baseType, derivedTypes);
        return derivedTypes;
    }

    // (A) Polymorphic subtypes of baseType that EXIST in its assembly but are NOT registered in THIS hub
    // serialise fine here yet DROP to FallBackToNearestAncestor when this hub RECEIVES them, with no other
    // signal (the silent-skin-drop class). Warn once per type (instance dict — no static state) so the
    // missing registration surfaces. Scoped to baseType.Assembly to avoid a full AppDomain scan, and the
    // assembly's type list comes from the per-assembly cache below, so STJ's once-per-type resolution
    // costs one enumeration per ASSEMBLY, not one per base type.
    //
    // 🚨 This is a DIAGNOSTIC running inside GetTypeInfo — i.e. inside every serialization on the hub,
    // including MessageService.ReportFailure's DeliveryFailure post. It must never throw: when it did
    // (Assembly.GetTypes() on a module whose closure lacked Microsoft.Agents.AI threw
    // ReflectionTypeLoadException), the sender of the failed request never learned it had failed —
    // "Failed to post DeliveryFailure message … breaking error cascade". A diagnostic degrades to a
    // diagnostic (GetLoadableTypes + WarnUnloadableTypes), never to a lost delivery.
    private readonly ConcurrentDictionary<Type, byte> warnedMissingDerived = new();
    private void WarnMissingDerivedRegistrations(Type baseType, List<JsonDerivedType> derivedTypes)
    {
        if (logger is null || baseType == typeof(object) || baseType.IsSealed)
            return;
        var present = derivedTypes.Select(d => d.DerivedType).ToHashSet();
        foreach (var t in GetLoadableTypes(baseType.Assembly))
        {
            if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition || t == baseType
                || !baseType.IsAssignableFrom(t) || present.Contains(t)
                || !warnedMissingDerived.TryAdd(t, 0))
                continue;
            logger.LogWarning(
                "Polymorphic subtype {Subtype} of {BaseType} is NOT registered on hub {Hub} — it serialises "
                + "here but DROPS to the nearest ancestor (renders empty) when this hub RECEIVES it. Register "
                + "it via WithType(typeof({Subtype}), nameof({Subtype})) so it round-trips in BOTH the sending "
                + "and receiving hub.",
                t.FullName, baseType.Name, owner ?? "(unknown)", t.Name, t.Name);
        }
    }

    // (B) Loadable types per assembly, computed ONCE per assembly per resolver. Weak-keyed on purpose: the
    // resolver belongs to a long-lived hub, and a strong Assembly → Type[] entry would root every
    // collectible module/dynamic-node assembly whose type was ever serialised here (the recompile-ALC leak
    // TypeRegistry's weak shadow exists to prevent). ConditionalWeakTable keeps the array alive only while
    // the assembly is otherwise reachable. Instance field — dies with the hub's options; never static.
    private readonly ConditionalWeakTable<Assembly, Type[]> loadableTypesByAssembly = new();
    // Warn ONCE per offending assembly (keyed by name — a string never pins the assembly).
    private readonly ConcurrentDictionary<string, byte> warnedUnloadableAssemblies = new();

    private Type[] GetLoadableTypes(Assembly assembly)
    {
        if (loadableTypesByAssembly.TryGetValue(assembly, out var cached))
            return cached;
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // The canonical loader-safe enumeration: keep every type that DID load, report the rest once.
            types = ex.Types.OfType<Type>().ToArray();
            WarnUnloadableTypes(assembly, ex);
        }
        // A concurrent caller may have computed the same array first; either copy is equally valid.
        loadableTypesByAssembly.TryAdd(assembly, types);
        return types;
    }

    /// <summary>
    /// One or more types in <paramref name="assembly"/> could not be loaded — in practice a module whose
    /// dependency closure is missing an assembly (measured: a closure without <c>Microsoft.Agents.AI</c>
    /// on Plugins CI and Reinsurance main). The derived-type scan has already continued with the types
    /// that did load, so the serialization this runs inside is unaffected; what is lost is only the
    /// scan's own coverage of the unloadable types, and THAT is what this warning reports — once per
    /// assembly per hub, naming the assembly and the de-duplicated loader messages, which name the
    /// missing dependency. It is the actionable diagnostic for the closure defect; the closure itself is
    /// fixed where closures are built, never here.
    /// </summary>
    private void WarnUnloadableTypes(Assembly assembly, ReflectionTypeLoadException ex)
    {
        var assemblyName = assembly.FullName ?? assembly.GetName().Name ?? "(unnamed)";
        if (logger is null || !warnedUnloadableAssemblies.TryAdd(assemblyName, 0))
            return;
        var loaderMessages = string.Join("; ",
            ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct());
        logger.LogWarning(
            "{UnloadableCount} of {TotalCount} types in assembly {Assembly} cannot be loaded on hub {Hub}: "
            + "{LoaderMessages}. Serialization continued with the types that did load, but a polymorphic "
            + "subtype among the unloadable ones cannot round-trip here. Fix the module's dependency closure "
            + "so the assembly named in the loader message ships with it.",
            ex.Types.Count(t => t is null), ex.Types.Length, assemblyName, owner ?? "(unknown)", loaderMessages);
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
            "Unregistered type {Type} (de)serialised on hub {Hub} with auto short-name $type='{Discriminator}': "
            + "the hub's TypeRegistry lacks it, so it is auto-registered under its SHORT name. That short name "
            + "resolves on any hub that registered the type under the same short name (the default that cures "
            + "the untyped-JsonElement read), but register it explicitly via WithType(typeof(...), nameof(...)) "
            + "where this hub is configured — explicit registration avoids short-name collisions across "
            + "namespaces and documents the contract.",
            type.FullName, owner ?? "(unknown)", discriminator);
    }

    /// <summary>
    /// A type from a COLLECTIBLE assembly (dynamic node compilation, kernel script) was serialised on a
    /// hub that has not explicitly registered it. The discriminator is formatted but the type is NOT
    /// adopted into the registry — a per-compile CLR identity in a long-lived registry would make every
    /// later $type resolution on this hub yield the foreign/stale type (consumers holding their own
    /// compilation read null) and would pin the collectible assembly. Deduped per type; Debug level —
    /// this is the EXPECTED shape whenever a shared hub relays dynamic-node content it does not own.
    /// </summary>
    private void WarnCollectibleSerialization(Type type, string discriminator)
    {
        if (logger is null || !warnedUnregistered.TryAdd(type, 0))
            return;
        logger.LogDebug(
            "Type {Type} from collectible assembly {Assembly} serialised on hub {Hub} with $type='{Discriminator}' "
            + "WITHOUT registering it: adopting a per-compile CLR identity into this hub's TypeRegistry would make "
            + "every later $type resolution here yield the foreign/stale type (consumers holding their own "
            + "compilation of the class read Content as null) and would pin the collectible assembly. Reads on "
            + "this hub degrade to JsonElement, which ContentAs<T> recovers at the consumer. If this hub OWNS the "
            + "type, register it explicitly via WithType/WithContentType.",
            type.FullName, type.Assembly.GetName().Name, owner ?? "(unknown)", discriminator);
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
