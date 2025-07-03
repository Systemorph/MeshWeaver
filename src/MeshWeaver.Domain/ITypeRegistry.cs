namespace MeshWeaver.Domain;

public interface ITypeRegistry
{
    ITypeRegistry WithType<TEvent>() => WithType(typeof(TEvent));
    ITypeRegistry WithType<TEvent>(string name) => WithType(typeof(TEvent), name);
    ITypeRegistry WithType(Type type);
    ITypeRegistry WithType(Type type, string typeName);
    KeyFunction? GetKeyFunction(string collection);
    KeyFunction? GetKeyFunction(Type type);
    ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction);
    bool TryGetType(string name, out ITypeDefinition? type);
    Type? GetType(string name);
    bool TryGetCollectionName(Type type, out string? typeName);
    string? GetCollectionName(Type type) => TryGetCollectionName(type, out var typeName) ? typeName : null;
    public ITypeRegistry WithTypesFromAssembly<T>(Func<Type, bool> filter)
        => WithTypesFromAssembly(typeof(T), filter);

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter);
    ITypeRegistry WithTypes(params IEnumerable<Type> types);
    ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string,Type>> types);
    string GetOrAddType(Type valueType, string? defaultName = null);
    ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction> key);
    ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null);
    ITypeDefinition? GetTypeDefinition(string collection);
    
    IEnumerable<KeyValuePair<string, ITypeDefinition>> Types { get; }
}
public record KeyFunction(Func<object, object> Function, Type KeyType);
