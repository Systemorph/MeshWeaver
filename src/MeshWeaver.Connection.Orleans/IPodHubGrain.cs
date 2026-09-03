using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Delivery to a POD-PROCESS hub — a hub that lives in a .NET process rather than in a grain
/// (<c>mesh/{id}</c>, <c>portal/{user}</c>, <c>client/{id}</c>, <c>cache/…</c>, and whatever a
/// module adds through <c>AddStreamRoutedAddressType</c>) — as a directed grain call instead of an
/// Orleans stream publish.
///
/// <para>🚨 <b>Why a grain can address a process at all.</b> Orleans places grains and nothing
/// places a process, which is why this leg was a stream in the first place. The trick is that the
/// grain's IDENTITY is the address and its ACTIVATION is created by the owning process itself
/// (<see cref="Attach"/>, from <c>OrleansRoutingService.RegisterStream</c>, under
/// <c>[PreferLocalPlacement]</c>) — so Orleans' single-activation guarantee turns its own grain
/// directory into the address→silo map, with no directory of ours to write, keep durable, or
/// lose.</para>
///
/// <para><b>What this buys over the stream.</b> A stream publish to nobody SUCCEEDS — silently — and
/// that is issue #1742. A grain call has an outcome: it lands, or it fails and the router NACKs the
/// sender. It also stops depending on Orleans streaming being ready, on the pub-sub registry
/// surviving a silo departure, and on <c>MemoryStreamQueueGrain</c>'s RAM: the local route table it
/// reads is written synchronously and unconditionally by <c>RegisterStream</c>.</para>
///
/// <para>Full mechanism and the two-release roll it must ship under:
/// <c>Doc/Architecture/PodHubDeliveryRollPlan</c>.</para>
/// </summary>
public interface IPodHubGrain : IGrainWithStringKey
{
    /// <summary>
    /// Claims this address for the CALLING silo: verifies that this silo's routing service really
    /// has a local route for it, and if so pins the activation for as long as the registration
    /// lives, so grain collection can never re-place a live hub's activation onto whichever silo
    /// happens to call next.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the activation is on the owning silo and is now pinned. <c>false</c> when it
    /// landed elsewhere — the previous owner's activation has not gone yet — in which case the
    /// activation steps aside and the caller should retry, which is how a hub MOVING between pods
    /// (a <c>portal/{user}</c> circuit reconnecting) converges instead of wedging.
    /// </returns>
    Task<bool> Attach();

    /// <summary>
    /// Releases the address when its registration is disposed, so a hub that moves silos leaves no
    /// activation stranded on the pod it left.
    /// </summary>
    Task Detach();

    /// <summary>
    /// Delivers to the hub this activation fronts.
    /// </summary>
    /// <param name="delivery">The delivery to hand to the local route.</param>
    /// <returns>The delivery, forwarded.</returns>
    /// <exception cref="PodHubNotHereException">
    /// This silo has no local route for the address. Deliberately NOT a transient Orleans rejection:
    /// retrying would re-place the activation on the caller and loop. The router treats it as
    /// "nobody serves this address through the grain transport" and — for one release — falls back
    /// to the stream publish. With <see cref="PodHubNotHereException.Released"/> set, the owner
    /// itself said goodbye (<see cref="Detach"/>) and the router answers TERMINALLY instead — see
    /// that property for why the difference is the whole #2426 eviction.
    /// </exception>
    Task<IMessageDelivery> Deliver(IMessageDelivery delivery);
}

/// <summary>
/// Thrown by <see cref="IPodHubGrain.Deliver"/> when the activation is on a silo that does not host
/// the address's local route. It is a definitive "not through this transport, not here" — never a
/// transient failure to retry — because <c>[PreferLocalPlacement]</c> would place the retry on the
/// caller again and the loop would never converge.
/// </summary>
[global::Orleans.GenerateSerializer]
public sealed class PodHubNotHereException : Exception
{
    /// <summary>Creates the exception for <paramref name="address"/>.</summary>
    /// <param name="address">The address whose local route this silo does not have.</param>
    public PodHubNotHereException(string address)
        : base($"No silo in this cluster serves '{address}' through the pod-hub transport.")
        => Address = address;

    /// <summary>
    /// Creates the exception for <paramref name="address"/>, naming the silo whose activation
    /// answered — the fact a production log could not previously carry.
    /// </summary>
    /// <param name="address">The address whose local route the responding silo does not have.</param>
    /// <param name="respondingSilo">The silo the activation was on, or null when unknown.</param>
    public PodHubNotHereException(string address, string? respondingSilo)
        : base(respondingSilo is null
            ? $"No silo in this cluster serves '{address}' through the pod-hub transport."
            : $"No silo in this cluster serves '{address}' through the pod-hub transport — the "
              + $"activation the call reached is on silo '{respondingSilo}', which has no local "
              + "route for that address.")
    {
        Address = address;
        RespondingSilo = respondingSilo;
    }

