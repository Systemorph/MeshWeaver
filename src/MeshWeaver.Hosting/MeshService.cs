using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(new CreateNodeRequest(node),
                    o => ConfigurePost(o, captured));
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }
            if (delivery == null)
            {
                observer.OnError(new InvalidOperationException("CreateNode: hub.Post returned null."));
                return Disposable.Empty;
            }
            // Subscribe to RegisterCallback's Task — DeliveryFailureException surfaces via onError
            // (RegisterCallback short-circuits the user callback for DeliveryFailure).
            return Observable.FromAsync(() =>
                    hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), default))
                .Subscribe(
                    d =>
                    {
                        if (d.Message is CreateNodeResponse r)
                        {
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
                        }
                        else
                        {
                            observer.OnError(new InvalidOperationException(
                                $"CreateNode: unexpected response type {d.Message?.GetType().Name ?? "null"}"));
                        }
                    },
                    observer.OnError);
        });
    }

    public IObservable<MeshNode> UpdateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(new UpdateNodeRequest(node),
                    o => ConfigurePost(o, captured));
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }
            if (delivery == null)
            {
                observer.OnError(new InvalidOperationException("UpdateNode: hub.Post returned null."));
                return Disposable.Empty;
            }
            return Observable.FromAsync(() =>
                    hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), default))
                .Subscribe(
                    d =>
                    {
                        if (d.Message is UpdateNodeResponse r)
                        {
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
                        }
                        else
                        {
                            observer.OnError(new InvalidOperationException(
                                $"UpdateNode: unexpected response type {d.Message?.GetType().Name ?? "null"}"));
                        }
                    },
                    observer.OnError);
        });
    }

    public IObservable<bool> DeleteNode(string path)
    {
        var captured = CaptureContext();
        return Observable.Create<bool>(observer =>
        {
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(new DeleteNodeRequest(path) { Recursive = true },
                    o => ConfigurePost(o, captured));
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }
            if (delivery == null)
            {
                observer.OnError(new InvalidOperationException("DeleteNode: hub.Post returned null."));
                return Disposable.Empty;
            }
            return Observable.FromAsync(() =>
                    hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), default))
                .Subscribe(
                    d =>
                    {
                        if (d.Message is DeleteNodeResponse r)
                        {
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
                        }
                        else
                        {
                            observer.OnError(new InvalidOperationException(
                                $"DeleteNode: unexpected response type {d.Message?.GetType().Name ?? "null"}"));
                        }
                    },
                    observer.OnError);
        });
    }

    public IObservable<MeshNode> CreateTransient(MeshNode node)
    {
        var persistence = hub.ServiceProvider.GetService<IMeshStorage>();
        if (persistence == null)
            return CreateNode(node);

        // Persistence ALLOWED here — `CreateTransient` is the canonical entry that
        // sets a transient MeshNode into the mesh. The request handler path for
        // `CreateNodeRequest` would force the node to `Active`; we deliberately want
        // a node in `Transient` state to register it without a permanent commit, so we
        // write straight through `IMeshStorage`. This is the *only* IMeshService
        // method allowed to bypass the hub-message pipeline — every other CRUD method
        // routes through `Post + RegisterCallback` and never touches persistence.
        var transientNode = node with { State = MeshNodeState.Transient };
        return persistence.SaveNode(transientNode);
    }

    public IObservable<MeshNode> CopyNode(string sourcePath, string targetPath,
        bool includeDescendants = true, bool includeSatellites = false)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var req = new CopyNodeRequest(sourcePath, targetPath)
            {
                IncludeDescendants = includeDescendants,
                IncludeSatellites = includeSatellites
            };
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(req, o => ConfigurePost(o, captured));
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }
            if (delivery == null)
            {
                observer.OnError(new InvalidOperationException("CopyNode: hub.Post returned null."));
                return Disposable.Empty;
            }
            return Observable.FromAsync(() =>
                    hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), default))
                .Subscribe(
                    d =>
                    {
                        if (d.Message is CopyNodeResponse r)
                        {
                            if (r.Success && r.Node != null)
                            {
                                observer.OnNext(r.Node);
                                observer.OnCompleted();
                            }
                            else
                            {
                                observer.OnError(r.RejectionReason switch
                                {
                                    NodeCopyRejectionReason.TargetAlreadyExists =>
                                        new InvalidOperationException(r.Error ?? "Target already exists"),
                                    NodeCopyRejectionReason.SourceNotFound =>
                                        new InvalidOperationException($"Source node not found: {sourcePath}"),
                                    NodeCopyRejectionReason.Unauthorized =>
                                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                                    _ => new InvalidOperationException(r.Error ?? "Node copy failed")
                                });
                            }
                        }
                        else
                        {
                            observer.OnError(new InvalidOperationException(
                                $"CopyNode: unexpected response type {d.Message?.GetType().Name ?? "null"}"));
                        }
                    },
                    observer.OnError);
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
