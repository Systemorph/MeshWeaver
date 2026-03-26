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
    /// Creates a node via CreateNodeRequest. Uses RegisterCallback to handle the response.
    /// </summary>
    public IObservable<MeshNode> CreateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var cts = new CancellationTokenSource();
            var delivery = hub.Post(new CreateNodeRequest(node),
                o => ConfigureWithIdentity(o, captured))!;

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

    /// <summary>
    /// Updates a node via workspace remote stream Update.
    /// </summary>
    public IObservable<MeshNode> UpdateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var cts = new CancellationTokenSource();
            var delivery = hub.Post(new UpdateNodeRequest(node),
                o => ConfigureWithIdentity(o, captured))!;

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

    /// <summary>
    /// Deletes a node via DeleteNodeRequest with callback.
    /// </summary>
    public IObservable<bool> DeleteNode(string path)
    {
        var captured = CaptureContext();
        return Observable.Create<bool>(observer =>
        {
            var cts = new CancellationTokenSource();
            var delivery = hub.Post(new DeleteNodeRequest(path) { Recursive = true },
                o => ConfigureWithIdentity(o, captured))!;

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
        => Observable.FromAsync(() => catalog.CreateTransientNodeAsync(node));
}
