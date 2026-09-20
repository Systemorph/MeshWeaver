namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>The seam that lets a hub whose address is COMING BACK say goodbye.</b> A higher layer
/// hangs this on a hub (<c>hub.Set(new RecycleAnnouncement(...))</c>) to be handed the ONE moment
/// a hub still has while it is whole: the first statement of its own <c>Dispose()</c>, before
/// <c>IsDisposing</c> flips — reached from a routed <see cref="DisposeRequest"/>'s handler AND from
/// a direct <c>Dispose()</c> such as an Orleans grain deactivation (#3986).
///
/// <para><b>Why the hub cannot do this itself, and why the moment matters.</b> A hub's own
/// teardown callbacks run in the ShutDown phase, by which time it can no longer speak for itself —
/// <c>JsonSynchronizationStream</c>'s end-of-stream announcement is suppressed there on purpose
/// ("a hub must speak only for itself, and never while it is dying"), because a dying owner that
/// reaches UP the hub tree to get a last word out resurrects the very Orleans activation it is
/// saying goodbye for. So the terminal event was being emitted after the thing that would deliver
/// it had been torn down, and a live subscriber of a RECYCLED hub was told nothing at all: no
/// frame, no completion, no error. It kept its last snapshot forever
/// (Systemorph/MeshWeaver#2533 / #2551 — a page stranded on the compile-progress overlay after
/// <c>NodeTypeEnrichmentHelpers.WithOverlaySelfHeal</c> recycled the instance hub underneath it).
/// </para>
///
/// <para><b>The contract.</b> <see cref="Announce"/> is invoked SYNCHRONOUSLY, at most ONCE per
/// hub, by whichever thread STARTS that hub's own teardown, and never when an ancestor's cascade
/// has already frozen the subtree (there the address is not coming back). 🚨 <b>That thread is
/// NOT necessarily the hub's action block</b>: a routed <see cref="DisposeRequest"/> reaches it on
/// the hub's own turn, but <c>MessageHubGrain.OnDeactivateAsync</c> and a <c>using</c> call
/// <c>Dispose()</c> from wherever they run. Until #3986 this was keyed on the routed request alone,
/// which left a DEACTIVATING owner telling its live subscribers nothing. So an implementation must
/// be THREAD-SAFE and must not assume a hub turn: read only state that is safe to read concurrently
/// (the one real implementation snapshots a <c>ConcurrentDictionary</c> and resolves a parent hub).
/// It must not block and must not throw — the hub logs and continues either way, because a
/// teardown that cannot announce must still tear down. Implementations are expected to
/// CAPTURE what they need in that turn and defer the actual delivery to a carrier that outlives
/// the teardown (see <c>Workspace.AnnounceRecycleToClientSubscriptions</c>, which posts through
/// the parent hub once <c>DisposalCompleted</c> has fired, so the re-ask it triggers lands on a
/// fresh activation instead of racing the dying one).</para>
/// </summary>
/// <param name="Announce">
/// Invoked by the thread that starts the hub's own teardown, before its disposal starts — the
/// hub's turn for a routed request, the caller's thread for a direct <c>Dispose()</c>.
/// Fire-and-forget and thread-safe: it may arrange later work, but must return promptly.
/// </param>
public sealed record RecycleAnnouncement(Action Announce);
