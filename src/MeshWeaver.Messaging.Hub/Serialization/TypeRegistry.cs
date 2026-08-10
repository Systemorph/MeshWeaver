using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

internal class TypeRegistry(ITypeRegistry? parent) : ITypeRegistry
{
    private static readonly Type[] BasicTypes =
    [
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(short),
        typeof(byte),
        typeof(sbyte),
        typeof(uint),
        typeof(ulong),
        typeof(ushort),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(char),
        typeof(bool),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(TimeSpan),
        typeof(Guid),
        typeof(Uri),
        typeof(byte[]),
        typeof(RawJson),
        typeof(Nullable<>),
        typeof(MessageDelivery<>),
        typeof(Address),
        typeof(HeartBeatEvent),
        typeof(DeliveryFailure),
        typeof(DisposeRequest)
    ];

    public IEnumerable<KeyValuePair<string, ITypeDefinition>> Types
    {
        get
        {
            var ret = typeByName.Select(x => new KeyValuePair<string, ITypeDefinition>(x.Key, x.Value));
            if (parent is not null)
                ret = ret.Concat(parent.Types)
                    .DistinctBy(x => x.Key);
            return ret;
        }
    }

    private readonly ConcurrentDictionary<string, TypeDefinition> typeByName =
        new(BasicTypes.Select(t => new KeyValuePair<string, TypeDefinition>(t.Name, new TypeDefinition(t, t.Name, null!))));
    private readonly ConcurrentDictionary<Type, string> nameByType =
        new(BasicTypes.Select(t => new KeyValuePair<Type, string>(t, t.Name)));
    // Resolution-only aliases (full namespace-qualified names) → definition. Consulted by TryGetType so
    // a full-name $type discriminator still resolves on the way IN, but deliberately NOT part of the
    // canonical `typeByName` map that the polymorphic resolver enumerates (PolymorphicTypeInfoResolver
    // → typeRegistry.Types) — otherwise each type would appear TWICE as a JsonDerivedType (short + full)
    // and STJ's discriminator emission breaks ("must specify a type discriminator"). One canonical
    // (short) name per type for OUTPUT; the full name is an INPUT-side alias only.
    private readonly ConcurrentDictionary<string, TypeDefinition> aliasByName = new();

    private readonly KeyFunctionBuilder keyFunctionBuilder = new();

    /// <summary>
    /// Copies the registrations this registry OWNS — its own map, never the inherited ones reachable
    /// through its parent — into <paramref name="target"/>.
    ///
    /// <para>Serves <c>MessageHubConfiguration.WithTypeRegistry</c>: a configuration that adopts a
    /// SHARED registry must not silently drop the types it registered before the swap. Skips every
    /// type the target's own chain already resolves, so an existing registration there — including a
    /// key function attached via <c>WithKeyFunction</c>, which a bare <c>WithType</c> would replace —
    /// is never clobbered. Seeded basic types are therefore a no-op on both sides.</para>
    /// </summary>
    internal void CopyOwnRegistrationsTo(ITypeRegistry target)
    {
        foreach (var (name, definition) in typeByName)
            if (!target.TryGetCollectionName(definition.Type, out _))
                target.WithType(definition.Type, name);
    }

    public ITypeRegistry WithType(Type type) => WithType(type, FormatType(type));

    public ITypeRegistry WithType(Type type, string typeName)
    {
        typeName ??= type.FullName!;
        var typeDefinition = new TypeDefinition(type, typeName, keyFunctionBuilder);
        typeByName[typeName] = typeDefinition;
        nameByType[type] = typeName;
        IndexFullNameAlias(type, typeDefinition, typeName);
        TrackCollectible(type);

        return this;
    }

    // Collectible load contexts this registry currently holds types from, so it subscribes to each
    // one's Unloading exactly once. ConditionalWeakTable and NOT a dictionary: an ALC used as a
    // dictionary KEY is a strong root, which is the very leak this eviction exists to close.
    private readonly ConditionalWeakTable<AssemblyLoadContext, object> trackedContexts = new();

