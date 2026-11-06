using MeshWeaver.Messaging;
using Orleans.Services;

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
/// This service is the carrier that tells the asker anyway: the router does not know which silo
/// hosts the asker, so it asks every member silo, and the one whose local route table holds the
/// address hands the <see cref="DeliveryFailure"/> over.</para>
///
/// <para><b>Why an Orleans grain SERVICE and not a grain.</b> A grain service is a per-silo system
/// target: it exists from silo start to silo termination, is addressed by the silo itself (never
/// through the grain directory) and needs no placement. A grain keyed by the silo would need a new
/// activation, and Orleans places none on a silo that is <c>ShuttingDown</c> — while its activations
/// are already being deactivated, so a draining asker, the very case #7842 is about, could not be
/// reached. Orleans stops grain services first on a graceful stop, but <c>Stop()</c> only flips their
/// status; the system target keeps answering calls until the silo's message centre goes down, which
/// is after every grain on it has been deactivated.</para>
///
/// <para>Full reference: <c>Doc/Architecture/PodHubClaimReassertion</c>, section #7842.</para>
/// </summary>
public interface ISiloLocalDeliveryService : IGrainService
{
    /// <summary>
    /// Delivers <paramref name="delivery"/> to its target's host through THIS silo's local route
    /// table, if — and only if — this silo hosts the target.
    /// </summary>
    /// <param name="delivery">The delivery to hand over. Its target's host address is looked up.</param>
    /// <returns>
    /// <c>true</c> when the delivery was handed to a local route on this silo; <c>false</c> when this
    /// silo hosts no route for the target.
    /// </returns>
    Task<bool> DeliverToLocalRoute(IMessageDelivery delivery);
}