    /// <summary>
    /// Creates the exception for an address whose OWNER released it — the
    /// <see cref="Released"/> shape. Distinct from the two-argument form on purpose: that one says
    /// "I cannot find it", this one says "it told me it is gone", and only the second is terminal.
    /// </summary>
    /// <param name="address">The address the owner released.</param>
    /// <param name="respondingSilo">The silo the released activation is on, or null when unknown.</param>
    /// <param name="released">Must be <c>true</c>; the parameter exists so the call site reads as what it is.</param>
    public PodHubNotHereException(string address, string? respondingSilo, bool released)
        : this(address, respondingSilo)
        => Released = released;

    /// <summary>Parameterless ctor for the serializer.</summary>
    public PodHubNotHereException() { }

    /// <summary>
    /// The claim's OWN refusal — <c>Attach</c> answered <c>false</c>, so nothing was ever thrown
    /// across the wire.
    ///
    /// <para>🚨 It reads differently on purpose. This exception is what
    /// <c>OrleansRoutingService.AttachPodHub</c>'s budget-exhausted <c>Warning</c> logs, and in
    /// production that line carried the wire-level text above — which reads as though the cluster
    /// had been asked and had answered, when in fact the OWNER's own claim landed on a silo that is
    /// not it. Those are different faults with different fixes, and eight days of memex-cloud logs
    /// could not tell them apart (#2938).</para>
    /// </summary>
    /// <param name="address">The address whose claim was refused.</param>
    /// <returns>The exception the claim's retry policy treats as "bounce and try again".</returns>
    public static PodHubNotHereException ClaimRefused(string address) =>
        new()
        {
            Address = address,
            ClaimRefusal = true,
        };

    /// <summary>The address that could not be served.</summary>
    [global::Orleans.Id(0)]
    public string? Address { get; set; }

    /// <summary>
    /// The silo whose activation answered, when known. Null on an older peer, and on the
    /// <see cref="ClaimRefused"/> shape (nothing crossed the wire there).
    /// </summary>
    [global::Orleans.Id(1)]
    public string? RespondingSilo { get; set; }

    /// <summary>
    /// True when this is the CLAIM's own refusal (<c>Attach</c> answered <c>false</c>) rather than
    /// a refusal thrown by a remote <c>Deliver</c>. See <see cref="ClaimRefused"/>.
    /// </summary>
    [global::Orleans.Id(2)]
    public bool ClaimRefusal { get; set; }

    /// <summary>
    /// True when the OWNER of this address released it — its registration was disposed
    /// (<c>OrleansRoutingService.RegisterStream</c>'s handle: a Blazor circuit that closed, a hosted
    /// hub that was torn down) and <see cref="IPodHubGrain.Detach"/> ran. The activation stays
    /// behind as a short-lived TOMBSTONE that answers every delivery with this shape.
    ///
    /// <para>🚨 <b>This is the one refusal that is TERMINAL, and the distinction is load-bearing.</b>
    /// Without it the router can only say "no silo serves this hub right now", which it must call
    /// transient (<c>ErrorType.ShuttingDown</c>) because it cannot tell a claim that has not landed
    /// yet from an address nobody will ever claim again — and the owner-side eviction that ends the
    /// fan-out-to-a-corpse storm (#2426/#2546) deliberately does NOT act on a transient verdict
    /// (#2756). So a closed circuit's owner kept fanning every change out to the corpse, each
    /// delivery refused and re-logged, until the stream's own idle release — measured on memex
    /// 2026-09-03 at 300–1,169 refusals per minute for 46 minutes after ONE tab closed, with zero
    /// evictions. A released address is not "moving between pods": Blazor never reuses a closed
    /// circuit id, and a hub that re-registers claims it afresh through <c>Attach</c>, which clears
    /// the tombstone. So the router stamps it <c>NotFound</c> + <c>TargetUnserved</c>, the verdict
    /// the eviction was written for.</para>
    ///
    /// <para>False on an older peer, which degrades to the transient shape — never to a wrong
    /// eviction.</para>
    /// </summary>
    [global::Orleans.Id(3)]
    public bool Released { get; set; }

    /// <inheritdoc />
    public override string Message => ClaimRefusal
        ? $"The pod-hub claim for '{Address}' was refused: Attach answered false, so the activation "
          + "this process reached is on a silo that has no local route for the address. The owner's "
          + "claim has NOT landed and the cluster cannot reach this hub by directed grain call."
        : Released
            ? $"'{Address}' was RELEASED by its owner: the registration that claimed it was disposed, "
              + "so no silo will serve it again through the pod-hub transport. This is a terminal "
              + "verdict, not a roll window."
            : base.Message;
}
