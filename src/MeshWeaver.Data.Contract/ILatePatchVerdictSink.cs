namespace MeshWeaver.Data;

/// <summary>
/// 🚨 The seam a LATE owner verdict for a cross-hub MeshNode write is handed to, declared here so
/// the hub that MINTS the verdict can reach the watch that is waiting for it.
///
/// <para><b>Why the interface exists at all.</b> The implementation is
/// <c>MeshWeaver.Mesh.LatePatchResponseRegistry</c>, in <c>MeshWeaver.Mesh.Contract</c>. The owner
/// side that mints the <c>OwnerDisposing</c> NACK is
/// <c>DataExtensions.RegisterOwnerDisposingNack</c>, in <c>MeshWeaver.Data</c> — and
/// <c>MeshWeaver.Data</c> cannot reference <c>MeshWeaver.Mesh.Contract</c>, because that would
/// close the cycle <c>Data → Mesh.Contract → Layout → Data</c>. <see cref="PatchDataResponse"/>
/// already lives here, in the assembly both sides reference, so this is where the verb belongs
/// too.</para>
///
/// <para><b>What it is for.</b> A verdict that lands after <c>UpdateRemote</c>'s ~2 s bounded wait
/// has no pending <c>Observe</c> callback left, so posting it is only a way of reaching this same
/// registry the long way round — routed to the caller, into the cache hub's
/// <see cref="PatchDataResponse"/> handler, and finally to <c>Dispatch</c>. Within one mesh the
/// registry is a singleton both hubs already share, so the owner can hand the verdict over
/// directly: same waiter, no hub woken, no message routed. That matters most exactly when the post
/// is unavailable — a parent already past <c>DisposeHostedHubs</c> (#2778).</para>
///
/// <para>🚨 It also turns an assumption into a fact. The dropped-NACK guard justified itself with
/// "nobody is waiting", which no code could verify and which was false precisely when it mattered.
/// <see cref="Dispatch"/> REPORTS whether a caller was armed, so the question is answered rather
/// than assumed — and a miss costs one dictionary lookup, which is what makes it affordable to ask
/// on every teardown.</para>
/// </summary>
public interface ILatePatchVerdictSink
{
    /// <summary>
    /// Delivers a late owner response to its armed watch, if one is still armed and unexpired.
    /// </summary>
    /// <param name="requestId">The originating <c>PatchDataRequest</c> delivery id — the same value
    /// carried back as the response's <c>RequestId</c> correlation property.</param>
    /// <param name="response">The owner's verdict.</param>
    /// <returns><c>true</c> when an armed, unexpired watch consumed it; <c>false</c> when nobody in
    /// this mesh was waiting — which the caller may then treat as a checked fact rather than a
    /// guess.</returns>
    bool Dispatch(string requestId, PatchDataResponse response);
}
