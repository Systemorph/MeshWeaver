using System.Collections;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Scopes.Synchronization;

namespace OpenSmc.Scopes
{
    /// <summary>
    /// Basic interface to define scopes.
    /// </summary>
    public interface IScope : IDisposable
    {
        /// <summary>
        /// Assigns a GUID to the scope instance and caches it
        /// </summary>
        /// <returns></returns>
        Guid GetGuid();

        /// <summary>
        /// Gets the context in which a scope was created
        /// </summary>
        /// <returns></returns>
        string GetContext();

        /// <summary>
        /// Gets the main ScopeType as it was resolved from the scope factory or one of the GetScope overloads.
        /// </summary>
        /// <returns></returns>
        Type GetScopeType();

        /// <summary>
        /// Gets or creates a scope unique to the scope type <typeparamref name="TScope"/>>,
        /// the <paramref name="identity"/>.
        /// Additional creation parameters, such as the factory or the context, can be provided in the <paramref name="options"/>.
        /// </summary>
        /// <typeparam name="TScope">Type of the scope to be resolved.</typeparam>
        /// <param name="identity">Identity of the scope. If it is <code>null</code>,
        /// it will be resolved as the singleton identity for a singleton scope (i.e. inheriting from the non-generic <code>IScope</code>).
        /// In case of a scope with identity, the same identity of the scope is passed as a parameter (see other overloads).</param>
        /// <param name="options">Options for specifying the scope, such as context, factory, etc.</param>
        /// <returns></returns>
        TScope GetScope<TScope>(object identity, Func<ScopeBuilderForScope<TScope>, ScopeBuilderForScope<TScope>> options);

        /// <summary>
        /// Returns a singleton instance or the instance with the same identity as the parent. 
        /// </summary>
        /// <typeparam name="TScope"></typeparam>
        /// <param name="options">Options for specifying the scope, such as context, factory, etc.</param>
        /// <returns></returns>
        TScope GetScope<TScope>(Func<ScopeBuilderForScope<TScope>, ScopeBuilderForScope<TScope>> options) => GetScope(null, options);
        TScope GetScope<TScope>() => GetScope<TScope>(null, null);
        TScope GetScope<TScope>(object identity) => GetScope<TScope>(identity, null);

        /// <summary>
        /// Gets or creates a scope unique to the scope type <typeparamref name="TScope"/>>,
        /// the <paramref name="identities"/>.
        /// Additional creation parameters, such as the factory or the context, can be provided in the <paramref name="options"/>.
        /// </summary>
        /// <typeparam name="TScope">Type of the scopes to be resolved.</typeparam>
        /// <param name="identities">Identities of the scopes. </param>
        /// <param name="options">Options for specifying the scope</param>
        /// <returns></returns>
        IList<TScope> GetScopes<TScope>(IEnumerable<object> identities, Func<ScopeBuilderForScope<TScope>, ScopeBuilderForScope<TScope>> options);
        IList<TScope> GetScopes<TScope>(IEnumerable<object> identities) => GetScopes<TScope>(identities,null);
        IList<TScope> GetScopes<TScope>(IEnumerable identities) => GetScopes<TScope>(identities.Cast<object>(), null);
    }

    public interface IScope<out TIdentity> : IScope
    {
        TIdentity Identity { get; }
    }

    public interface IScopeWithStorage<out TStorage> : IScope
    { 
        TStorage GetStorage();
    }


    public interface IScope<out TIdentity, out TStorage> : IScope<TIdentity>, IScopeWithStorage<TStorage>
    {
    }

    public interface IMutableScope : IScope
    {
        event ScopePropertyChangedEventHandler ScopePropertyChanged;
    }

    public interface IMutableScope<out TIdentity> : IScope<TIdentity>, IMutableScope { }
    public interface IMutableScopeWithStorage<out TStorage> : IScopeWithStorage<TStorage>, IMutableScope { }

    public interface IMutableScope<out TIdentity, out TStorage> : IScope<TIdentity, TStorage>, IMutableScope {}
}