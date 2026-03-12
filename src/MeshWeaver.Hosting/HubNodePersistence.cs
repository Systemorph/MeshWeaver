using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Internal helper for routing node CRUD operations through the message bus.
/// Uses Post + RegisterCallback instead of AwaitResponse to avoid deadlocks
/// when called from within the hub's execution pipeline.
/// Identity is always resolved from AccessService.Context (set by the calling code
/// or via using (accessService.ImpersonateAsHub(hub)) for hub-level operations).
/// </summary>
internal sealed class HubNodePersistence(
    IMessageHub hub,
    MeshCatalog catalog)
{
    private PostOptions ConfigurePost(PostOptions o)
        => o.WithTarget(catalog.MeshAddress);

    public async Task<MeshNode> CreateNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var delivery = hub.Post(new CreateNodeRequest(node), ConfigurePost)!;
        var response = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), ct);
        var r = ((IMessageDelivery<CreateNodeResponse>)response).Message;
        if (r.Success && r.Node != null)
            return r.Node;
        throw r.RejectionReason switch
        {
            NodeCreationRejectionReason.ValidationFailed =>
                new UnauthorizedAccessException(r.Error ?? "Access denied"),
            NodeCreationRejectionReason.NodeAlreadyExists =>
                new InvalidOperationException($"Node already exists: {node.Path}"),
            _ => new InvalidOperationException(r.Error ?? "Node creation failed")
        };
    }

    public async Task<MeshNode> UpdateNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var delivery = hub.Post(new UpdateNodeRequest(node), ConfigurePost)!;
        var response = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), ct);
        var r = ((IMessageDelivery<UpdateNodeResponse>)response).Message;
        if (r.Success && r.Node != null)
            return r.Node;
        throw r.RejectionReason switch
        {
            NodeUpdateRejectionReason.ValidationFailed =>
                new UnauthorizedAccessException(r.Error ?? "Access denied"),
            NodeUpdateRejectionReason.NodeNotFound =>
                new InvalidOperationException($"Node not found: {node.Path}"),
            _ => new InvalidOperationException(r.Error ?? "Node update failed")
        };
    }

    public async Task DeleteNodeAsync(string path, CancellationToken ct = default)
    {
        var delivery = hub.Post(new DeleteNodeRequest(path) { Recursive = true }, ConfigurePost)!;
        var response = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), ct);
        var r = ((IMessageDelivery<DeleteNodeResponse>)response).Message;
        if (r.Success)
            return;
        throw r.RejectionReason switch
        {
            NodeDeletionRejectionReason.ValidationFailed =>
                new UnauthorizedAccessException(r.Error ?? "Access denied"),
            NodeDeletionRejectionReason.NodeNotFound =>
                new InvalidOperationException($"Node not found: {path}"),
            _ => new InvalidOperationException(r.Error ?? "Node deletion failed")
        };
    }

    public Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default)
        => catalog.CreateTransientNodeAsync(node, ct);
}
