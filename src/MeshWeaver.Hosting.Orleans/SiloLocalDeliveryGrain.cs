using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The silo side of <see cref="ISiloLocalDeliveryGrain"/> (#7842): hands a delivery to a hub THIS
/// silo hosts, through the same local route table <see cref="PodHubGrain"/> delivers through, without
/// any grain-directory lookup of the hub's own address.
///
/// <para>Keyed by the silo it serves and placed there by the caller's placement hint. An activation
/// that finds itself on another silo answers <c>false</c> and deactivates on idle, so a stale
/// directory entry for this key cannot keep catching calls meant for the silo the key names; the next
/// call is placed afresh by its hint. It never carries a delivery on to the recipient itself: the
/// router asks every member silo, so a silo it cannot reach this way is reported by the router, which
/// counts the answers.</para>
///
/// <para>Not <c>[Reentrant]</c> and holding no state: each call is one synchronous route lookup and an
/// O(1) hand-off into the target hub's own queue, the same hand-off <see cref="PodHubGrain.Deliver"/>
/// makes, for the same reasons (never hold the turn for the hub's processing).</para>
/// </summary>
/// <param name="meshHub">Mesh hub, used only to resolve this silo's routing service.</param>
/// <param name="localSilo">This silo's identity, compared with the grain key.</param>
/// <param name="logger">Logger for misplaced calls and faulted hand-offs.</param>
[global::Orleans.Placement.PreferLocalPlacement]
internal sealed class SiloLocalDeliveryGrain(
    IMessageHub meshHub,
    ILocalSiloDetails localSilo,
    ILogger<SiloLocalDeliveryGrain> logger)
    : Grain, ISiloLocalDeliveryGrain
{
    /// <summary>This silo's local route table — the one <see cref="PodHubGrain"/> delivers through.</summary>
    private readonly OrleansRoutingService? localRoutes =
        meshHub.ServiceProvider.GetService<IRoutingService>() as OrleansRoutingService;

    private bool OnRecipient =>
        string.Equals(this.GetPrimaryKeyString(), localSilo.SiloAddress.ToParsableString(), StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<bool> DeliverToLocalRoute(IMessageDelivery delivery)
    {
        if (!OnRecipient)
        {
            logger.LogDebug(
                "[POD-HUB] A local delivery of {MessageType} ({Id}) addressed to silo {Recipient} landed on silo {Here} — "
                + "stepping aside so the next call is placed on the recipient",
                delivery.Message?.GetType().Name, delivery.Id, this.GetPrimaryKeyString(), localSilo.SiloAddress);
            StepAside();
            return Task.FromResult(false);
        }

        if (delivery.Target is not { } target
            || localRoutes?.TryGetLocalRoute(RoutingGrain.GetHostAddress(target)) is not { } route)
            return Task.FromResult(false);

        // HAND OFF, DO NOT AWAIT THE HUB — PodHubGrain.Deliver's rule and reasons. A faulted hand-off
        // is a lost message, so it is loud.
        route.Invoke(delivery, CancellationToken.None)
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[POD-HUB] Local delivery of {MessageType} ({Id}) to {Target} faulted — message dropped",
                    delivery.Message?.GetType().Name, delivery.Id, delivery.Target));
        return Task.FromResult(true);
    }

    private void StepAside()
    {
        try { DeactivateOnIdle(); }
        catch (InvalidOperationException ex)
        {
            // The activation died between the check and the call: it has already stepped aside.
            logger.LogDebug(ex, "[POD-HUB] a misplaced local-delivery activation was already gone");
        }
    }
}
