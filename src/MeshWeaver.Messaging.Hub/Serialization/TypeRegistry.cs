using System.Collections.Concurrent;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

internal class TypeRegistry(ITypeRegistry parent) : ITypeRegistry
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
        typeof(RawJson)
    ];

    private readonly ConcurrentDictionary<string, TypeDefinition> typeByName =
        new(BasicTypes.Select(t => new KeyValuePair<string, TypeDefinition>(t.Name, new TypeDefinition(t,t.Name, null))));
    private readonly ConcurrentDictionary<Type, string> nameByType =
        new(BasicTypes.Select(t => new KeyValuePair<Type, string>(t, t.Name)));

    private readonly KeyFunctionBuilder keyFunctionBuilder = new();

    public ITypeRegistry WithType(Type type) => WithType(type, FormatType(type));

    public ITypeRegistry WithType(Type type, string typeName)
    {
        var typeDefinition = new TypeDefinition(type, typeName, keyFunctionBuilder);
        typeByName[typeName] = typeDefinition;
        nameByType[type] = typeName;
        return this;
    }

    public KeyFunction GetKeyFunction(string collection) =>
        typeByName.GetValueOrDefault(collection)?.Key.Value;

    public KeyFunction GetKeyFunction(Type type)
    {
        return (TryGetCollectionName(type, out var typeName)
                   ? GetKeyFunction(typeName)
                   : null)
               ?? keyFunctionBuilder.GetKeyFunction(type); 
    }

    public bool TryGetType(string name, out ITypeDefinition typeDefinition)
    {
        typeDefinition = typeByName.GetValueOrDefault(name);
        if (typeDefinition != null)
            return true;
        if (name.Contains('[') && name.EndsWith(']'))
        {
            var typeName = name.Substring(0, name.IndexOf('['));
            var baseType = typeByName.GetValueOrDefault(typeName)?.Type;

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
                if (TryGetType(genericArgs[i].Trim(), out var genericTypeArg))
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

    public Type GetType(string name) => TryGetType(name, out var td) ? td.Type : null;

    public bool TryGetCollectionName(Type type, out string typeName)
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
                if (!TryGetCollectionName(genericArguments[i], out var genericTypeArgument))
                    return false;
                genericTypeArguments[i] = genericTypeArgument;
            }
            typeName =
                $"{FormatType(genericTypeDefinition)}[{string.Join(',', genericTypeArguments)}]";
            return true;
        }

        return parent?.TryGetCollectionName(type, out typeName) ?? false;
    }

    public string GetOrAddType(Type type)
    {
        if (nameByType.TryGetValue(type, out var typeName))
            return typeName;

        typeName = FormatType(type);
        typeByName[typeName] = new(type,typeName, keyFunctionBuilder);
        return nameByType[type] = typeName;
    }


    public ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction> key)
    {
        keyFunctionBuilder.WithKeyFunction(key);
        return this;
    }

    public ITypeDefinition GetTypeDefinition(Type type, bool create)
    {
        if (nameByType.TryGetValue(type, out var name))
            return typeByName.GetValueOrDefault(name);
        var ret = parent?.GetTypeDefinition(type, false);
        if (ret != null)
            return ret;

        if (create)
        {
            ret = new TypeDefinition(type, FormatType(type), keyFunctionBuilder);
            typeByName[ret.CollectionName] = (TypeDefinition)ret;
            nameByType[type] = ret.CollectionName;
        }
        return ret;
    }

    public ITypeDefinition GetTypeDefinition(string typeName)
    {
        var ret = typeByName.GetValueOrDefault(typeName);
        if (ret != null)
            return ret;
        return parent?.GetTypeDefinition(typeName);
    }

    public ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction)
    {
        var typeDefinition = typeByName.GetValueOrDefault(collection);
        if(typeDefinition == null)
            throw new ArgumentException($"Type {collection} not found");
        return typeByName[collection] = typeDefinition with{Key = new(() => keyFunction)};
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
        var mainTypeName = (mainType.FullName ?? mainType.Name).Replace('\u002B', '.');
        if (!mainType.IsGenericType || mainType.IsGenericTypeDefinition)
            return mainTypeName;

        var typeDefinition = mainType.GetGenericTypeDefinition();
        if (typeDefinition == typeof(Nullable<>))
            return FormatType(mainType.GetGenericArguments()[0]) + "?";

        var text =
            $"{GetOrAddType(typeDefinition)}[{string.Join(',', mainType.GetGenericArguments().Select(GetOrAddType))}]";
        return text;
    }
}
