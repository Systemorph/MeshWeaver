using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Scoped IMeshService implementation that composes HubNodePersistence (writes)
/// and MeshQuery (reads) into a single unified service.
/// </summary>
internal sealed class MeshService : IMeshService
{
    private readonly HubNodePersistence? _persistence;
    private readonly MeshQuery _query;
    private readonly IEnumerable<IMeshQueryProvider> _providers;
    private readonly IMessageHub _hub;
    private readonly MeshCatalog? _catalog;
    private readonly bool _impersonate;

    public MeshService(
        IEnumerable<IMeshQueryProvider> providers,
        IMessageHub hub,
        MeshCatalog? catalog = null,
        bool impersonate = false)
    {
        _providers = providers;
        _hub = hub;
        _catalog = catalog;
        _impersonate = impersonate;
        _persistence = catalog != null ? new HubNodePersistence(hub, catalog, impersonate) : null;
        _query = new MeshQuery(providers, hub, impersonate);
    }

    // === Node CRUD (delegated to HubNodePersistence) ===

    private HubNodePersistence Persistence
        => _persistence ?? throw new InvalidOperationException(
            "Write operations require MeshCatalog. Register it via AddMeshCatalog().");

    public Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
        => Persistence.CreateNodeAsync(node, createdBy, ct);

    public Task<MeshNode> UpdateNodeAsync(MeshNode node, string? updatedBy = null, CancellationToken ct = default)
        => Persistence.UpdateNodeAsync(node, updatedBy, ct);

    public Task DeleteNodeAsync(string path, string? deletedBy = null, CancellationToken ct = default)
        => Persistence.DeleteNodeAsync(path, deletedBy, ct);

    public Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default)
        => Persistence.CreateTransientAsync(node, ct);

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

    // === Impersonation ===

    public IMeshService ImpersonateAsNode()
        => _impersonate ? this : new MeshService(_providers, _hub, _catalog, true);
}
