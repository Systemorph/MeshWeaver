using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Internal helper for routing node CRUD operations through the message bus.
/// Uses Post/RegisterCallback pattern (no ContinueWith, no blocking).
/// Identity is captured eagerly from AccessService and stamped on each delivery.
/// </summary>
internal sealed class HubNodePersistence(
    IMessageHub hub,
    MeshCatalog catalog)
{
    private PostOptions ConfigurePost(PostOptions o)
        => o.WithTarget(catalog.MeshAddress);

    private AccessContext? CaptureContext()
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context ?? accessService?.CircuitContext;
    }

    private PostOptions ConfigureWithIdentity(PostOptions o, AccessContext? captured)
    {
        o = ConfigurePost(o);
        return captured != null ? o.WithAccessContext(captured) : o;
    }

    /// <summary>
    /// Creates a node via CreateNodeRequest. Subscribes to RegisterCallback's Task so
    /// DeliveryFailureException surfaces via onError (the user callback short-circuits
    /// for DeliveryFailure — without this Subscribe, a routing failure would hang forever).
    /// </summary>
    public IObservable<MeshNode> CreateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(new CreateNodeRequest(node),
                    o => ConfigureWithIdentity(o, captured));
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

    /// <summary>
    /// Updates a node via UpdateNodeRequest. See <see cref="CreateNode"/> for the
    /// DeliveryFailure-handling rationale.
    /// </summary>
    public IObservable<MeshNode> UpdateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(new UpdateNodeRequest(node),
                    o => ConfigureWithIdentity(o, captured));
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

    /// <summary>
    /// Deletes a node via DeleteNodeRequest. See <see cref="CreateNode"/> for the
    /// DeliveryFailure-handling rationale.
    /// </summary>
    public IObservable<bool> DeleteNode(string path)
    {
        var captured = CaptureContext();
        return Observable.Create<bool>(observer =>
        {
            IMessageDelivery? delivery;
            try
            {
                delivery = hub.Post(new DeleteNodeRequest(path) { Recursive = true },
                    o => ConfigureWithIdentity(o, captured));
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
        => catalog.CreateTransientNode(node);
}
