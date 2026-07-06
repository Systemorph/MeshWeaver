using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    private TimeSpan OpTimeout =>
        (hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions()).Timeout;

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
        // Canonical write via the mesh-node stream (UpdateNodeRequest retired). The owning
        // hub applies the RFC 7396 patch and re-validates RLS + stamps auditing; emits the
        // optimistic snapshot. See MeshService.UpdateNode for the full rationale.
        // Use the live lambda parameter as the write base and carry ITS version — a
        // subscriber never mints a version; the owner assigns the fresh one on apply.
        => Observable.Defer(() => hub.GetMeshNodeStream(node.Path)
                .Update(live => node with { Version = live.Version }))
            .CarryAccessContext(hub.ServiceProvider);

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
}
