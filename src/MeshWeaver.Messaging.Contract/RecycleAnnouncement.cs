namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>The seam that lets a RECYCLE say goodbye.</b> A higher layer hangs this on a hub
/// (<c>hub.Set(new RecycleAnnouncement(...))</c>) to be handed the ONE turn a recycled hub still
/// has while it is whole: the turn in which its routed <see cref="DisposeRequest"/> is handled,
/// BEFORE <c>Dispose()</c> begins.
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
/// <para><b>The contract.</b> <see cref="Announce"/> is invoked SYNCHRONOUSLY on the hub's own
/// action block, once, only for a message-routed recycle (never for an ancestor's teardown
/// cascade, where the address is not coming back), and only while the hub is still
/// <c>Started</c>. It must not block and must not throw — the hub logs and continues either way,
/// because a recycle that cannot announce must still recycle. Implementations are expected to
/// CAPTURE what they need in that turn and defer the actual delivery to a carrier that outlives
/// the teardown (see <c>Workspace.AnnounceRecycleToClientSubscriptions</c>, which posts through
/// the parent hub once <c>DisposalCompleted</c> has fired, so the re-ask it triggers lands on a
/// fresh activation instead of racing the dying one).</para>
/// </summary>
/// <param name="Announce">
/// Invoked on the recycled hub's own turn, before its disposal starts. Fire-and-forget: it may
/// arrange later work, but must return promptly.
/// </param>
public sealed record RecycleAnnouncement(Action Announce);
