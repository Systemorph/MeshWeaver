namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>A message whose SENDER is load-bearing correlation state</b>, not an incidental routing
/// origin — so the receiver pairs this delivery with an earlier one BY that address, and re-posting
/// it from a different hub would break the pairing rather than merely relabel it.
///
/// <para><see cref="RouterTrafficRule.RoleOf(string?, string?, object?, bool)"/> does not report
/// such a delivery, for the same reason it does not report a heartbeat or a routing NACK: the
/// detector's value is that it fires on exactly the traffic somebody should change, and a report
/// nobody may act on trains people to mute the channel. Here "may not act on it" is structural —
/// hopping the message to an off-router hub is the one thing that would silence the report, and it
/// is precisely what the receiver's bookkeeping forbids.</para>
///
/// <para>🚨 <b>Implementing this is a claim about the RECEIVER, and a strong one.</b> It says: some
/// other delivery already told this receiver to remember the sender, and this one is only
/// meaningful against that memory. It is NOT a way to quiet a report that is merely inconvenient —
/// a message whose sender the receiver does not correlate has no business carrying it, and the
/// honest fix there is the issuing seam (<c>NodeOperationIssuingHub</c>, <c>ReadIssuingHub</c>)
/// that moves the origin off the router.</para>
///
/// <para><b>The case this exists for</b> (MeshWeaver#4489, split from #1140):
/// <c>UnsubscribeRequest</c>, posted by <c>JsonSynchronizationStream.CreateExternalClient</c>'s
/// release disposable. That post and the <c>SubscribeRequest</c> it releases are issued from the
/// SAME <c>workspace.Hub</c>, and the owner's per-subscriber stream is keyed on the subscriber that
/// opened it — so moving only the unsubscribe would leave the owner holding a subscription opened
/// by one hub and released by another, and moving both would change
/// <c>SubscribeRequest.Identity</c>, which is what the owner's access check reads. Where that
/// workspace belongs to the root mesh hub the release is honestly stamped <c>mesh/{id}</c>, and
/// that is correct, not a violation to be fixed.</para>
/// </summary>
public interface ICorrelatedBySender;
