using System;
using System.Collections.Concurrent;

namespace OpenSmc.Serialization;

public class TypeRegistry(ITypeRegistry parent) : ITypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> typeByName = new();
    private readonly ConcurrentDictionary<Type, string> nameByType = new();

    // TODO V10: Delegate to parent events provider when nothing is defined on this level (16.01.2024, Roland Buergi)

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

    public string GetTypeName(Type type)
    {
        if (nameByType.TryGetValue(type, out var typeName))
            return typeName;

        // ReSharper disable once AssignNullToNotNullAttribute
        typeByName[type.FullName] = type;
        nameByType[type] = type.FullName;
        return type.FullName;
    }

    public string GetOrAddTypeName(Type type)
    {
        if (nameByType.TryGetValue(type, out var typeName))
            return typeName;

        WithType(type);
        return nameByType[type];
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
        if (!mainType.IsGenericType)
            return mainType.FullName ?? mainType.Name;

        var @namespace = mainType.Namespace != null ? mainType.Namespace + "." : "";
        var text = $"{@namespace}{mainType.Name[..mainType.Name.IndexOf('`')]}[{string.Join(',', mainType.GetGenericArguments().Select(FormatType))}]";
        return text;
    }
}