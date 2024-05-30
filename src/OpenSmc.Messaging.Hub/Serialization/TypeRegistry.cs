using System.Collections.Concurrent;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Serialization;

public class TypeRegistry(ITypeRegistry parent) : ITypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> typeByName = new();
    private readonly ConcurrentDictionary<Type, string> nameByType = new();
    private readonly ConcurrentDictionary<string, Func<object, object>> keysByType = new();

    public ITypeRegistry WithType(Type type) => WithType(type, FormatType(type));

    public ITypeRegistry WithType(Type type, string typeName) => WithType(type, typeName, null);

    public ITypeRegistry WithType(Type type, string typeName, Func<object, object> key)
    {
        typeByName[typeName] = type;
        nameByType[type] = typeName;
        if (key != null)
            keysByType[typeName] = key;
        return this;
    }

    public Func<object, object> GetKeyFunction(string collection) =>
        keysByType.GetValueOrDefault(collection);

    public bool TryGetType(string name, out Type type)
    {
        if (typeByName.TryGetValue(name, out type))
            return true;
        return parent?.TryGetType(name, out type) ?? false;
    }

    public bool TryGetTypeName(Type type, out string typeName) =>
        nameByType.TryGetValue(type, out typeName);

    public string GetOrAddTypeName(Type type)
    {
        if (nameByType.TryGetValue(type, out var typeName))
            return typeName;

        typeName = FormatType(type);
        typeByName[typeName] = type;
        return nameByType[type] = typeName;
    }

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter) =>
        WithTypes(type.Assembly.GetTypes().Where(filter));

    public ITypeRegistry WithTypes(IEnumerable<Type> types)
    {
        foreach (var t in types)
            WithType(t);
        return this;
    }

    public static string FormatType(Type mainType)
        => FormatType(mainType, t => t.GetGenericArguments().Select(FormatType));

    public static string FormatType(Type mainOpenType, Func<Type, IEnumerable<string>> genericArgumentsGetter)
    {
        var mainTypeName = (mainOpenType.FullName ?? mainOpenType.Name).Replace('\u002B', '.');
        if (!mainOpenType.IsGenericType)
            return mainTypeName;

        var text =
            $"{mainTypeName[..mainTypeName.IndexOf('`')]}[{string.Join(',', genericArgumentsGetter(mainOpenType))}]";
        return text;
    }
}
