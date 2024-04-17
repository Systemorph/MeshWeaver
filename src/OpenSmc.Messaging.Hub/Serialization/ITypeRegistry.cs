namespace OpenSmc.Messaging.Serialization;

public interface ITypeRegistry
{
    ITypeRegistry WithType<TEvent>() => WithType(typeof(TEvent));
    ITypeRegistry WithType(Type type);
    ITypeRegistry WithType(Type type, string typeName);
    ITypeRegistry WithType(Type type, string typeName, Func<object,object> getKey);
    Func<object, object> GetKeyFunction(string collection);
    bool TryGetType(string name, out Type type);
    bool TryGetTypeName(Type type, out string typeName);
    public ITypeRegistry WithTypesFromAssembly<T>(Func<Type, bool> filter)
        => WithTypesFromAssembly(typeof(T), filter);

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter);
    ITypeRegistry WithTypes(IEnumerable<Type> select);
    string GetOrAddTypeName(Type valueType);
}