using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Scoped wrapper around IMeshQueryCore that automatically injects
/// JsonSerializerOptions from the current IMessageHub.
/// </summary>
public class MeshQueryService(IMeshQueryCore core, IMessageHub hub) : IMeshQuery
{
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    public IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, CancellationToken ct = default)
        => core.QueryAsync(request, Options, ct);

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        CancellationToken ct = default)
        => core.AutocompleteAsync(basePath, prefix, Options, limit, ct);

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        CancellationToken ct = default)
        => core.AutocompleteAsync(basePath, prefix, Options, mode, limit, ct);

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
        => core.ObserveQuery<T>(request, Options);
}
