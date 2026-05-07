using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Marker interface for query providers that <c>SyncedQueryMeshNodes</c>
/// is allowed to consume for path discovery. Deliberately a NARROW
/// surface — only <see cref="ObserveQuery{T}"/> — because the synced
/// query needs nothing else from a provider, and constraining the
/// contract this way lets unsecured providers
/// (<c>InMemoryMeshQueryCore</c>) implement it without taking on the
/// full <see cref="IMeshQueryProvider"/> surface (Autocomplete, Select,
/// QueryAsync) they don't need.
///
/// <para>Why dedicated:</para>
/// <list type="bullet">
///   <item>Synced queries fan out across MULTIPLE registered providers
///         (persistence + static-node + any future ones). Using the
///         general <see cref="IMeshQueryProvider"/> set risks pulling
///         in the secured <c>InMemoryMeshQuery</c>, which has a
///         <c>ISecurityService</c> dependency that creates a re-entrancy
///         cycle when the synced query is the one feeding
///         <c>SecurityService</c> itself
///         (<c>nodeType:AccessAssignment</c>).</item>
///   <item>Marker semantics make registration intent explicit. A
///         provider opts in by implementing this interface, instead of
///         the synced query filtering by type name (brittle) or by
///         iterating every <see cref="IMeshQueryProvider"/> (cycles).</item>
/// </list>
///
/// <para>Implementers are unsecured by contract — they MUST NOT depend
/// on services that themselves consume synced queries. The synced
/// query stays infrastructure-level (<c>WellKnownUsers.System</c>);
/// access control runs LATER if the consumer dispatches to the owning
/// hub.</para>
/// </summary>
public interface ISyncedMeshNodeQueryProvider
{
    /// <summary>
    /// Stable identifier for this provider — used by
    /// <c>SyncedQueryMeshNodes</c> to deduplicate registrations.
    /// Defaults to the type's full name; override only if the same
    /// type registers multiple times with different state.
    /// </summary>
    string Name => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// Observe nodes matching a query. Same shape as
    /// <see cref="IMeshQueryProvider.ObserveQuery{T}"/> — minus the
    /// security filter on the result set.
    /// </summary>
    IObservable<QueryResultChange<T>> ObserveQuery<T>(
        MeshQueryRequest request, JsonSerializerOptions options);
}
