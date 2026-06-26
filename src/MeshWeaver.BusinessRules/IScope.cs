namespace MeshWeaver.BusinessRules;

/// <summary>
/// Non-generic base abstraction for a business-rules scope: an object that exposes
/// derived/calculated members for a given identity and can navigate to related scopes.
/// </summary>
public interface IScope
{
    /// <summary>
    /// Resolves (creating it on first access) the related scope of the requested type for the given identity.
    /// </summary>
    /// <typeparam name="TScope">The scope type to resolve.</typeparam>
    /// <param name="identity">The identity that the requested scope is keyed by.</param>
    /// <returns>The scope instance of type <typeparamref name="TScope"/> for the given identity.</returns>
    TScope GetScope<TScope>(object identity) where TScope : IScope;

}
/// <summary>
/// A business-rules scope keyed by an identity and backed by shared state.
/// </summary>
/// <typeparam name="TIdentity">The type of the identity the scope is keyed by.</typeparam>
/// <typeparam name="TState">The type of the shared state the scope reads from.</typeparam>
public interface IScope<out TIdentity, out TState> : IScope
{
    /// <summary>Gets the identity this scope is associated with.</summary>
    TIdentity Identity { get; }
    /// <summary>Gets the shared state backing this scope.</summary>
    TState GetStorage();
}
