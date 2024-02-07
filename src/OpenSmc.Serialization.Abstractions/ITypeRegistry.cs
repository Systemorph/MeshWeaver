using System;

namespace OpenSmc.Serialization;

// TODO V10: Rename to ITypeRegistry? (2023/09/04, Alexander Yolokhov)
public interface ITypeRegistry
{
    ITypeRegistry WithType<TEvent>() => WithType(typeof(TEvent));
    ITypeRegistry WithType(Type type);
    
    bool TryGetType(string name, out Type type);
    bool TryGetTypeName(Type type, out string typeName);
    string GetOrAddTypeName(Type type);

    public ITypeRegistry WithTypesFromAssembly<T>(Func<Type, bool> filter)
        => WithTypesFromAssembly(typeof(T), filter);

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter);
}