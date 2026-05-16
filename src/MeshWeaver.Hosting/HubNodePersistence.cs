using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Internal helper for routing node CRUD operations through the message bus.
/// Uses hub.Observe (pre-register-then-post) — the typed overload registers the
/// response subject BEFORE the request is dispatched, eliminating the
/// sync-handler-responds-before-subscribe race.
/// Identity is captured eagerly from AccessService and stamped on each delivery.
/// </summary>
internal sealed class HubNodePersistence(
    IMessageHub hub)
{
    private PostOptions ConfigurePost(PostOptions o)
        => o.WithTarget(hub.Address);

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
        return hub.Observe(new CreateNodeRequest(node), o => ConfigureWithIdentity(o, captured))
            .SelectMany(d =>
            {
                var r = d.Message;
                if (r.Success && r.Node != null)
                    return Observable.Return(r.Node);
                return Observable.Throw<MeshNode>(r.RejectionReason switch
                {
                    NodeCreationRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                    NodeCreationRejectionReason.NodeAlreadyExists =>
                        new InvalidOperationException($"Node already exists: {node.Path}"),
                    _ => new InvalidOperationException(r.Error ?? "Node creation failed")
                });
            });
    }

    public IObservable<MeshNode> UpdateNode(MeshNode node)
    {
        var captured = CaptureContext();
        return hub.Observe(new UpdateNodeRequest(node), o => ConfigureWithIdentity(o, captured))
            .SelectMany(d =>
            {
                var r = d.Message;
                if (r.Success && r.Node != null)
                    return Observable.Return(r.Node);
                return Observable.Throw<MeshNode>(r.RejectionReason switch
                {
                    NodeUpdateRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                    NodeUpdateRejectionReason.NodeNotFound =>
                        new InvalidOperationException($"Node not found: {node.Path}"),
                    _ => new InvalidOperationException(r.Error ?? "Node update failed")
                });
            });
    }

    public IObservable<bool> DeleteNode(string path)
    {
        var captured = CaptureContext();
        return hub.Observe(new DeleteNodeRequest(path) { Recursive = true },
                o => ConfigureWithIdentity(o, captured))
            .SelectMany(d =>
            {
                var r = d.Message;
                if (r.Success)
                    return Observable.Return(true);
                return Observable.Throw<bool>(r.RejectionReason switch
                {
                    NodeDeletionRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                    NodeDeletionRejectionReason.NodeNotFound =>
                        new InvalidOperationException($"Node not found: {path}"),
                    _ => new InvalidOperationException(r.Error ?? "Node deletion failed")
                });
            });
    }

    /// <summary>
    /// Persists a node in <see cref="MeshNodeState.Transient"/> via the
    /// storage adapter directly — the CreateNodeRequest pipeline would force
    /// the node to <c>Active</c>. This is the only CRUD path that bypasses
    /// hub messaging, mirroring <see cref="MeshService.CreateTransient"/>.
    /// </summary>
    public IObservable<MeshNode> CreateTransient(MeshNode node)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return CreateNode(node);
        var transient = node with { State = MeshNodeState.Transient };
        return persistence.Write(transient, hub.JsonSerializerOptions);
    }
}
