namespace MeshWeaver.Domain;

/// <summary>
/// Registry mapping CLR types to their serialization-friendly collection names and key functions,
/// and resolving type definitions by name or type.
/// </summary>
public interface ITypeRegistry
{
    /// <summary>
    /// Registers the type <typeparamref name="TEvent"/> using its default collection name.
    /// </summary>
    /// <typeparam name="TEvent">The type to register.</typeparam>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithType<TEvent>() => WithType(typeof(TEvent));
    /// <summary>
    /// Registers the type <typeparamref name="TEvent"/> under the given collection name.
    /// </summary>
    /// <typeparam name="TEvent">The type to register.</typeparam>
    /// <param name="name">The collection name to register the type under.</param>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithType<TEvent>(string name) => WithType(typeof(TEvent), name);
    /// <summary>
    /// Registers the given type using its default collection name.
    /// </summary>
    /// <param name="type">The type to register.</param>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithType(Type type);
    /// <summary>
    /// Registers the given type under the specified collection name.
    /// </summary>
    /// <param name="type">The type to register.</param>
    /// <param name="typeName">The collection name to register the type under.</param>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithType(Type type, string typeName);
    /// <summary>
    /// Returns the key function for the given collection name, or <c>null</c> if none is registered.
    /// </summary>
    /// <param name="collection">The collection name to look up.</param>
    /// <returns>The key function, or <c>null</c> if not found.</returns>
    KeyFunction? GetKeyFunction(string collection);
    /// <summary>
    /// Returns the key function for the given type, or <c>null</c> if none is registered.
    /// </summary>
    /// <param name="type">The type to look up.</param>
    /// <returns>The key function, or <c>null</c> if not found.</returns>
    KeyFunction? GetKeyFunction(Type type);
    /// <summary>
    /// Associates a key function with the given collection name.
    /// </summary>
    /// <param name="collection">The collection name to associate the key function with.</param>
    /// <param name="keyFunction">The key function to register.</param>
    /// <returns>The affected type definition.</returns>
    ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction);
    /// <summary>
    /// Attempts to resolve the type definition registered under the given name.
    /// </summary>
    /// <param name="name">The collection name to resolve.</param>
    /// <param name="type">When this method returns, the resolved type definition, or <c>null</c> if not found.</param>
    /// <returns><c>true</c> if a definition was found; otherwise <c>false</c>.</returns>
    bool TryGetType(string name, out ITypeDefinition? type);
    /// <summary>
    /// Returns the CLR type registered under the given name, or <c>null</c> if not found.
    /// </summary>
    /// <param name="name">The collection name to resolve.</param>
    /// <returns>The CLR type, or <c>null</c> if not found.</returns>
    Type? GetType(string name);
    /// <summary>
    /// Attempts to resolve the collection name registered for the given type.
    /// </summary>
    /// <param name="type">The type to look up.</param>
    /// <param name="typeName">When this method returns, the collection name, or <c>null</c> if not found.</param>
    /// <returns><c>true</c> if a collection name was found; otherwise <c>false</c>.</returns>
    bool TryGetCollectionName(Type type, out string? typeName);
    /// <summary>
    /// Returns the collection name registered for the given type, or <c>null</c> if not found.
    /// </summary>
    /// <param name="type">The type to look up.</param>
    /// <returns>The collection name, or <c>null</c> if not found.</returns>
    string? GetCollectionName(Type type) => TryGetCollectionName(type, out var typeName) ? typeName : null;
    /// <summary>
    /// Registers all matching types from the assembly that contains <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A type whose declaring assembly is scanned.</typeparam>
    /// <param name="filter">Predicate selecting which types to register.</param>
    /// <returns>This registry, for chaining.</returns>
    public ITypeRegistry WithTypesFromAssembly<T>(Func<Type, bool> filter)
        => WithTypesFromAssembly(typeof(T), filter);

    /// <summary>
    /// Registers all matching types from the assembly that declares the given type.
    /// </summary>
    /// <param name="type">A type whose declaring assembly is scanned.</param>
    /// <param name="filter">Predicate selecting which types to register.</param>
    /// <returns>This registry, for chaining.</returns>
    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter);
    /// <summary>
    /// Registers each of the given types using their default collection names.
    /// </summary>
    /// <param name="types">The types to register.</param>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithTypes(params IEnumerable<Type> types);
    /// <summary>
    /// Registers each of the given types under their associated collection names.
    /// </summary>
    /// <param name="types">The collection-name/type pairs to register.</param>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string, Type>> types);
    /// <summary>
    /// Returns the collection name for the type, registering it (optionally under a default name) if absent.
    /// </summary>
    /// <param name="valueType">The type to resolve or register.</param>
    /// <param name="defaultName">The collection name to use when registering a new type; defaults to the type's name.</param>
    /// <returns>The resolved or newly assigned collection name.</returns>
    string GetOrAddType(Type valueType, string? defaultName = null);
    /// <summary>
    /// Registers a provider that supplies a key function for a type on demand.
    /// </summary>
    /// <param name="key">A function returning a key function for a given type, or <c>null</c> if it cannot supply one.</param>
    /// <returns>This registry, for chaining.</returns>
    ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction?> key);
    /// <summary>
    /// Returns the type definition for the given type, optionally creating one if it is not yet registered.
    /// </summary>
    /// <param name="type">The type to resolve.</param>
    /// <param name="create">Whether to create and register a definition when none exists.</param>
    /// <param name="typeName">Optional collection name to use when creating a new definition.</param>
    /// <returns>The type definition, or <c>null</c> if none exists and <paramref name="create"/> is <c>false</c>.</returns>
    ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null);
    /// <summary>
    /// Returns the type definition registered under the given collection name, or <c>null</c> if not found.
    /// </summary>
    /// <param name="collection">The collection name to resolve.</param>
    /// <returns>The type definition, or <c>null</c> if not found.</returns>
    ITypeDefinition? GetTypeDefinition(string collection);

    /// <summary>
    /// All registered type definitions, keyed by their collection name.
    /// </summary>
    IEnumerable<KeyValuePair<string, ITypeDefinition>> Types { get; }
}
/// <summary>
/// A function that extracts an identity key from an instance, together with the key's CLR type.
/// </summary>
/// <param name="Function">The function returning the key for a given instance.</param>
/// <param name="KeyType">The CLR type of the key produced by <paramref name="Function"/>.</param>
public record KeyFunction(Func<object, object> Function, Type KeyType);
