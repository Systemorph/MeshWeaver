using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Internal helper for routing node CRUD operations through the message bus.
/// Observable methods capture AccessContext eagerly at call time and stamp it
/// on the delivery via PostOptions.WithAccessContext. This ensures identity
/// flows correctly even when the Observable is subscribed from ContinueWith
/// or other non-hub contexts where AsyncLocal is null.
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

    public IObservable<MeshNode> CreateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var delivery = hub.Post(new CreateNodeRequest(node),
                o => ConfigureWithIdentity(o, captured))!;
            var cts = new CancellationTokenSource();
            hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled) { observer.OnError(new OperationCanceledException()); return; }
                    if (t.IsFaulted) { observer.OnError(t.Exception!.Flatten().InnerException!); return; }
                    var r = ((IMessageDelivery<CreateNodeResponse>)t.Result).Message;
                    if (r.Success && r.Node != null)
                    { observer.OnNext(r.Node); observer.OnCompleted(); }
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
                });
            return Disposable.Create(() => cts.Cancel());
        });
    }

    public IObservable<MeshNode> UpdateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return Observable.Create<MeshNode>(observer =>
        {
            var delivery = hub.Post(new UpdateNodeRequest(node),
                o => ConfigureWithIdentity(o, captured))!;
            var cts = new CancellationTokenSource();
            hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled) { observer.OnError(new OperationCanceledException()); return; }
                    if (t.IsFaulted) { observer.OnError(t.Exception!.Flatten().InnerException!); return; }
                    var r = ((IMessageDelivery<UpdateNodeResponse>)t.Result).Message;
                    if (r.Success && r.Node != null)
                    { observer.OnNext(r.Node); observer.OnCompleted(); }
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
                });
            return Disposable.Create(() => cts.Cancel());
        });
    }

    public IObservable<bool> DeleteNode(string path)
    {
        var captured = CaptureContext();
        return Observable.Create<bool>(observer =>
        {
            var delivery = hub.Post(new DeleteNodeRequest(path) { Recursive = true },
                o => ConfigureWithIdentity(o, captured))!;
            var cts = new CancellationTokenSource();
            hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled) { observer.OnError(new OperationCanceledException()); return; }
                    if (t.IsFaulted) { observer.OnError(t.Exception!.Flatten().InnerException!); return; }
                    var r = ((IMessageDelivery<DeleteNodeResponse>)t.Result).Message;
                    if (r.Success)
                    { observer.OnNext(true); observer.OnCompleted(); }
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
                });
            return Disposable.Create(() => cts.Cancel());
        });
    }

    public IObservable<MeshNode> CreateTransient(MeshNode node)
        => Observable.FromAsync(() => catalog.CreateTransientNodeAsync(node));
}

