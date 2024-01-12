namespace OpenSmc.Serialization;

// TODO V10: Rename to ITypeRegistry? (2023/09/04, Alexander Yolokhov)
public interface IEventsRegistry
{
    IEventsRegistry WithEvent<TEvent>() => WithEvent(typeof(TEvent));
    IEventsRegistry WithEvent(Type type);
    
    bool TryGetType(string name, out Type type);
    bool TryGetTypeName(Type type, out string typeName);
    string GetOrAddTypeName(Type type);
}