    /// <summary>
    /// A registry entry for a type from a COLLECTIBLE assembly (a dynamically compiled node — every
    /// recompile mints a new assembly with a new CLR identity) strongly roots that assembly's
    /// <see cref="AssemblyLoadContext"/>. Two consequences, exactly as for Autofac's shared
    /// reflection cache (<c>ReflectionCacheEviction</c>, which mirrors this for its own store):
    /// <list type="number">
    /// <item>the context can never be collected — an unbounded leak; and</item>
    /// <item>after the context IS unloaded, anything walking this registry dereferences <b>freed
    /// metadata</b> → <see cref="AccessViolationException"/> / SIGSEGV. That is not hypothetical:
    /// <c>PolymorphicTypeInfoResolver.ComputeDerivedTypes</c> enumerates <see cref="Types"/> and
    /// calls <c>IsAssignableFrom</c> on every entry, and a CI core dump caught it faulting there
    /// while FutuRe's node types sat at recompile v5/v8/v15 (exit=139, no failing test).</item>
    /// </list>
    /// So the registry cleans up after itself: it subscribes to the context's <c>Unloading</c> the
    /// first time it takes a type from it. The event holds a delegate to THIS registry, never the
    /// reverse — the only reference to the context is the weak key below, so nothing here defeats
    /// the collection it enables.
    /// </summary>
    private void TrackCollectible(Type type)
    {
        if (!type.Assembly.IsCollectible)
            return;
        var context = AssemblyLoadContext.GetLoadContext(type.Assembly);
        if (context is null || !context.IsCollectible)
            return;
        // AddOrUpdate would re-subscribe on every registration; Add throws on a duplicate key, so
        // TryAdd-shaped semantics via TryGetValue keeps it to one handler per context.
        if (trackedContexts.TryGetValue(context, out _))
            return;
        trackedContexts.Add(context, Sentinel);
        context.Unloading += EvictLoadContext;
    }

    private static readonly object Sentinel = new();

    /// <summary>
    /// Drops every entry whose type came from <paramref name="context"/>. Runs from the context's
    /// <c>Unloading</c> event — i.e. BEFORE the metadata is freed, while <c>type.Assembly</c> is
    /// still safe to read.
    /// </summary>
    public void EvictLoadContext(AssemblyLoadContext context)
    {
        foreach (var (name, definition) in typeByName)
            if (BelongsTo(definition.Type, context))
                typeByName.TryRemove(name, out _);
        foreach (var (name, definition) in aliasByName)
            if (BelongsTo(definition.Type, context))
                aliasByName.TryRemove(name, out _);
        foreach (var (type, _) in nameByType)
            if (BelongsTo(type, context))
                nameByType.TryRemove(type, out _);
        trackedContexts.Remove(context);
        context.Unloading -= EvictLoadContext;
    }

    private static bool BelongsTo(Type type, AssemblyLoadContext context) =>
        type.Assembly.IsCollectible && AssemblyLoadContext.GetLoadContext(type.Assembly) == context;

    // Resolution alias: index the full (namespace-qualified) name alongside the canonical name, so a
    // full-name $type discriminator — persisted data, OR a payload written before the short-name $type
    // default (fb2ee677d) — still RESOLVES on the way IN. The canonical OUTPUT name stays whatever the
    // caller registered (short by default) via nameByType, so new payloads keep serialising short.
    // Collision-safe: full names are unique; TryAdd never clobbers an explicit registration that already
    // owns the key. This is what lets TryGetType("MeshWeaver.Layout.StackControl") and the old full-name
    // generic forms resolve again after the short-name default (LayoutSerializationTest et al.).
    private void IndexFullNameAlias(Type type, TypeDefinition definition, string canonicalName)
    {
        var fullName = (type.FullName ?? type.Name).Replace('+', '.');
        if (fullName != canonicalName)
            aliasByName.TryAdd(fullName, definition);
    }

    public KeyFunction? GetKeyFunction(string collection) =>
        typeByName.GetValueOrDefault(collection)?.Key.Value;

    public KeyFunction? GetKeyFunction(Type type)
    {
        return (TryGetCollectionName(type, out var typeName) && typeName != null
                   ? GetKeyFunction(typeName)
                   : null)
               ?? keyFunctionBuilder.GetKeyFunction(type);
    }

