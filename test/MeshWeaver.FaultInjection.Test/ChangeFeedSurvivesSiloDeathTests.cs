using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#4802 — a commit reaches every other silo even when the silo that made it dies mid-delivery.</b>
/// Cross-silo change delivery used to ride an Orleans memory stream: the writer's in-process
/// broadcast queue, then a <c>MemoryStreamQueueGrain</c> held in the RAM of ONE silo. Whatever either
/// held when its silo left was gone — no retry, no error at the reader, a replica serving its
/// pre-commit caches for the life of the process. The stream is retired; the carrier is now the
/// STORE's own feed (PostgreSQL <c>LISTEN/NOTIFY</c> → <c>IStorageAdapter.Changes</c> →
/// <c>StorageChangeFeedRelay</c> → <see cref="IMeshInvalidationFeed"/>), which no silo owns.
///
/// <para><b>The fault, deterministically.</b> Three silos over one store of record, joined by the
/// <see cref="Testing.FaultInjection.CrossProcessChangeRelay"/> (the LISTEN/NOTIFY model). Silo 2
/// listens on its invalidation feed. The relay is HELD, silo 0 commits a node, the hold's arrival
/// proves the notification is in flight — and silo 0 is KILLED (no graceful stop) before anything is
/// delivered. Then the relay lets go.</para>
///
/// <list type="bullet">
///   <item><b>Fixed:</b> the notification belongs to the store, not to the dead writer, so silo 2's
///     invalidation feed hears the commit after the writer is gone.</item>
///   <item><b>Negative control:</b> the identical sequence with a carrier that lives in the writer's
///     process — the memory stream's shape: the notification is QUEUED in the writer
///     (<see cref="Testing.FaultInjection.CrossProcessChangeRelay.HoldInWriter"/>), and the kill
///     itself is what discards it (<c>FaultInjectionCluster.Kill</c> departs the writer from the
///     relay) — and silo 2 never hears it. This is
///     what proves the fixed arm's arrival comes from the store's feed and from nothing else: no
///     Orleans stream, no second publish, no timer.</item>
/// </list>
/// </summary>
public abstract class ACommitInFlightWhenItsWriterDies(FaultInjectionCluster mesh)
{
    /// <summary>Whether the in-flight notification survives the writer (the store's feed) or dies with it.</summary>
    protected abstract bool CarrierOutlivesTheWriter { get; }

    [Fact(Timeout = 120_000)]
    public async Task TheCommit_ReachesTheOtherSilo_OnlyThroughTheStoresFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"silodeath-{Guid.NewGuid():N}/node";
        var writerHub = mesh.Hub(0);
        var writerStore = mesh.Storage(0);

        // Silo 2 — neither the writer nor (in the old design) necessarily the queue grain's host.
        var heard = new ReplaySubject<MeshChangeEvent>();
        using var listening = mesh.Silo(2).GetRequiredService<IMeshInvalidationFeed>()
            .Subscribe(change =>
            {
                if (string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase))
                    heard.OnNext(change);
            });

        // ── The fault: the notification is in flight when its writer dies ─────────────────────
        // Both arms QUEUE the notification; they differ only in WHERE it is queued — in the store
        // (survives the writer) or in the writer's own process (dies in Kill below).
        using var inFlight = CarrierOutlivesTheWriter
            ? mesh.Relay.Hold()
            : mesh.Relay.HoldInWriter();
        await writerStore.Write(
                new MeshNode("node", path[..path.IndexOf('/')])
                {
                    Name = "node", NodeType = "Markdown", State = MeshNodeState.Active, Version = 1,
                },
                writerHub.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit("silo 0 commits the node", ct);
        await inFlight.Arrivals.Where(a => a.Contains(path, StringComparison.OrdinalIgnoreCase))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the commit's notification reached the carrier — otherwise this case injected nothing", ct);

        await mesh.Kill(0);
        inFlight.Release();

        if (CarrierOutlivesTheWriter)
        {
            await heard.Where(c => c.Kind != MeshChangeKind.Deleted)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the notification belongs to the store, so silo 2 hears the commit although the "
                      + "silo that made it was killed before delivery (#4802)", ct);
        }
        else
        {
            await heard.Should().NotEmit(TimeSpan.FromSeconds(5),
                "with a carrier that dies with the writer, nothing else announces the commit to silo 2 — "
                + "if this arm ever hears it, something other than the store's feed is delivering and the "
                + "fixed arm proves nothing", ct);
        }
    }
}

/// <summary>The fix: the store's feed outlives the writer.</summary>
public class ACommitReachesTheOtherSiloAfterItsWriterDiesTest(ACommitReachesTheOtherSiloAfterItsWriterDiesTest.Cluster mesh)
    : ACommitInFlightWhenItsWriterDies(mesh), IClassFixture<ACommitReachesTheOtherSiloAfterItsWriterDiesTest.Cluster>
{
    protected override bool CarrierOutlivesTheWriter => true;

    /// <summary>Three silos: the writer, a bystander, and the listener.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;
    }
}

/// <summary>Negative control: a carrier that dies with the writer loses the commit.</summary>
public class ACommitIsLostWhenItsCarrierDiesWithTheWriterTest(ACommitIsLostWhenItsCarrierDiesWithTheWriterTest.Cluster mesh)
    : ACommitInFlightWhenItsWriterDies(mesh), IClassFixture<ACommitIsLostWhenItsCarrierDiesWithTheWriterTest.Cluster>
{
    protected override bool CarrierOutlivesTheWriter => false;

    /// <summary>Three silos: the writer, a bystander, and the listener.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;
    }
}
