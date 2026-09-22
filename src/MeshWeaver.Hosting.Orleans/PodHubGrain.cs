using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
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
///     with an owner placement hint. <c>[PreferLocalPlacement]</c> honours that hint, including when
///     a claim migrates an activation off another silo. The owner then pins it
///     with <c>DelayDeactivation</c>, because prefer-local prefers the *caller*: a collected
///     activation would be re-created on whichever router called next, and every delivery to a
///     perfectly healthy hub would then land on the wrong process.</item>
///   <item><b>A silo that cannot serve it steps aside, loudly.</b> No throwing
///     <c>OnActivateAsync</c> (that hides the reason inside an Orleans activation failure): the call
///     itself answers. <see cref="Deliver"/> throws <see cref="PodHubNotHereException"/> without
///     ending the activation, so queued deliveries receive that same verdict. A refused
///     <see cref="Attach"/> requests migration to its owner's hinted silo and answers <c>false</c>.
///     A legacy/client claim without that hint deactivates instead. That is how an address MOVING
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
            // The activation landed on a silo that does not own the address. Relocate to the
            // owner's hint so a stale directory entry cannot recreate it on this silo again.
            logger.LogInformation(
                "[POD-HUB] Attach for {Address} landed on silo {Silo}, which has no local route for it "
                + "({LocalRoutes} routing service resolved) — stepping aside so the owner's retry can "
                + "claim it. Expected while a hub MOVES between pods.",
                AddressPath, SiloIdentity ?? "(unknown)",
                localRoutes is null ? "NO" : "a");
            TryRelocateOnIdle();
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
        // 🚨 A RELEASE REMOVES WHAT ITS REGISTRATION REGISTERED — the same rule the two registries
        // behind it already keep (#4741 for the hosted-hub registry, #5159 for the local route), and
        // the third and last place it was missing (#5136). RegisterStream's disposal removes its own
        // local route, value-matched, BEFORE it releases this claim; so if this silo still has a live
        // route for the address when the release arrives, it belongs to a SUCCESSOR that registered
        // under the same address since (RegisterStream is last-writer-wins). This Detach is then the
        // predecessor's, and it is stale. Honouring it would stamp the terminal Released tombstone on
        // a LIVE hub — every delivery refused for ten minutes with the one verdict the owner-side
        // eviction acts on, and the pin cut from indefinite to the tombstone's lifetime — which is
        // strictly worse than the fault a retire-and-replace exists to cure. The grain is not
        // reentrant, so this read is ordered against the successor's Attach and every Deliver: there
        // is no moment at which the route is there and the successor is not.
        Address address = AddressPath;
        if (localRoutes?.TryGetLocalRoute(address) is not null)
        {
            logger.LogDebug(
                "[POD-HUB] {Address}: a release arrived while this silo holds a live local route for it — "
                + "a successor has registered under the address, so the release is the predecessor's and "
                + "is not honoured. The claim stays as the successor's Attach left it.",
                AddressPath);
            return Task.CompletedTask;
        }

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
            // it, but a delivery cannot establish an ownership claim, so the retry would not
            // converge. This is a definitive answer about a transport, and the router reads it as
            // "fall back" during the roll and as "NACK the sender" after it.
            logger.LogInformation(
                "[POD-HUB] {Address} has no local route on silo {Silo} — the owner is gone, or its claim "
                + "is not held there. Answering PodHubNotHere; the router decides what to do with it.",
                AddressPath, SiloIdentity ?? "(unknown)");
            // A delivery is not an ownership claim (#2299/#5177). Deactivating here forwards the
            // calls already queued on this activation to a fresh one, whose first refusal also
            // deactivates it. Orleans exhausts its forwarding budget and those callers receive an
            // invalid-activation rejection instead of this refusal. Keep serving the verdict;
            // Attach still steps aside when an actual owner asks to claim the address elsewhere.
            // Cancel a previous owner's pin if its Detach did not arrive. Zero restores ordinary
            // idle collection; it does not deactivate while queued deliveries still need an answer.
            TryDelayDeactivation(TimeSpan.Zero);
            // 🚨 NAME THE SILO. This is the one fact the production refusal could not carry: a
            // router that says "no silo in this cluster is currently serving that hub" cannot tell
            // "the owner answered no" from "prefer-local placed an unclaimed activation on ME,
            // because the directory has no entry at all". Those are different faults (#2938), and
            // the second used to re-create the activation on every refusal. With the silo named,
            // one log line separates them without relying on activation churn to find the owner.
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

    private void TryRelocateOnIdle()
    {
        if (deactivated) return;
        try
        {
            // A stale directory entry can recreate a deactivated grain on this same silo without
            // running placement again. Migration updates that entry and forwards queued calls to
            // the owner carried by Attach's scoped hint; deactivation alone cannot move it.
            if (RequestContext.Get(IPlacementDirector.PlacementHintKey) is SiloAddress owner
                && !owner.Equals(localSilo?.SiloAddress))
                MigrateOnIdle();
            else
                DeactivateOnIdle();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex,
                "[POD-HUB] {Address}: Relocation after the activation died — already achieved",
                AddressPath);
        }
    }
}
