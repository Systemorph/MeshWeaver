namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>A message that is the ONLY thing which releases state the RECEIVER holds</b> — so dropping
/// it at the sender does not lose a notification, it leaks the receiver's memory for the rest of
/// that receiver's life.
///
/// <para><b>Why the teardown post guard needs to know.</b> A hub at run level
/// <c>DisposeHostedHubs</c> or beyond refuses every post but its own <c>ShutdownRequest</c> /
/// <c>DisposeRequest</c> and a correlated reply, and that refusal is right for the traffic it was
/// written for: nobody awaits a fire-and-forget event, so nothing is left waiting. A release is the
/// one fire-and-forget shape for which "nobody is waiting" is the reason it must go out rather than
/// a reason it may be dropped — there is no requester to NACK, no retry to trigger and no later
/// probe that discovers the loss. The receiver simply keeps what it was holding, silently, with
/// nothing to grep.</para>
///
/// <para><b>The case this exists for</b> (Systemorph/MeshWeaver#3432). <c>UnsubscribeRequest</c> is
/// the only thing that ends an owner-side per-subscriber stream and its <c>sync/{id}</c> sub-hub
/// ("only an UnsubscribeRequest disposes a server-side stream"). It is posted from the SUBSCRIBING
/// hub by the release disposable that <c>JsonSynchronizationStream.CreateExternalClient</c>
/// registers on the client-side <c>sync/{id}</c> hub, so it runs in that hub's <c>ShutDown</c>
/// phase. On the stream-dispose route the subscribing hub is live and the release leaves. On the
/// HUB-teardown route — a Blazor circuit ending, a <c>DisposeRequest</c>, a recycle — the
/// subscribing hub is by construction already in <c>DisposeHostedHubs</c> (that phase is what
/// disposes the child), so the farewell was refused at the only door it has and the owner was never
/// told: one <c>RunLevel=Started</c> hub per subscription, each holding its own Autofac lifetime
/// scope and TypeRegistry, for the life of the process.</para>
///
/// <para>🚨 <b>Implementing this is a claim about what happens when the message is LOST, and it is a
/// narrow one.</b> It says: no other mechanism in the system ever reclaims what this message
/// releases. It is NOT a way to make an ordinary event survive a teardown — an event whose loss the
/// receiver recovers from (a fresh snapshot, a re-subscribe, a change feed, a heartbeat lapse) must
/// keep the historical refusal, because forwarding fire-and-forget traffic out of a disposing hub is
/// the storm shape that guard exists to prevent.</para>
///
/// <para>The carrier is the hub's PARENT, the same one <c>NackThroughParent</c> and the
/// refused-reply path already use ("our own Post would re-enter this same gate and be dropped").
/// One hop, not a walk: on this route the parent is the hub that is disposing us and it cannot
/// reach its own <c>ShutDown</c> until we have completed, so it is still routing. In a whole-TREE
/// teardown the parent is going too — and then so is the receiver, which is about to drop
/// everything anyway.</para>
/// </summary>
public interface IReleasesRemoteState;
