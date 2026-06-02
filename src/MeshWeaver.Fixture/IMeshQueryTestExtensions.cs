using System.Reactive.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;

namespace MeshWeaver.Mesh;

/// <summary>
/// Test-only sanctioned bridge from <see cref="IMeshService.Query{T}"/> back to
/// <see cref="IAsyncEnumerable{T}"/> at the test boundary. Production code MUST NOT use these —
/// it composes <c>IObservable&lt;T&gt;</c> end-to-end per
/// <c>Doc/Architecture/AsynchronousCalls.md</c>. Tests are allowed to bridge because
/// their assertion phase requires a single materialized snapshot.
/// </summary>
public static class IMeshQueryTestExtensions
{
    /// <summary>
    /// Legacy <c>QueryAsync</c> shape for tests: subscribes to <see cref="IMeshService.Query{T}"/>,
    /// takes the Initial emission, flattens its items as <c>IAsyncEnumerable&lt;object&gt;</c>.
    /// Uses <c>object</c> as the type parameter so <c>select:</c>-projected
    /// dictionaries (and other non-MeshNode payloads) survive the generic cast.
    /// </summary>
    public static IAsyncEnumerable<object> QueryAsync(
        this IMeshService svc,
        MeshQueryRequest request,
        CancellationToken ct = default)
        => svc.Query<object>(request)
            .Take(1)
            .SelectMany(c => c.Items.ToObservable())
            .ToAsyncEnumerableSequence(ct);

    /// <summary>
    /// Typed <c>QueryAsync&lt;T&gt;</c> shape for tests.
    /// </summary>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService svc,
        MeshQueryRequest request,
        CancellationToken ct = default)
        => svc.Query<T>(request)
            .Take(1)
            .SelectMany(c => c.Items.ToObservable())
            .ToAsyncEnumerableSequence(ct);

    /// <summary>
    /// Typed <c>QueryAsync&lt;T&gt;</c> from a query string.
    /// </summary>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService svc,
        string query,
        CancellationToken ct = default)
        => svc.QueryAsync<T>(MeshQueryRequest.FromQuery(query), ct);

    /// <summary>
    /// Typed <c>QueryAsync&lt;T&gt;</c> from a query string with an optional type registry.
    /// </summary>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService svc,
        string query,
        ITypeRegistry? typeRegistry,
        CancellationToken ct = default)
        => svc.QueryAsync<T>(MeshQueryRequest.FromQuery(query), ct);

    /// <summary>
    /// Typed <c>QueryAsync&lt;T&gt;</c> via an explicit request and optional type registry.
    /// </summary>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService svc,
        MeshQueryRequest request,
        ITypeRegistry? typeRegistry,
        CancellationToken ct = default)
        => svc.QueryAsync<T>(request, ct);

    /// <summary>
    /// Typed <c>QueryAsync&lt;T&gt;</c> with paging.
    /// </summary>
    public static IAsyncEnumerable<T> QueryAsync<T>(
        this IMeshService svc,
        string query,
        int skip,
        int limit,
        ITypeRegistry? typeRegistry = null,
        CancellationToken ct = default)
        => svc.QueryAsync<T>(new MeshQueryRequest { Query = query, Skip = skip, Limit = limit }, ct);
}
