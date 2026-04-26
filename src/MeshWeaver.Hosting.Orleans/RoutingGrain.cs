using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

[StatelessWorker(1)]
internal class RoutingGrain(
    IPathResolver pathResolver,
    ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
{
    public async Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery)
    {
        var address = GetHostAddress(delivery.Target!);
        var addressPath = address.ToString();

        var resolution = await pathResolver.ResolvePath(addressPath).FirstAsync().ToTask();
        var grainKey = resolution?.Prefix ?? addressPath;

        logger.LogDebug("RouteMessage: {MessageType} â†’ address={Address}, resolved={Prefix}, remainder={Remainder}, grainKey={GrainKey}",
            delivery.Message.GetType().Name, addressPath, resolution?.Prefix ?? "(null)",
            resolution?.Remainder ?? "(null)", grainKey);

        // ============================================================================
        // 🚨🚨🚨  NO  FUCKING  FALLBACK  🚨🚨🚨
        // ============================================================================
        // If `resolution.Remainder` is non-empty, the exact requested address has NO
        // grain/hub of its own — only an ancestor exists. DO NOT FALL BACK to that
        // ancestor.
        //
        // A non-empty remainder almost always means the node is broken — no NodeType,
        // an invalid NodeType, or the node simply doesn't exist. Forwarding to the
        // closest ancestor would let it answer with its OWN data (e.g.
        // MeshNodeReference returns the ancestor's MeshNode), and callers would get
        // back the wrong data instead of seeing absence/failure.
        //
        // ⛔️ DO NOT add an "exception" here. DO NOT redirect to the prefix. DO NOT
        // ⛔️ store the remainder as `UnifiedPath`. The mesh must surface the broken
        // ⛔️ node honestly so it can be fixed at its source.
        //
        // The right response is NotFound. Period.
        // ============================================================================
        if (resolution != null && !string.IsNullOrEmpty(resolution.Remainder))
        {
            var failureMessage = $"No node found at '{addressPath}'. " +
                $"Closest ancestor is '{resolution.Prefix}' (remainder='{resolution.Remainder}'). " +
                $"This usually means the node is missing, has no NodeType, or has an invalid NodeType.";
            logger.LogWarning("RouteMessage: NotFound for {MessageType} → {Address}. {FailureMessage}",
                delivery.Message.GetType().Name, addressPath, failureMessage);
            return delivery.Failed(failureMessage);
        }

        // Portal/client hubs are not grains â€” deliver via Orleans memory stream.
        // The portal subscribes to this stream in OrleansRoutingService.RegisterStreamAsync.
        if (address.Type == AddressExtensions.PortalType || address.Type == "client")
        {
            logger.LogDebug("RouteMessage: delivering to {Address} via memory stream (not a grain)", addressPath);
            var stream = this.GetStreamProvider(StreamProviders.Memory)
                .GetStream<IMessageDelivery>(addressPath);
            await stream.OnNextAsync(delivery);
            return delivery.Forwarded(address);
        }

        try
        {
            var grain = GrainFactory.GetGrain<IMessageHubGrain>(grainKey);
            return await grain.DeliverMessage(delivery);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grain delivery failed for {MessageType} to {Address} (key={Key}), falling back to stream",
                delivery.Message.GetType().Name, address, grainKey);
            var stream = this.GetStreamProvider(StreamProviders.Memory)
                .GetStream<IMessageDelivery>(addressPath);
            await stream.OnNextAsync(delivery);
            return delivery.Forwarded(address);
        }
    }

    internal static Address GetHostAddress(Address address)
    {
        if (address.Host != null)
        {
            var host = GetHostAddress(address.Host);
            if (host.Type == AddressExtensions.MeshType)
                return address with { Host = null };
            return host;
        }
        return address;
    }
}

