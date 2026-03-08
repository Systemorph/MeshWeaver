using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Extension methods for IMeshService providing typed query support.
/// </summary>
public static class MeshQueryExtensions
{
    #region IMeshService (wrapper) extensions - no JsonSerializerOptions

    /// <summary>
    /// Query for objects of a specific type with type-safe results.
    /// Adds $type filter to the query and casts results to T.
    /// </summary>
    /// <typeparam name="T">The type to query for</typeparam>
    /// <param name="meshQuery">The mesh query service</param>
    /// <param name="request">The query request</param>
    /// <param name="typeRegistry">Optional type registry for type name resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Typed results matching the query</returns>
    public static async IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService meshQuery,
        MeshQueryRequest request,
        ITypeRegistry? typeRegistry = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typeName = GetTypeName<T>(typeRegistry);
        var typedRequest = AddTypeFilter(request, typeName);

        await foreach (var item in meshQuery.QueryAsync(typedRequest, ct))
        {
            if (item is T typed)
            {
                yield return typed;
            }
        }
    }

    /// <summary>
    /// Query for objects of a specific type using a query string.
    /// Adds $type filter to the query and casts results to T.
    /// </summary>
    /// <typeparam name="T">The type to query for</typeparam>
    /// <param name="meshQuery">The mesh query service</param>
    /// <param name="query">The query string</param>
    /// <param name="typeRegistry">Optional type registry for type name resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Typed results matching the query</returns>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService meshQuery,
        string query,
        ITypeRegistry? typeRegistry = null,
        CancellationToken ct = default)
    {
        return meshQuery.QueryAsync<T>(MeshQueryRequest.FromQuery(query), typeRegistry, ct);
    }

    /// <summary>
    /// Query for objects of a specific type with paging.
    /// Adds $type filter to the query and casts results to T.
    /// </summary>
    /// <typeparam name="T">The type to query for</typeparam>
    /// <param name="meshQuery">The mesh query service</param>
    /// <param name="query">The query string</param>
    /// <param name="skip">Number of results to skip</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="typeRegistry">Optional type registry for type name resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Typed results matching the query</returns>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService meshQuery,
        string query,
        int skip,
        int limit,
        ITypeRegistry? typeRegistry = null,
        CancellationToken ct = default)
    {
        var request = new MeshQueryRequest
        {
            Query = query,
            Skip = skip,
            Limit = limit
        };
        return meshQuery.QueryAsync<T>(request, typeRegistry, ct);
    }

    #endregion

    #region IMeshQueryProvider extensions - with JsonSerializerOptions

    /// <summary>
    /// Query for objects of a specific type with type-safe results.
    /// Adds $type filter to the query and casts results to T.
    /// </summary>
    /// <typeparam name="T">The type to query for</typeparam>
    /// <param name="meshQuery">The mesh query core service</param>
    /// <param name="request">The query request</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="typeRegistry">Optional type registry for type name resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Typed results matching the query</returns>
    public static async IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshQueryProvider meshQuery,
        MeshQueryRequest request,
        JsonSerializerOptions options,
        ITypeRegistry? typeRegistry = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typeName = GetTypeName<T>(typeRegistry);
        var typedRequest = AddTypeFilter(request, typeName);

        await foreach (var item in meshQuery.QueryAsync(typedRequest, options, ct))
        {
            if (item is T typed)
            {
                yield return typed;
            }
        }
    }

    /// <summary>
    /// Query for objects of a specific type using a query string.
    /// Adds $type filter to the query and casts results to T.
    /// </summary>
    /// <typeparam name="T">The type to query for</typeparam>
    /// <param name="meshQuery">The mesh query core service</param>
    /// <param name="query">The query string</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="typeRegistry">Optional type registry for type name resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Typed results matching the query</returns>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshQueryProvider meshQuery,
        string query,
        JsonSerializerOptions options,
        ITypeRegistry? typeRegistry = null,
        CancellationToken ct = default)
    {
        return meshQuery.QueryAsync<T>(MeshQueryRequest.FromQuery(query), options, typeRegistry, ct);
    }

    /// <summary>
    /// Query for objects of a specific type with paging.
    /// Adds $type filter to the query and casts results to T.
    /// </summary>
    /// <typeparam name="T">The type to query for</typeparam>
    /// <param name="meshQuery">The mesh query core service</param>
    /// <param name="query">The query string</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="skip">Number of results to skip</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="typeRegistry">Optional type registry for type name resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Typed results matching the query</returns>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshQueryProvider meshQuery,
        string query,
        JsonSerializerOptions options,
        int skip,
        int limit,
        ITypeRegistry? typeRegistry = null,
        CancellationToken ct = default)
    {
        var request = new MeshQueryRequest
        {
            Query = query,
            Skip = skip,
            Limit = limit
        };
        return meshQuery.QueryAsync<T>(request, options, typeRegistry, ct);
    }

    #endregion

    private static string GetTypeName<T>(ITypeRegistry? typeRegistry)
    {
        if (typeRegistry != null)
        {
            var collectionName = typeRegistry.GetCollectionName(typeof(T));
            if (!string.IsNullOrEmpty(collectionName))
            {
                return collectionName;
            }
        }

        return typeof(T).Name;
    }

    private static MeshQueryRequest AddTypeFilter(MeshQueryRequest request, string typeName)
    {
        var typeFilter = $"$type:{typeName}";
        var newQuery = string.IsNullOrWhiteSpace(request.Query)
            ? typeFilter
            : $"{typeFilter} {request.Query}";

        return request with { Query = newQuery };
    }
}
