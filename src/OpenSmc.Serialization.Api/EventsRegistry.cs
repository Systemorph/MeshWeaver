using System.Collections.Concurrent;

namespace OpenSmc.Serialization;

public class EventsRegistry : IEventsRegistry
{
    private readonly ConcurrentDictionary<string, Type> typeByName = new();
    private readonly ConcurrentDictionary<Type, string> nameByType = new();

    public EventsRegistry()
    {
    }

    public IEventsRegistry WithEvent(Type type)
    {
        var typeName = FormatType(type);
        typeByName[typeName] = type;
        nameByType[type] = typeName;
        return this;
    }

    public bool TryGetType(string name, out Type type)
    {
        return typeByName.TryGetValue(name, out type);
    }

    public bool TryGetTypeName(Type type, out string typeName)
    {
        return nameByType.TryGetValue(type, out typeName);
    }

    public string GetOrAddTypeName(Type type)
    {
        if (nameByType.TryGetValue(type, out var typeName))
        {
            return typeName;
        }

        WithEvent(type);
        return nameByType[type];
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