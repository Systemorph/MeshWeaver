using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Graph.Configuration;
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
    INodeTypeStreamCache streamCache,
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
            // Wait for the target's NodeType to be ready (compiled, with
            // AssemblyLocation set) before forwarding. The cache subscribes
            // to the synchronization stream and fires CompilationStatus =
            // Pending on the NodeType MeshNode if no release is present —
            // by the time the chain emits, the CompileWatcher has either
            // already run or finished and the stream re-emitted. Single
            // observable chain, one await at the irreducible Orleans
            // boundary. Per ~/.claude/plans/splendid-sauteeing-garden.md.
            await WaitForCompileReady(grainKey).ToTask();

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

    /// <summary>
    /// Composes the activation-readiness wait as a single <see cref="IObservable{T}"/>
    /// chain — no per-step <c>await</c>, no <c>Task</c> bridge mid-flow. The chain:
    /// <list type="number">
    ///   <item>Subscribes to the cached MeshNode stream for <paramref name="grainKey"/>.</item>
    ///   <item>Filters for "ready" emissions: static-registration NodeTypes
    ///     (with <see cref="MeshNode.HubConfiguration"/> set) pass immediately;
    ///     compiled NodeTypes need <see cref="MeshNode.AssemblyLocation"/> populated;
    ///     instance nodes fall through (their <c>NodeType</c> reference is awaited
    ///     in the inner SelectMany).</item>
    ///   <item><c>SelectMany</c>: if the emitted node is itself a
    ///     <see cref="NodeTypeDefinition"/>, we're done — return it. If it's an
    ///     instance, switch to the cached stream of its NodeType and await
    ///     activation-readiness there.</item>
    ///   <item><c>Timeout(30s)</c>: a wedged compile / unreachable NodeType
    ///     surfaces as <see cref="TimeoutException"/>. Caller's catch falls
    ///     back to the existing stream-delivery path; the grain has its own
    ///     15s safeguard on top.</item>
    /// </list>
    /// </summary>
    private IObservable<MeshNode> WaitForCompileReady(string grainKey) =>
        streamCache.GetStream(grainKey)
            .Where(IsActivationReady)
            .Take(1)
            .SelectMany(node =>
            {
                // NodeType-itself: ready means AssemblyLocation populated, which
                // IsActivationReady already enforced. Done.
                if (node.Content is NodeTypeDefinition)
                    return Observable.Return(node);

                // Instance with no NodeType reference (or pointing to the meta
                // "NodeType" path): nothing more to await — the activation
                // grain consumes whatever's already cached.
                if (string.IsNullOrEmpty(node.NodeType)
                    || string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
                    return Observable.Return(node);

                // Instance node — switch to the NodeType's cached stream and
                // wait for IT to be activation-ready (AssemblyLocation set
                // either by the CompileWatcher or by static registration).
                return streamCache.GetStream(node.NodeType)
                    .Where(IsActivationReady)
                    .Take(1);
            })
            .Timeout(TimeSpan.FromSeconds(30));

    private static bool IsActivationReady(MeshNode node)
    {
        // Static-registration NodeTypes carry HubConfiguration directly; treat
        // those as ready even without AssemblyLocation (they're code-shipped,
        // not Roslyn-compiled).
        if (node.HubConfiguration is not null) return true;

        // Compiled NodeTypes (NodeType-content) need AssemblyLocation set by
        // the CompileWatcher. Instance nodes pass through immediately —
        // the SelectMany handles their NodeType-level wait separately.
        if (node.Content is NodeTypeDefinition)
            return !string.IsNullOrEmpty(node.AssemblyLocation);

        return true;
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

