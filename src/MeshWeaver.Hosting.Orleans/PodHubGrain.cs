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

    /// <summary>
    /// Set by <see cref="Detach"/>: the owner released this address. From then until the activation
    /// is collected, <see cref="Deliver"/> answers with the TERMINAL shape
    /// (<see cref="PodHubNotHereException.Released"/>) rather than the transient "no local route",
    /// and a successful <see cref="Attach"/> clears it — a hub that re-registers under the same
    /// address (a process-level hub coming back on the same silo) claims it afresh.
    /// </summary>
    private volatile bool released;

    /// <summary>
    /// How long a released address stays behind as a tombstone that answers terminally.
    ///
    /// <para>The tombstone has to outlive the owner-side fan-out that is still aimed at the dead
    /// address: the owner learns the subscriber is gone from the NACK to its NEXT push, and for a
    /// quiet stream that push can be minutes away. Ten minutes is at or past the sync stream's own
    /// idle release, so any stream that would otherwise have stormed until that release meets its
    /// terminal verdict first. The cost is one idle activation per released address for that long
    /// — nothing runs in it. After collection a later delivery re-creates the activation on the
    /// caller (prefer-local) and gets the transient shape again, exactly as before this existed;
    /// the tombstone narrows the window, the eviction it triggers is what closes the loop.</para>
    /// </summary>
    internal static readonly TimeSpan ReleasedTombstoneLifetime = TimeSpan.FromMinutes(10);

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
        released = false;
        TryDelayDeactivation(TimeSpan.MaxValue);
        logger.LogDebug("[POD-HUB] {Address} attached and pinned on this silo", AddressPath);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task Detach()
    {
        // 🚨 A RELEASE IS A FACT THE CLUSTER MUST KEEP, briefly. Deactivating on idle here (the
        // previous behaviour) threw that fact away: the next delivery to the address re-created the
        // activation on the CALLER's silo (prefer-local), which has no local route either, and the
        // router could only answer "no silo serves this hub right now" — transient by construction,
        // which the owner-side eviction (#2426/#2546) rightly ignores (#2756). Net effect: a closed
        // circuit's owner fanned every change out to the corpse until the stream's idle release.
        // Keeping the activation as a tombstone lets Deliver answer with the one thing the caller
        // cannot otherwise learn — that the owner said goodbye — so the eviction fires on the very
        // next push. See PodHubNotHereException.Released.
        released = true;
        logger.LogDebug(
            "[POD-HUB] {Address} released by its owner — tombstone answers terminally for {Lifetime}",
            AddressPath, ReleasedTombstoneLifetime);
        TryDelayDeactivation(ReleasedTombstoneLifetime);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IMessageDelivery> Deliver(IMessageDelivery delivery)
    {
        Address address = AddressPath;
        if (released)
        {
            // The owner released this address (Detach) and nothing has re-claimed it (a re-claim
            // goes through Attach, which clears the flag). Terminal: the router stamps NotFound +
            // TargetUnserved and the owner drops its server-side stream instead of pushing again.
            logger.LogDebug(
                "[POD-HUB] {Address} was released by its owner — answering the TERMINAL refusal so the "
                + "sender's owner-side eviction can act on it ({MessageType} {Id})",
                AddressPath, delivery.Message?.GetType().Name, delivery.Id);
            throw new PodHubNotHereException(AddressPath, SiloIdentity, released: true);
        }

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

        // 🚨 THE ACKNOWLEDGEMENT CARRIES THE VERDICT, NOT THE BODY — issue #3045. This leg is the
        // starkest of the three: BuildPodHubRoute discards the result outright
        // (.Select(_ => Unit.Default)), so the body's entire return trip — an Orleans JsonCodec deep
        // copy of the whole payload, then a frame back across the wire — bought nothing at all. See
        // DeliveryPayloadBounds.WithoutEchoedPayload.
        return Task.FromResult(
            DeliveryPayloadBounds.WithoutEchoedPayload(delivery).Forwarded(address));
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
