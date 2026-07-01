using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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

    public ITypeRegistry WithType(Type type) => WithType(type, FormatType(type));

    public ITypeRegistry WithType(Type type, string typeName)
    {
        typeName ??= type.FullName!;
        var typeDefinition = new TypeDefinition(type, typeName, keyFunctionBuilder);
        typeByName[typeName] = typeDefinition;
        nameByType[type] = typeName;
        IndexFullNameAlias(type, typeDefinition, typeName);

        return this;
    }

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
        // (renders empty / reactive waits time out — the chat-vanish / atioz storm wedge class). This
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
