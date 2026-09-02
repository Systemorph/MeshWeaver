using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// <see cref="IClusterMembershipFeed"/> over Orleans' own silo-status oracle — the EDGE sibling of
/// <see cref="OrleansClusterMembership"/>'s level answer.
///
/// <para><b>Why <see cref="ISiloStatusListener"/> and not the membership snapshot's async stream.</b>
/// <c>IClusterMembershipService.MembershipUpdates</c> is an <c>IAsyncEnumerable</c>, and consuming
/// one means an <c>await foreach</c> that never ends — an async edge with no natural bound, which on
/// this codebase would have to hold an <c>IIoPool</c> slot for the life of the process. The oracle's
/// listener interface is the same information delivered as a SYNCHRONOUS push, which is what a
/// reactive feed wants: no task, no loop, nothing to park.</para>
///
/// <para>🚨 <b>The hop off Orleans' notification thread is mandatory, not tidiness.</b> Orleans calls
/// every registered listener inline on its membership-processing path, so a subscriber that did real
/// work there would delay the cluster's own view of itself — and this feed's subscriber is the
/// pod-hub claim, which issues a grain call per registered address. <see cref="Changes"/> therefore
/// hands every subscriber an <c>ObserveOn(Scheduler.Default)</c> hop; the notification method itself
/// only pushes a number.</para>
///
/// <para>Notifications arrive serialised from the oracle's single processing loop, which is what
/// makes a plain <see cref="Subject{T}"/> correct here.</para>
/// </summary>
/// <param name="oracle">Orleans' silo-status oracle — the source of membership notifications.</param>
/// <param name="logger">Logger for the subscribe/notify diagnostics.</param>
internal sealed class OrleansClusterMembershipFeed(
    ISiloStatusOracle oracle,
    ILogger<OrleansClusterMembershipFeed> logger)
    : IClusterMembershipFeed, ISiloStatusListener, IDisposable
{
    private readonly Subject<long> changes = new();
    private long sequence;
    private int subscribed;
    private volatile bool disposed;

    /// <inheritdoc />
    public IObservable<long> Changes
    {
        get
        {
            // Subscribe to the oracle on FIRST use rather than in the constructor: the feed is a DI
            // singleton that a consumer may resolve while the silo's own lifecycle is still being
            // built, and registering a listener is the one thing that must happen exactly once.
            if (Interlocked.Exchange(ref subscribed, 1) == 0 && !disposed)
            {
                oracle.SubscribeToSiloStatusEvents(this);
                logger.LogDebug(
                    "[MEMBERSHIP] Subscribed to Orleans silo-status events — anything this mesh "
                    + "publishes into the cluster-partitioned grain directory can now be re-asserted "
                    + "when the partitioning moves.");
            }

            return changes.ObserveOn(Scheduler.Default);
        }
    }

    /// <inheritdoc />
    public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
    {
        if (disposed)
            return;

        var seq = Interlocked.Increment(ref sequence);
        logger.LogDebug(
            "[MEMBERSHIP] change #{Sequence}: {Silo} is now {Status} — re-asserting anything this "
            + "process has published into the grain directory",
            seq, updatedSilo, status);
        changes.OnNext(seq);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (Volatile.Read(ref subscribed) == 1)
            oracle.UnSubscribeFromSiloStatusEvents(this);
        changes.OnCompleted();
        changes.Dispose();
    }
}
