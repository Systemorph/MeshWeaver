namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// The cluster-wide DRAIN signal (#6865): one silo telling another "I have begun to drain".
///
/// <para><b>Why it exists.</b> Orleans membership cannot tell a draining pod from a healthy one: a pod
/// in its preStop drain stays <c>Active</c> for the whole termination grace period. An instance
/// singleton that leaves a draining pod (<c>RelocateOnDrain</c>) therefore picked its target from a
/// membership view in which the OTHER draining pod looked healthy, and when two pods drained at once
/// the singleton was handed back and forth between them every couple of seconds for the whole drain
/// (memex-cloud: ~28 minutes, every copy torn down before its first write landed).</para>
///
/// <para><b>Shape.</b> The grain's KEY is the RECIPIENT silo's parsable address and its activation is
/// placed ON that silo (a placement hint naming it, under <c>[PreferLocalPlacement]</c> — the same
/// trick that lets <see cref="IPodHubGrain"/> address a process). The draining silo calls it once for
/// every other Active silo when its drain begins and again on every membership change while it
/// drains, so a silo that joins mid-drain learns it too. The recipient records the drain in its own
/// process-local roster, which the singleton hand-off reads synchronously.</para>
/// </summary>
public interface ISiloDrainNoticeGrain : IGrainWithStringKey
{
    /// <summary>
    /// Records that <paramref name="drainingSilo"/> has begun to drain, on the silo this activation
    /// lives on.
    /// </summary>
    /// <param name="drainingSilo">The draining silo's parsable address.</param>
    /// <returns>
    /// <c>true</c> when the activation is on the silo its key names and the drain is now recorded
    /// there; <c>false</c> when it landed anywhere else (the recipient was not placeable), in which
    /// case nothing was recorded and the activation steps aside.
    /// </returns>
    Task<bool> Draining(string drainingSilo);
}
