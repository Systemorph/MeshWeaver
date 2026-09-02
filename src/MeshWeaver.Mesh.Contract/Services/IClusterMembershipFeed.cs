namespace MeshWeaver.Mesh.Services;

/// <summary>
/// EDGE-triggered cluster membership: one emission every time this cluster's membership changes.
/// The sibling of <see cref="IClusterMembership"/>, which answers the LEVEL question ("is that
/// member alive right now?"); this one answers "has the shape of the cluster just moved?".
///
/// <para><b>Why the mesh needs an edge, not just a level.</b> Anything the mesh publishes INTO a
/// cluster-partitioned structure — above all the pod-hub claim
/// (<c>OrleansRoutingService.AttachPodHub</c>), which writes an address→silo mapping into Orleans'
/// own grain directory — is only as durable as that structure's partitioning. Orleans' grain
/// directory is re-partitioned on every membership change; that is the component the pod-hub
/// transport's own design note names as *"the component that is unstable while cluster membership
/// changes"* (<c>Doc/Architecture/PodHubDeliveryRollPlan</c> → "What the swap traded"). A
/// registration made once and never re-made can therefore be silently lost, and nothing on the
/// owning side would ever notice: the router that cannot resolve the address answers the SENDER,
/// never the owner.</para>
///
/// <para><b>Prior art, and why this is not a watchdog.</b> Orleans' own <c>ClientDirectory</c>
/// publishes its client routing table to every silo on every membership change, for exactly this
/// reason. Re-asserting on a membership change is a DERIVED lifetime (#2426): it fires on the real
/// event that can invalidate the assertion, never on a timer, and never "just in case". A poll that
/// re-claimed every N seconds would be the band-aid shape; this is the event itself.</para>
///
/// <para><b>Registered by the cluster host (the Orleans silo) only.</b> Its absence means "no
/// cluster membership can change under me" — a monolith, an Orleans client, a bare mesh in a unit
/// test — and every consumer must then behave exactly as it did before this existed: assert once,
/// and never re-assert. Consumers therefore resolve it with <c>GetService</c>, never
/// <c>GetRequiredService</c>.</para>
/// </summary>
public interface IClusterMembershipFeed
{
    /// <summary>
    /// Emits once per observed membership change, carrying a monotonically increasing local
    /// sequence number (useful in logs; the VALUE is not a cluster-wide version and must not be
    /// compared across processes).
    ///
    /// <para>🚨 <b>Hot and shared.</b> Subscribing does not replay history — a subscriber sees only
    /// changes after it subscribed, which is correct: a consumer that also needs an initial
    /// assertion composes its own <c>StartWith</c>. Emissions are delivered off the cluster's own
    /// notification thread, so a subscriber's work never blocks membership processing.</para>
    /// </summary>
    IObservable<long> Changes { get; }
}
