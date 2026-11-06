using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Hands a delivery to a hub hosted on ONE NAMED SILO through that silo's local route table,
/// without asking the grain directory where the hub lives (#7842).
///
/// <para><b>Why it exists.</b> A pod-process hub is reachable from another silo only through its
/// <see cref="IPodHubGrain"/> registration in the grain directory. When that registration is missing
/// — a claim not landed yet, or an entry the directory lost while a silo left — the router finds
/// no owner, and a REPLY to that hub is refused. The router used to tell only the REPLIER, which has
/// nothing to retry, while the hub that asked waited out its whole request budget in silence.
/// This grain is the carrier that tells the asker anyway: the router does not know which silo hosts
/// the asker, so it asks every member silo, and the one whose local route table holds the address
/// hands the <see cref="DeliveryFailure"/> over.</para>
///
/// <para><b>Shape.</b> The grain's KEY is the silo's parsable address and its activation is placed
/// ON that silo by a placement hint naming it, under <c>[PreferLocalPlacement]</c> — the same trick
/// <see cref="ISiloDrainNoticeGrain"/> uses. The directory entry of THIS grain may be lost the same
/// way; then placement simply re-runs on the hinted silo, which is exactly the silo the key names.
/// An activation that lands anywhere else (a stale entry, or a silo of an older generation that has
/// no such grain) answers <c>false</c> and steps aside, so the next call is placed afresh.</para>
///
/// <para>Full reference: <c>Doc/Architecture/PodHubClaimReassertion</c>, section #7842.</para>
/// </summary>
public interface ISiloLocalDeliveryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Delivers <paramref name="delivery"/> to its target's host through THIS silo's local route
    /// table, if — and only if — this activation is on the silo its key names and that silo hosts
    /// the target.
    /// </summary>
    /// <param name="delivery">The delivery to hand over. Its target's host address is looked up.</param>
    /// <returns>
    /// <c>true</c> when the delivery was handed to the local route of the silo the key names;
    /// <c>false</c> when this activation is on another silo, or that silo hosts no route for the
    /// target.
    /// </returns>
    Task<bool> DeliverToLocalRoute(IMessageDelivery delivery);
}
