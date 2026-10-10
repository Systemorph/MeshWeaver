namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The mesh's own query predicate — the same evaluation the in-memory providers apply (filter
/// conditions and free-text search) — for a provider that SYNTHESIZES its rows and must answer a
/// query with ordinary mesh-query semantics (the app directory's <c>{user}/_Apps</c>). Registered
/// by the persistence layer; path, scope, sort and limit stay with the caller and the aggregator.
/// </summary>
public interface IMeshNodeQueryMatcher
{
    /// <summary>Whether <paramref name="node"/> satisfies <paramref name="query"/>'s filter and
    /// text search.</summary>
    bool Matches(MeshNode node, ParsedQuery query);
}
