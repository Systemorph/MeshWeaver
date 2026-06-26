#nullable enable
using System.Reflection;

namespace MeshWeaver.BusinessRules;

/// <summary>
/// Base class for concrete scope implementations. Provides identity access, state access,
/// navigation to related scopes, and evaluation of default-interface-method rules.
/// </summary>
/// <typeparam name="TScope">The scope interface this implementation represents.</typeparam>
/// <typeparam name="TIdentity">The type of the identity the scope is keyed by.</typeparam>
/// <typeparam name="TState">The type of the shared state the scope reads from.</typeparam>
/// <param name="identity">The identity this scope is associated with.</param>
/// <param name="registry">The owning registry that supplies shared state and related scopes.</param>
public class ScopeBase<TScope, TIdentity, TState>(
    TIdentity identity,
    ScopeRegistry<TState> registry
    ) : IScope<TIdentity, TState>
where TScope : IScope<TIdentity, TState>
{
    /// <summary>Gets the identity this scope is associated with.</summary>
    public TIdentity Identity => identity;
    /// <summary>Gets the shared state backing this scope, taken from the owning registry.</summary>
    public TState GetStorage() => registry.State;
    /// <summary>
    /// Resolves the related scope of the requested type for the given identity via the owning registry.
    /// </summary>
    /// <typeparam name="TRelatedScope">The related scope type to resolve.</typeparam>
    /// <param name="id">The identity the requested scope is keyed by.</param>
    /// <returns>The related scope instance of type <typeparamref name="TRelatedScope"/>.</returns>
    public TRelatedScope GetScope<TRelatedScope>(object id) where TRelatedScope : IScope => registry.GetScope<TRelatedScope>(id);
    /// <summary>Gets the scope interface type (<typeparamref name="TScope"/>) this implementation represents.</summary>
    public Type ScopeType => typeof(TScope);

    /// <summary>
    /// Invokes the given method non-virtually (the default interface implementation) against this scope
    /// and returns its result.
    /// </summary>
    /// <typeparam name="T">The expected return type of the evaluated method.</typeparam>
    /// <param name="method">The method to invoke non-virtually on this scope.</param>
    /// <returns>The result of invoking <paramref name="method"/>, cast to <typeparamref name="T"/>.</returns>
    protected T Evaluate<T>(MethodInfo method)
        => (T)DefaultImplementationOfInterfacesExtensions.DynamicInvokeNonVirtually(method, this, []);

}
