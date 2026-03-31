using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Domain;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Simple test implementation of ITypeRegistry.
/// </summary>
internal class TestTypeRegistry : ITypeRegistry
{
    private readonly Dictionary<string, TestTypeDefinition> _typeByName = new();
    private readonly Dictionary<Type, string> _nameByType = new();
    private readonly List<Func<Type, KeyFunction?>> _keyFunctionProviders = new();

    public IEnumerable<KeyValuePair<string, ITypeDefinition>> Types =>
        _typeByName.Select(x => new KeyValuePair<string, ITypeDefinition>(x.Key, x.Value));

    public ITypeRegistry WithType(Type type) => WithType(type, type.Name);

    public ITypeRegistry WithType(Type type, string typeName)
    {
        _typeByName[typeName] = new TestTypeDefinition(type, typeName);
        _nameByType[type] = typeName;
        return this;
    }

    public KeyFunction? GetKeyFunction(string collection) =>
        _typeByName.TryGetValue(collection, out var td) ? td.KeyFunction : null;

    public KeyFunction? GetKeyFunction(Type type)
    {
        if (_nameByType.TryGetValue(type, out var name) && _typeByName.TryGetValue(name, out var td))
            return td.KeyFunction;
        foreach (var provider in _keyFunctionProviders)
        {
            var keyFunc = provider(type);
            if (keyFunc != null)
                return keyFunc;
        }
        return null;
    }

    public ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction)
    {
        if (_typeByName.TryGetValue(collection, out var td))
        {
            td.KeyFunction = keyFunction;
            return td;
        }
        throw new ArgumentException($"Type {collection} not found");
    }

    public bool TryGetType(string name, out ITypeDefinition? type)
    {
        if (_typeByName.TryGetValue(name, out var td))
        {
            type = td;
            return true;
        }
        type = null;
        return false;
    }

    public Type? GetType(string name) =>
        _typeByName.TryGetValue(name, out var td) ? td.Type : null;

    public bool TryGetCollectionName(Type type, out string? typeName)
    {
        if (_nameByType.TryGetValue(type, out typeName))
            return true;
        typeName = null;
        return false;
    }

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter)
    {
        foreach (var t in type.Assembly.GetTypes().Where(filter))
            WithType(t);
        return this;
    }

    public ITypeRegistry WithTypes(params IEnumerable<Type> types)
    {
        foreach (var t in types)
            WithType(t);
        return this;
    }

    public ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string, Type>> types)
    {
        foreach (var kvp in types)
            WithType(kvp.Value, kvp.Key);
        return this;
    }

    public string GetOrAddType(Type type, string? defaultName = null)
    {
        if (_nameByType.TryGetValue(type, out var name))
            return name;
        name = defaultName ?? type.Name;
        WithType(type, name);
        return name;
    }

    public ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction?> key)
    {
        _keyFunctionProviders.Add(key);
        return this;
    }

    public ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null)
    {
        if (_nameByType.TryGetValue(type, out var name) && _typeByName.TryGetValue(name, out var td))
            return td;
        if (create)
        {
            typeName ??= type.Name;
            var newTd = new TestTypeDefinition(type, typeName);
            _typeByName[typeName] = newTd;
            _nameByType[type] = typeName;
            return newTd;
        }
        return null;
    }

    public ITypeDefinition? GetTypeDefinition(string collection) =>
        _typeByName.TryGetValue(collection, out var td) ? td : null;

    private class TestTypeDefinition(Type type, string collectionName) : ITypeDefinition
    {
        public Type Type { get; } = type;
        public string CollectionName { get; } = collectionName;
        public string DisplayName => CollectionName;
        public object? Icon => null;
        public int? Order => null;
        public string? GroupName => null;
        public string Description => string.Empty;
        public KeyFunction? KeyFunction { get; set; }
        public KeyFunction? Key => KeyFunction;

        public object GetKey(object instance)
        {
            if (KeyFunction != null)
                return KeyFunction.Function(instance);
            return instance.GetHashCode();
        }

        public Type GetKeyType()
        {
            return KeyFunction?.KeyType ?? typeof(int);
        }
    }
}
