using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Scoped IMeshService implementation that composes HubNodePersistence (writes)
/// and MeshQuery (reads) into a single unified service.
/// Identity is always resolved from AccessService.Context.
/// Use accessService.ImpersonateAsHub(hub) to temporarily switch identity.
/// </summary>
internal sealed class MeshService(
    IEnumerable<IMeshQueryProvider> providers,
    IMessageHub hub,
    MeshCatalog? catalog = null)
    : IMeshService
{
    private readonly HubNodePersistence? _persistence = catalog != null ? new HubNodePersistence(hub, catalog) : null;
    private readonly MeshQuery _query = new(providers, hub);

    // === Node CRUD (delegated to HubNodePersistence) ===

    private HubNodePersistence Persistence
        => _persistence ?? throw new InvalidOperationException(
            "Write operations require MeshCatalog. Register it via AddMeshCatalog().");

    public IObservable<MeshNode> CreateNode(MeshNode node) => Persistence.CreateNode(node);
    public IObservable<MeshNode> UpdateNode(MeshNode node) => Persistence.UpdateNode(node);
    public IObservable<bool> DeleteNode(string path) => Persistence.DeleteNode(path);
    public IObservable<MeshNode> CreateTransient(MeshNode node) => Persistence.CreateTransient(node);

    // === Query (delegated to MeshQuery) ===

    public IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, CancellationToken ct = default)
        => _query.QueryAsync(request, ct);

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath, string prefix, int limit = 10, CancellationToken ct = default)
        => _query.AutocompleteAsync(basePath, prefix, limit, ct);

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath, string prefix, AutocompleteMode mode, int limit = 10,
        string? contextPath = null, string? context = null, CancellationToken ct = default)
        => _query.AutocompleteAsync(basePath, prefix, mode, limit, contextPath, context, ct);

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
        => _query.ObserveQuery<T>(request);

    public Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default)
        => _query.SelectAsync<T>(path, property, ct);

    public async Task<string?> GetPreRenderedHtmlAsync(string path, CancellationToken ct = default)
    {
        await foreach (var result in _query.QueryAsync(
            new MeshQueryRequest { Query = $"path:{path}", Limit = 1 }, ct))
        {
            if (result is MeshNode node)
                return node.PreRenderedHtml;
        }
        return null;
    }
}
