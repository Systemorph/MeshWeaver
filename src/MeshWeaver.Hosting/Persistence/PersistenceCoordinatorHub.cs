using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Persistence;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Wires up the per-silo persistence coordinator hub. The hub is registered at
/// <see cref="PersistenceCoordinator.Address"/> and accepts <see cref="WriteRequest"/>
/// messages from any producer in the mesh.
///
/// The hub's single-threaded <c>ActionBlock</c> IS the queue — writes are processed
/// in publish order, one at a time. Storage I/O happens inside the handler; the next
/// message waits until the previous storage call completes (natural backpressure).
///
/// See <c>Doc/Architecture/PersistencePipeline.md</c>.
/// </summary>
public static class PersistenceCoordinatorHub
{
    /// <summary>
    /// Registers the persistence coordinator as a hosted hub on the given mesh.
    /// Call once per silo / process during startup.
    /// </summary>
    public static IMessageHub StartPersistenceCoordinator(this IMessageHub meshHub)
        => meshHub.GetHostedHub(
            PersistenceCoordinator.Address,
            config => config
                .WithHandler<WriteRequest>(HandleWrite));

    private static IMessageDelivery HandleWrite(IMessageHub hub, IMessageDelivery<WriteRequest> delivery)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Hosting.Persistence.PersistenceCoordinator");
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();

        if (storage == null)
        {
            logger.LogError(
                "PersistenceCoordinator: no IStorageAdapter registered. Drop write {Op} {Path}.",
                delivery.Message.Op, delivery.Message.Path);
            return delivery.Failed("No IStorageAdapter registered.");
        }

        // Run the storage I/O on the hub's ActionBlock thread (the hub IS the queue).
        // The handler is synchronous from the hub's perspective — we kick off the work
        // and let it complete naturally; the action block waits for the returned Task
        // before processing the next WriteRequest, which gives us serial-per-silo
        // ordering for free.
        _ = ProcessAsync(delivery.Message, storage, changeFeed, logger);
        return delivery.Processed();
    }

    private static async Task ProcessAsync(
        WriteRequest req,
        IStorageAdapter storage,
        IMeshChangeFeed? changeFeed,
        ILogger logger)
    {
        // Bare-bones implementation. Polly retry pipeline + IPersistenceMonitor stream
        // come in a follow-up — see PersistencePipeline.md for the full plan.
        try
        {
            switch (req.Op)
            {
                case WriteOp.Create:
                case WriteOp.Update:
                    if (req.Node == null)
                    {
                        logger.LogError("PersistenceCoordinator: {Op} for {Path} has null Node payload.", req.Op, req.Path);
                        return;
                    }
                    await storage.WriteAsync(req.Node, defaultJsonOptions);
                    changeFeed?.Publish(req.Op == WriteOp.Create
                        ? MeshChangeEvent.Created(req.Node)
                        : MeshChangeEvent.Updated(req.Node));
                    logger.LogDebug("PersistenceCoordinator: {Op} committed for {Path}", req.Op, req.Path);
                    break;

                case WriteOp.Delete:
                    await storage.DeleteAsync(req.Path);
                    changeFeed?.Publish(MeshChangeEvent.Deleted(req.Path));
                    logger.LogDebug("PersistenceCoordinator: Delete committed for {Path}", req.Path);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "PersistenceCoordinator: {Op} for {Path} failed and was dropped (no retry yet — see PersistencePipeline.md Phase 2).",
                req.Op, req.Path);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions defaultJsonOptions = new();
}
