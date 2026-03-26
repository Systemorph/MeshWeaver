using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Scoped IMeshService implementation.
/// Writes go through hub messaging (Post + RegisterCallback) — no direct persistence dependency.
/// Reads go through MeshQuery (aggregated query providers).
/// Identity is captured from AccessService and stamped on each delivery.
/// </summary>
internal sealed class MeshService(
    IEnumerable<IMeshQueryProvider> providers,
    IMessageHub hub)
    : IMeshService
{
    private readonly MeshQuery _query = new(providers, hub);

    /// <summary>
    /// The mesh hub address where CRUD handlers (CreateNode, UpdateNode, DeleteNode) are registered.
    /// Resolved from MeshCatalog if available, otherwise falls back to the root hub.
    /// </summary>
    private Address MeshAddress => hub.ServiceProvider.GetService<MeshCatalog>()?.MeshAddress ?? hub.Address;

    private AccessContext? CaptureContext()
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context ?? accessService?.CircuitContext;
    }

    private PostOptions ConfigurePost(PostOptions o, AccessContext? captured)
    {
        o = o.WithTarget(MeshAddress);
        return captured != null ? o.WithAccessContext(captured) : o;
    }

    // === Node CRUD via messaging ===

    public IObservable<MeshNode> CreateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var cts = new CancellationTokenSource();
            var delivery = hub.Post(new CreateNodeRequest(node),
                o => ConfigurePost(o, captured))!;

            hub.RegisterCallback(delivery, (d, _) =>
            {
                var r = ((IMessageDelivery<CreateNodeResponse>)d).Message;
                if (r.Success && r.Node != null)
                {
                    observer.OnNext(r.Node);
                    observer.OnCompleted();
                }
                else
                {
                    observer.OnError(r.RejectionReason switch
                    {
                        NodeCreationRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        NodeCreationRejectionReason.NodeAlreadyExists =>
                            new InvalidOperationException($"Node already exists: {node.Path}"),
                        _ => new InvalidOperationException(r.Error ?? "Node creation failed")
                    });
                }
                return Task.FromResult(d);
            }, cts.Token);

            return Disposable.Create(() => cts.Cancel());
        });
    }

    public IObservable<MeshNode> UpdateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var cts = new CancellationTokenSource();
            var delivery = hub.Post(new UpdateNodeRequest(node),
                o => ConfigurePost(o, captured))!;

            hub.RegisterCallback(delivery, (d, _) =>
            {
                var r = ((IMessageDelivery<UpdateNodeResponse>)d).Message;
                if (r.Success && r.Node != null)
                {
                    observer.OnNext(r.Node);
                    observer.OnCompleted();
                }
                else
                {
                    observer.OnError(r.RejectionReason switch
                    {
                        NodeUpdateRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        NodeUpdateRejectionReason.NodeNotFound =>
                            new InvalidOperationException($"Node not found: {node.Path}"),
                        _ => new InvalidOperationException(r.Error ?? "Node update failed")
                    });
                }
                return Task.FromResult(d);
            }, cts.Token);

            return Disposable.Create(() => cts.Cancel());
        });
    }

    public IObservable<bool> DeleteNode(string path)
    {
        var captured = CaptureContext();
        return Observable.Create<bool>(observer =>
        {
            var cts = new CancellationTokenSource();
            var delivery = hub.Post(new DeleteNodeRequest(path) { Recursive = true },
                o => ConfigurePost(o, captured))!;

            hub.RegisterCallback(delivery, (d, _) =>
            {
                var r = ((IMessageDelivery<DeleteNodeResponse>)d).Message;
                if (r.Success)
                {
                    observer.OnNext(true);
                    observer.OnCompleted();
                }
                else
                {
                    observer.OnError(r.RejectionReason switch
                    {
                        NodeDeletionRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        NodeDeletionRejectionReason.NodeNotFound =>
                            new InvalidOperationException($"Node not found: {path}"),
                        _ => new InvalidOperationException(r.Error ?? "Node deletion failed")
                    });
                }
                return Task.FromResult(d);
            }, cts.Token);

            return Disposable.Create(() => cts.Cancel());
        });
    }

    public IObservable<MeshNode> CreateTransient(MeshNode node)
    {
        var persistence = hub.ServiceProvider.GetService<IMeshStorage>();
        if (persistence == null)
            return CreateNode(node);

        return Observable.FromAsync(async ct =>
        {
            // Persist directly with Transient state — bypasses the CreateNodeRequest handler
            // which would force Active state.
            var transientNode = node with { State = MeshNodeState.Transient };
            return await persistence.SaveNodeAsync(transientNode, ct);
        });
    }

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
