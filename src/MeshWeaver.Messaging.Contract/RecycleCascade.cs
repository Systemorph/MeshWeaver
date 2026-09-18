namespace MeshWeaver.Messaging;

/// <summary>
/// Hung on a hub with <c>hub.Set(new RecycleCascade(...))</c>: what a routed <see cref="DisposeRequest"/>
/// on this hub must ALSO tear down — its dependency network — invoked by <c>MessageHub.HandleDispose</c>
/// on the recycle's own turn, BEFORE the hub's own teardown starts, and only for a request that is
/// not itself a cascade (<see cref="DisposeRequest.CascadedFrom"/> is <c>null</c>).
///
/// <para><b>Why a seam and not a handler.</b> The hub that knows its network is the one being torn
/// down; it can compute the set but must not DELIVER the requests — a dying hub cannot deliver its
/// own last frame (see <c>HubRecycleExtensions</c>). The implementation therefore issues the cascade
/// from a SURVIVING hub, exactly as <see cref="RecycleAnnouncement"/> delivers through the parent.
/// The framework registers the one real implementation on NodeType definition hubs
/// (<c>NodeTypeRecycleCascade</c>): the dependent NodeTypes (those sharing this type's sources,
/// transitively) and every instance of each. A cascaded request reaching an address with no live
/// activation is a no-op at the routing layer — nothing is instantiated in order to be recycled.</para>
///
/// <para>Maintainer, 2026-09-18: <i>"everything involved must be properly disposed ⇒ implement logic
/// mesh side and just recycle main bit. all sub-bits (dependency network) should only be recycled if
/// instantiated in the first place."</i> Before this, an operator recycled the definition and every
/// instance hub kept serving the assembly it was born with — the stale state that made people reach
/// for image pins instead.</para>
/// </summary>
/// <param name="Cascade">Invoked once per direct recycle, with the request that started it.</param>
public sealed record RecycleCascade(Action<DisposeRequest> Cascade);
