﻿namespace MeshWeaver.Domain;

public interface ITypeRegistry
{
    ITypeRegistry WithType<TEvent>() => WithType(typeof(TEvent));
    ITypeRegistry WithType(Type type);
    ITypeRegistry WithType(Type type, string typeName);
    KeyFunction GetKeyFunction(string collection);
    KeyFunction GetKeyFunction(Type type);
    ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction);
    bool TryGetType(string name, out ITypeDefinition type);
    Type GetType(string name);
    bool TryGetCollectionName(Type type, out string typeName);
    string GetCollectionName(Type type) => TryGetCollectionName(type, out var typeName) ? typeName : null;
    public ITypeRegistry WithTypesFromAssembly<T>(Func<Type, bool> filter)
        => WithTypesFromAssembly(typeof(T), filter);

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter);
    ITypeRegistry WithTypes(IEnumerable<Type> select);
    string GetOrAddType(Type valueType);
    ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction> key);
    ITypeDefinition GetTypeDefinition(Type type, bool create = true);
    ITypeDefinition GetTypeDefinition(string collection);
}
public record KeyFunction(Func<object, object> Function, Type KeyType);