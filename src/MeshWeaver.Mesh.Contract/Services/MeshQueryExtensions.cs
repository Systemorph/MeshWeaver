using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Extension methods for IMeshService / IMeshQueryProvider providing typed-result queries.
/// All overloads compose on top of <see cref="IMeshService.ObserveQuery{T}"/> — there is no
/// underlying <c>QueryAsync</c> interface method anymore. The Initial emission of
/// <see cref="IMeshService.ObserveQuery{T}"/> is the legacy "QueryAsync" snapshot.
/// </summary>
public static class MeshQueryExtensions
{
    #region IMeshService observable typed query

    /// <summary>
    /// Observe typed results for a query — emits Initial / Added / Updated / Removed changes
    /// scoped to objects of type <typeparamref name="T"/>. Adds <c>$type</c> filter to the
    /// query so non-MeshNode payloads (partition objects) only show up when <c>T</c> matches.
    /// </summary>
    public static IObservable<QueryResultChange<T>> ObserveQuery<T>(
        this IMeshService meshQuery,
        MeshQueryRequest request,
        ITypeRegistry? typeRegistry = null)
    {
        var typeName = GetTypeName<T>(typeRegistry);
        var typedRequest = AddTypeFilter(request, typeName);
        return meshQuery.ObserveQuery<T>(typedRequest);
    }

    /// <summary>
    /// Observe typed results from a query string.
    /// </summary>
    public static IObservable<QueryResultChange<T>> ObserveQuery<T>(
        this IMeshService meshQuery,
        string query,
        ITypeRegistry? typeRegistry = null)
        => meshQuery.ObserveQuery<T>(MeshQueryRequest.FromQuery(query), typeRegistry);

    /// <summary>
    /// Observe typed results from a query string with paging.
    /// </summary>
    public static IObservable<QueryResultChange<T>> ObserveQuery<T>(
        this IMeshService meshQuery,
        string query,
        int skip,
        int limit,
        ITypeRegistry? typeRegistry = null)
        => meshQuery.ObserveQuery<T>(new MeshQueryRequest { Query = query, Skip = skip, Limit = limit }, typeRegistry);

    #endregion

    #region IMeshQueryProvider observable typed query

    /// <summary>
    /// Observe typed results for a query against a raw provider (no security filter).
    /// </summary>
    public static IObservable<QueryResultChange<T>> ObserveQuery<T>(
        this IMeshQueryProvider meshQuery,
        MeshQueryRequest request,
        JsonSerializerOptions options,
        ITypeRegistry? typeRegistry = null)
    {
        var typeName = GetTypeName<T>(typeRegistry);
        var typedRequest = AddTypeFilter(request, typeName);
        return meshQuery.ObserveQuery<T>(typedRequest, options);
    }

    #endregion

    private static string GetTypeName<T>(ITypeRegistry? typeRegistry)
    {
        if (typeRegistry != null)
        {
            var collectionName = typeRegistry.GetCollectionName(typeof(T));
            if (!string.IsNullOrEmpty(collectionName))
                return collectionName;
        }
        return typeof(T).Name;
    }

    private static MeshQueryRequest AddTypeFilter(MeshQueryRequest request, string typeName)
    {
        // MeshNode is the base type — all rows in mesh_nodes are MeshNodes,
        // so adding $type:MeshNode would incorrectly filter on content.$type instead.
        if (typeName == nameof(MeshNode))
            return request;

        var typeFilter = $"$type:{typeName}";
        var newQuery = string.IsNullOrWhiteSpace(request.Query)
            ? typeFilter
            : $"{typeFilter} {request.Query}";

        return request with { Query = newQuery };
    }
}