    public bool TryGetType(string name, out ITypeDefinition? typeDefinition)
    {
        // Canonical (short) name first, then the full-name resolution alias (input side only).
        typeDefinition = typeByName.GetValueOrDefault(name) ?? aliasByName.GetValueOrDefault(name);
        if (typeDefinition != null)
            return true;
        // Handle nullable syntax (e.g., "Int32?" -> Nullable<Int32>)
        if (name.EndsWith('?'))
        {
            var underlyingName = name[..^1];
            if (TryGetType(underlyingName, out var underlyingDef) && underlyingDef != null)
            {
                var nullableType = typeof(Nullable<>).MakeGenericType(underlyingDef.Type);
                typeDefinition = new TypeDefinition(nullableType, name, keyFunctionBuilder);
                return true;
            }
            return false;
        }
        if (name.Contains('[') && name.EndsWith(']'))
        {
            var typeName = name.Substring(0, name.IndexOf('['));
            var baseType = GetTypeDefinition(typeName)?.Type;

            // If not found with full name, try without namespace (e.g., "System.Nullable`1" -> "Nullable`1")
            if (baseType == null && typeName.Contains('.'))
            {
                var shortName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                baseType = GetTypeDefinition(shortName)?.Type;
            }

            if (baseType == null)
                return false;

            var genericArgs = name.Substring(
                    name.IndexOf('[') + 1,
                    name.Length - name.IndexOf('[') - 2
                )
                .Split(',');
            var genericTypeArgs = new Type[genericArgs.Length];

            for (var i = 0; i < genericArgs.Length; i++)
            {
                var argName = genericArgs[i].Trim();
                
                // Handle nullable syntax (e.g., "Int32?" -> "System.Nullable`1[Int32]")
                if (argName.EndsWith('?'))
                {
                    var underlyingTypeName = argName.Substring(0, argName.Length - 1);
                    if (TryGetType(underlyingTypeName, out var underlyingType) && underlyingType != null)
                    {
                        genericTypeArgs[i] = typeof(Nullable<>).MakeGenericType(underlyingType.Type);
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (TryGetType(argName, out var genericTypeArg) && genericTypeArg != null)
                {
                    genericTypeArgs[i] = genericTypeArg.Type;
                }
                else
                {
                    return false;
                }
            }
            var type = baseType.MakeGenericType(genericTypeArgs);
            if (nameByType.TryGetValue(type, out typeName))
            {
                typeDefinition = typeByName[typeName];
                return true;
            }
            typeDefinition = new TypeDefinition(type, FormatType(type), keyFunctionBuilder);
            return true;
        }
        return parent?.TryGetType(name, out typeDefinition)
               ?? typeDefinition != null;
    }

    public Type? GetType(string name) => TryGetType(name, out var td) && td != null ? td.Type : null;

    public bool TryGetCollectionName(Type type, out string? typeName)
    {
        if (nameByType.TryGetValue(type, out typeName))
            return true;

        if (type.IsGenericType)
        {
            var genericTypeDefinition = type.GetGenericTypeDefinition();
            var genericArguments = type.GetGenericArguments();
            var genericTypeArguments = new string[genericArguments.Length];
            for (var i = 0; i < genericArguments.Length; i++)
            {
                // For nullable types, use the special formatting (e.g., "Int32?" instead of "System.Nullable`1[Int32]")
                if (genericArguments[i].IsGenericType && genericArguments[i].GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    genericTypeArguments[i] = FormatType(genericArguments[i]);
                }
                else if (!TryGetCollectionName(genericArguments[i], out var genericTypeArgument) || genericTypeArgument == null)
                {
                    return false;
                }
                else
                {
                    genericTypeArguments[i] = genericTypeArgument;
                }
            }
            typeName =
                $"{GetOrAddType(genericTypeDefinition)}[{string.Join(',', genericTypeArguments)}]";
            return true;
        }

        return parent?.TryGetCollectionName(type, out typeName) ?? false;
    }

    public ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string, Type>> types)
        => types.Aggregate((ITypeRegistry)this, (i, kvp) => i.WithType(kvp.Value, kvp.Key));

    public string GetOrAddType(Type type, string? defaultName = null)
    {
        if (nameByType.TryGetValue(type, out var typeName))
            return typeName;

        // Check parent registry for already registered type name
        if (parent?.TryGetCollectionName(type, out var parentTypeName) == true && parentTypeName != null)
            return parentTypeName;

        typeName = defaultName ?? FormatType(type);
        var definition = new TypeDefinition(type, typeName, keyFunctionBuilder);
        typeByName[typeName] = definition;
        IndexFullNameAlias(type, definition, typeName);
        return nameByType[type] = typeName;
    }


    public ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction?> key)
    {
        keyFunctionBuilder.WithKeyFunction(key);
        return this;
    }

