using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Services;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The silo side of <see cref="ISiloLocalDeliveryService"/> (#7842): hands a delivery to a hub THIS
/// silo hosts, through the same local route table <see cref="PodHubGrain"/> delivers through, without
/// any grain-directory lookup of the hub's own address. See the interface for why a grain service.
///
/// <para>Holds no state. Each call is one synchronous route lookup and an O(1) hand-off into the
/// target hub's own queue — the hand-off <see cref="PodHubGrain.Deliver"/> makes, for the same reasons
/// (never hold the turn for the hub's processing). The mesh hub and its routing service are resolved
/// on the first call, not at construction: grain services are built while the silo starts, before the
/// mesh hub necessarily exists.</para>
/// </summary>
internal sealed class SiloLocalDeliveryService : GrainService, ISiloLocalDeliveryService
{
    private readonly IServiceProvider services;
    private readonly ILogger<SiloLocalDeliveryService> logger;
    private OrleansRoutingService? localRoutes;

    /// <summary>Built by Orleans' grain-service factory at silo start.</summary>
    /// <param name="grainId">The service's id, supplied by Orleans.</param>
    /// <param name="silo">The silo hosting it.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="services">The silo's services, for the mesh hub's routing service.</param>
    public SiloLocalDeliveryService(
        GrainId grainId, Silo silo, ILoggerFactory loggerFactory, IServiceProvider services)
        : base(grainId, silo, loggerFactory)
    {
        this.services = services;
        logger = loggerFactory.CreateLogger<SiloLocalDeliveryService>();
    }

    /// <inheritdoc />
    public Task<bool> DeliverToLocalRoute(IMessageDelivery delivery)
    {
        localRoutes ??= services.GetService<IMessageHub>()?.ServiceProvider.GetService<IRoutingService>()
            as OrleansRoutingService;
        if (delivery.Target is not { } target
            || localRoutes?.TryGetLocalRoute(RoutingGrain.GetHostAddress(target)) is not { } route)
            return Task.FromResult(false);

        // HAND OFF, DO NOT AWAIT THE HUB — PodHubGrain.Deliver's rule and reasons. A faulted hand-off
        // is a lost message, so it is loud.
        route.Invoke(delivery, CancellationToken.None)
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[POD-HUB] Local delivery of {Payload} ({Id}) to {Target} faulted — message dropped",
                    DeliveryPayloadBounds.Quote(DeliveryPayloadBounds.Preview(delivery)), delivery.Id, delivery.Target));
        return Task.FromResult(true);
    }
}

/// <summary>
/// Addresses <see cref="ISiloLocalDeliveryService"/> on a NAMED silo — the one thing
/// <see cref="GrainServiceClient{TGrainService}"/> exposes only to a derived type. A silo-only
/// singleton (registered by <c>ConfigureMeshWeaverServer</c>).
/// </summary>
/// <param name="serviceProvider">The silo's services.</param>
internal sealed class SiloLocalDeliveryClient(IServiceProvider serviceProvider)
    : GrainServiceClient<ISiloLocalDeliveryService>(serviceProvider)
{
    /// <summary>The carrier on <paramref name="silo"/>, addressed directly — no directory, no placement.</summary>
    /// <param name="silo">The silo to address.</param>
    /// <returns>A reference to that silo's service.</returns>
    public ISiloLocalDeliveryService On(SiloAddress silo) => GetGrainService(silo);
}
