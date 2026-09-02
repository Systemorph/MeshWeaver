using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The silo-side implementation of <see cref="IPodHubGrain"/> — see that interface for why a grain
/// can address a process at all, and <c>Doc/Architecture/PodHubDeliveryRollPlan</c> for the roll.
///
/// <para><b>The whole correctness argument is two lifecycle rules.</b></para>
/// <list type="number">
///   <item><b>Only the owner pins it.</b> <see cref="Attach"/> is called from
///     <c>OrleansRoutingService.RegisterStream</c> on the silo that just created the local route,
///     and <c>[PreferLocalPlacement]</c> places a not-yet-activated grain on the CALLER's silo — so
///     the owner's call is what brings the activation to life in the right process. It then pins it
///     with <c>DelayDeactivation</c>, because prefer-local prefers the *caller*: a collected
///     activation would be re-created on whichever router called next, and every delivery to a
///     perfectly healthy hub would then land on the wrong process.</item>
///   <item><b>A silo that cannot serve it steps aside, loudly.</b> No throwing
///     <c>OnActivateAsync</c> (that hides the reason inside an Orleans activation failure): the call
///     itself answers. <see cref="Deliver"/> throws <see cref="PodHubNotHereException"/> and
///     requests deactivation so the true owner's next <see cref="Attach"/> can be placed correctly.
///     <see cref="Attach"/> answers <c>false</c> and does the same, which is how an address MOVING
///     between pods converges.</item>
/// </list>
///
/// <para>Not <c>[Reentrant]</c>: one activation fronts one process-local hub, the delivery it makes
/// is a synchronous hand-off into that hub's own queue (the hub is what serialises work), and a
/// non-reentrant grain keeps <see cref="Attach"/>/<see cref="Detach"/> strictly ordered against
/// deliveries — which is what makes "claimed, then pinned" a single indivisible step.</para>
/// </summary>
/// <param name="logger">Logger for attach/detach/delivery diagnostics.</param>
/// <param name="meshHub">Mesh hub, used only to resolve this silo's routing service.</param>
/// <param name="localSilo">
/// This silo's own identity, so a refusal can NAME the silo that answered. Optional — a container
/// without Orleans silo services (never production, but several fixtures) resolves null and the
/// refusal degrades to the un-named form.
/// </param>
[global::Orleans.Placement.PreferLocalPlacement]
internal sealed class PodHubGrain(
    ILogger<PodHubGrain> logger,
    IMessageHub meshHub,
    ILocalSiloDetails? localSilo = null)
    : Grain, IPodHubGrain
{
    /// <summary>
    /// This silo's LOCAL route table — the same one <c>OrleansRoutingService.DeliverMessage</c>
    /// short-circuits on and <c>RoutingGrain.PostFailure</c> answers through. It is written
    /// SYNCHRONOUSLY and UNCONDITIONALLY by <c>RegisterStream</c>, before any Orleans streaming
    /// exists, which is precisely the property the stream leg never had: a hub whose stream
    /// subscription was never attached — or was attached and then lost — has always been reachable
    /// here.
    /// </summary>
    private readonly OrleansRoutingService? localRoutes =
        meshHub.ServiceProvider.GetService<IRoutingService>() as OrleansRoutingService;

    /// <summary>
    /// Set at the start of <see cref="OnDeactivateAsync"/> so a lifetime call arriving afterwards is
    /// a graceful no-op rather than Orleans' invalid-activation throw — the same boundary
    /// <c>MessageHubGrain.TryDelayDeactivation</c> documents, and the same reason (a straggler that
    /// throws into an unobserved Task escalates to a catastrophic xUnit failure).
    /// </summary>
    private volatile bool deactivated;

    private string AddressPath => this.GetPrimaryKeyString();

    /// <summary>
    /// This silo's identity, for the refusal lines. Null only where Orleans' silo services are not
    /// in the container.
    /// </summary>
    private string? SiloIdentity => localSilo?.SiloAddress.ToParsableString();

    /// <inheritdoc />
    public Task<bool> Attach()
    {
        Address address = AddressPath;
        if (localRoutes?.TryGetLocalRoute(address) is null)
        {
            // The activation landed on a silo that does not own the address — the previous owner's
            // activation is still alive. Step aside so the caller's retry can be placed on itself.
            logger.LogInformation(
                "[POD-HUB] Attach for {Address} landed on silo {Silo}, which has no local route for it "
                + "({LocalRoutes} routing service resolved) — stepping aside so the owner's retry can "
                + "claim it. Expected while a hub MOVES between pods.",
                AddressPath, SiloIdentity ?? "(unknown)",
                localRoutes is null ? "NO" : "a");
            TryDeactivateOnIdle();
            return Task.FromResult(false);
        }

        // 🚨 PIN IT. Prefer-local prefers the CALLER, so an activation collected for idleness would
        // be re-created on whichever router called next — and every delivery to a live hub would
        // then land on the wrong process, permanently. The activation's lifetime is deliberately
        // tied to the registration, not to traffic.
        TryDelayDeactivation(TimeSpan.MaxValue);
        logger.LogDebug("[POD-HUB] {Address} attached and pinned on this silo", AddressPath);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task Detach()
    {
        logger.LogDebug("[POD-HUB] {Address} detached", AddressPath);
        TryDeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IMessageDelivery> Deliver(IMessageDelivery delivery)
    {
        Address address = AddressPath;
        var route = localRoutes?.TryGetLocalRoute(address);
        if (route is null)
        {
            // Not an Orleans transient rejection, on purpose: DeliverToGrainWithRetry would retry
            // it, prefer-local would place the retry on the caller again, and the loop would never
            // converge. This is a definitive answer about a transport, and the router reads it as
            // "fall back" during the roll and as "NACK the sender" after it.
            logger.LogInformation(
                "[POD-HUB] {Address} has no local route on silo {Silo} — the owner is gone, or its claim "
                + "is not held there. Answering PodHubNotHere; the router decides what to do with it.",
                AddressPath, SiloIdentity ?? "(unknown)");
            TryDeactivateOnIdle();
            // 🚨 NAME THE SILO. This is the one fact the production refusal could not carry: a
            // router that says "no silo in this cluster is currently serving that hub" cannot tell
            // "the owner answered no" from "prefer-local placed a throw-away activation on ME,
            // because the directory has no entry at all". Those are different faults (#2938), and
            // the second is the one that repeats forever — every refusal re-creates the activation
            // on the caller's own silo. With the silo named, one log line separates them.
            throw new PodHubNotHereException(AddressPath, SiloIdentity);
        }

        // 🚨 HAND OFF, DO NOT AWAIT THE HUB. Subscribing invokes the local route, which posts the
        // delivery onto the owning hub's own queue — an O(1) enqueue. The hub's PROCESSING of it is
        // a separate, unbounded thing, and waiting for that here would be two bugs at once: the
        // grain turn would be held for the duration (this grain is not [Reentrant], so the
        // destination's deliveries would serialise behind one slow handler), and a handler whose
        // work routes back to this same address would deadlock against its own turn. The stream
        // handler in OrleansRoutingService.SubscribeWhenStreamingReadyAsync makes exactly this
        // choice, for exactly these reasons, and this is the same hand-off with a real outcome
        // attached: reaching this line at all is what a stream publish could never confirm.
        //
        // onError is therefore mandatory here too — we answer Forwarded immediately, so nothing
        // retries, and a faulted delivery IS a lost message that must be loud.
        route.Invoke(delivery, CancellationToken.None)
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[POD-HUB] Delivery callback faulted for {MessageType} ({Id}) on {Address} — message dropped",
                    delivery.Message?.GetType().Name, delivery.Id, AddressPath));

        return Task.FromResult(delivery.Forwarded(address));
    }

    /// <inheritdoc />
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        deactivated = true;
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    private void TryDelayDeactivation(TimeSpan delay)
    {
        if (deactivated) return;
        try { DelayDeactivation(delay); }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex,
                "[POD-HUB] {Address}: DelayDeactivation after the activation died — keep-alive is moot",
                AddressPath);
        }
    }

    private void TryDeactivateOnIdle()
    {
        if (deactivated) return;
        try { DeactivateOnIdle(); }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex,
                "[POD-HUB] {Address}: DeactivateOnIdle after the activation died — already achieved",
                AddressPath);
        }
    }
}