    public ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null)
    {
        if (nameByType.TryGetValue(type, out var name))
            return typeByName.GetValueOrDefault(name);
        var ret = parent?.GetTypeDefinition(type, false);
        if (ret != null)
            return ret;

        if (create)
        {
            typeName ??= FormatType(type);
            ret = new TypeDefinition(type, typeName, keyFunctionBuilder);
            typeByName[ret.CollectionName] = (TypeDefinition)ret;
            nameByType[type] = ret.CollectionName;
            IndexFullNameAlias(type, (TypeDefinition)ret, ret.CollectionName);
        }
        return ret;
    }

    public ITypeDefinition? GetTypeDefinition(string typeName)
    {
        var ret = typeByName.GetValueOrDefault(typeName) ?? aliasByName.GetValueOrDefault(typeName);
        if (ret != null)
            return ret;
        return parent?.GetTypeDefinition(typeName);
    }

    public ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction)
    {
        var typeDefinition = typeByName.GetValueOrDefault(collection) ?? (TypeDefinition?)parent?.GetTypeDefinition(collection);
        if (typeDefinition == null)
            throw new ArgumentException($"Type {collection} not found");
        return typeByName[collection] = typeDefinition with { Key = new(() => keyFunction) };
    }

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter) =>
        WithTypes(type.Assembly.GetTypes().Where(filter));

    public ITypeRegistry WithTypes(IEnumerable<Type> types)
    {
        foreach (var t in types)
            WithType(t);
        return this;
    }

    public string FormatType(Type mainType)
    {
        // Check if the type is already registered with a name (e.g., basic types like "Int32")
        if (nameByType.TryGetValue(mainType, out var registeredName))
            return registeredName;

        // 🎯 The $type discriminator defaults to the SHORT type name (type.Name), NOT the
        // namespace-qualified full name. An unregistered type then serialises as e.g. "ThreadViewModel"
        // — which a reading hub that registered the type under its short name (the standard
        // WithType(typeof(T), nameof(T)) shape) RESOLVES — instead of "MeshWeaver.AI.ThreadViewModel",
        // which mismatches the short-name registration → the value comes back as an untyped JsonElement
        // (renders empty / reactive waits time out — the chat-vanish / prod storm wedge class). This
        // cures the whole class at the default. Short-name collisions across namespaces are resolved by
        // registering the colliding types explicitly (full registration is still done on top of this).
        var mainTypeName = (mainType.Name ?? mainType.FullName!).Replace('\u002B', '.');
        if (!mainType.IsGenericType || mainType.IsGenericTypeDefinition)
            return mainTypeName;

        // Handle nullable types specially BEFORE checking parent registry
        var typeDefinition = mainType.GetGenericTypeDefinition();
        if (typeDefinition == typeof(Nullable<>))
            return FormatType(mainType.GetGenericArguments()[0]) + "?";

        // Check parent registry for already registered type name (after nullable handling)
        if (parent?.TryGetCollectionName(mainType, out var parentTypeName) == true && parentTypeName != null)
            return parentTypeName;

        var text =
            $"{GetOrAddType(typeDefinition)}[{string.Join(',', mainType.GetGenericArguments().Select(valueType => GetOrAddType(valueType)))}]";
        return text;
    }
}
