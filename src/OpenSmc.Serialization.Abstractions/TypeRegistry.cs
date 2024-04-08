using System.Collections.Concurrent;

namespace OpenSmc.Serialization;

public class TypeRegistry(ITypeRegistry parent) : ITypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> typeByName = new();
    private readonly ConcurrentDictionary<Type, string> nameByType = new();

    public ITypeRegistry WithType(Type type)
    {
        var typeName = FormatType(type);
        typeByName[typeName] = type;
        nameByType[type] = typeName;
        return this;
    }

    public bool TryGetType(string name, out Type type)
    {
        if (typeByName.TryGetValue(name, out type))
            return true;
        return parent?.TryGetType(name, out type) ?? false;
    }

    public bool TryGetTypeName(Type type, out string typeName)
        => nameByType.TryGetValue(type, out typeName);
    public string GetOrAddTypeName(Type type)
    {
        if (nameByType.TryGetValue(type, out var typeName))
            return typeName;

        typeByName[type.AssemblyQualifiedName!] = type;
        return nameByType[type] = type.AssemblyQualifiedName;
    }

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter)
        => WithTypes(type.Assembly.GetTypes().Where(filter));

    public ITypeRegistry WithTypes(IEnumerable<Type> types)
    {
        foreach (var t in types)
            WithType(t);
        return this;
    }


    public static string FormatType(Type mainType)
    {
        var mainTypeName = (mainType.FullName ?? mainType.Name).Replace('\u002B', '.');
        if (!mainType.IsGenericType)
            return mainTypeName;

        var text = $"{mainTypeName[..mainTypeName.IndexOf('`')]}[{string.Join(',', mainType.GetGenericArguments().Select(FormatType))}]";
        return text;
    }
}