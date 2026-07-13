using Xunit;

[assembly: AssemblyFixture(typeof(MeshWeaver.Hosting.Orleans.Test.OrleansDisposalDrainFixture))]

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Assembly-scoped drain for the backgrounded <see cref="TestCluster"/> disposals
/// (<see cref="OrleansClusterDisposal.DisposeInBackground"/>). xUnit v3 constructs this once and
/// disposes it AFTER every test class has run its per-class teardown (backgrounding its cluster's
/// stop→dispose onto the I/O pool) but BEFORE the native in-process runner does its foreground-thread
/// check. A SYNCHRONOUS <see cref="IDisposable.Dispose"/> (no <c>async</c>/<c>await</c>) blocks on the
/// pooled disposals draining, so no Orleans silo shutdown thread is still alive when the runner checks
/// — which otherwise force-exits the whole shard non-zero with
/// <c>"Foreground threads were left running, forcing process exit"</c> despite 123/123 green.
///
/// <para>Replaces the previous <c>ProcessExit</c> hook as the PRIMARY drain: <c>ProcessExit</c> fires
/// only DURING the runner's force-exit — too late to prevent it. The 30 s bound abandons a genuinely
/// wedged silo rather than hanging the run.</para>
/// </summary>
public sealed class OrleansDisposalDrainFixture : IDisposable
{
    public void Dispose() => OrleansClusterDisposal.WaitAll(TimeSpan.FromSeconds(30));
}
