using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>Plugins#3000: every process-local cache that only the change feed retracts re-reads on a declared gap.</b>
/// <see cref="ALiveQueryAcrossALostChangeFeedWindow"/> pins one consumer of
/// <see cref="ChangeFeedGap"/>, the live query. These cases pin the other four that core #6241 wired:
///
/// <list type="number">
///   <item><b>Path resolution</b> (<c>PathResolutionService.OnChangeFeedGap</c>): a cached resolution of a node
///     that another process deleted during the gap.</item>
///   <item><b>The stream cache's storm breaker</b> (<c>MeshNodeStreamCache.OnChangeFeedGap</c>): a negative
///     window on a path that another process created during the gap.</item>
///   <item><b>The per-node hub's own-node mirror</b> (<c>MeshDataSource</c>'s gap re-read): a commit to THIS
///     node that another process made during the gap.</item>
///   <item><b>The NodeType rebind watcher</b> (<c>NodeTypeRebindWatcher.ReadAfterEachGap</c>): a retype of THIS
///     node that another process made during the gap.</item>
/// </list>
///
/// <para><b>The fault.</b> Two silos over one store of record, joined by the
/// <see cref="Testing.FaultInjection.CrossProcessChangeRelay"/> (the LISTEN/NOTIFY model). The relay is
/// switched to <c>Drop</c>, the OTHER silo commits straight to the store, the drop's arrival proves that
/// commit's notification was LOST, and the relay comes back. The fixed arm declares the gap on release;
/// the negative-control arm is the pre-fix listener, which loses the notification and tells nobody.</para>
///
/// <para>🚨 For the two per-node cases the commit is made through the store of the silo that does NOT host
/// the node's hub. A commit through the hosting silo's own store would reach the hub as a LOCAL
/// notification, which the relay never carries, and the control arm would converge without any gap.</para>
/// </summary>
public abstract class AChangeFeedGapReachesEveryConsumer(FaultInjectionCluster mesh)
{
    /// <summary>Whether the relay declares the gap when it comes back (the fix) or stays silent (pre-fix).</summary>
    protected abstract bool AnnounceGap { get; }

    /// <summary>How long the negative-control arm watches for a convergence that must not come.</summary>
    private static readonly TimeSpan StaleWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private AccessService Access(int silo) => mesh.Silo(silo).GetRequiredService<AccessService>();
    private IMeshNodeStreamCache Cache(int silo) => mesh.Silo(silo).GetRequiredService<IMeshNodeStreamCache>();

    /// <summary>(1) Path resolution drops a cached resolution of a node deleted during the gap.</summary>
    [Fact(Timeout = 120_000)]
    public async Task PathResolution_DropsACachedResolution_OfANodeDeletedDuringTheGap()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"gap-resolve-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";
        var resolver = mesh.Silo(1).GetRequiredService<IPathResolver>();

        await mesh.Storage(0).Write(Node(ns, "Item"), mesh.Hub(0).JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit("the node commits while the relay is healthy", ct);
        await Resolutions(resolver, path).Where(r => r?.Prefix == path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("silo 1 resolves the node, and its resolution cache now holds that answer", ct);

        await LoseOneCommit(path, () => mesh.Storage(0).Delete(path).Select(_ => Unit.Default),
            "silo 0 deletes the node while the feed is down", ct);

        var moved = Resolutions(resolver, path).Where(r => r is null || r.Prefix != path);
        if (AnnounceGap)
            await moved.Should().Within(TestTimeouts.Convergence)
                .Emit("the gap cleared silo 1's resolution cache, so the next resolution re-read the store "
                      + "and no longer routes to the deleted node (Plugins#3000)", ct);
        else
            await moved.Should().NotEmit(StaleWindow,
                "with the Deleted notification lost and no gap declared, the cached resolution is the only "
                + "answer silo 1 has. If this arm ever moves, something other than the gap retracts the cache "
                + "and the fixed arm proves nothing", ct);
    }

    /// <summary>(2) The stream cache closes a storm-breaker window on a path created during the gap.</summary>
    [Fact(Timeout = 120_000)]
    public async Task StreamCache_ClosesTheStormWindow_OfAPathCreatedDuringTheGap()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"gap-storm-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";
        var cache = (MeshNodeStreamCache)Cache(1);

        // The owner's REAL missing-node failure, taken on a sibling path that stays absent, so the seeded
        // window carries the exception class production records.
        var miss = await Access(1).RunAsSystem(() =>
                cache.GetStream($"{ns}/NeverCreated", mesh.Hub(1).JsonSerializerOptions).Materialize())
            .Where(n => n.Kind == NotificationKind.OnError)
            .Should().Within(TestTimeouts.Convergence).Emit("reading an absent path answers NotFound", ct);
        MeshNodeStreamCache.IsMissingNodeFailure(miss.Exception!).Should().BeTrue(
            "precondition: the seeded failure must be the class the storm breaker records");

        // A grown failure history (8 consecutive misses = a 256 s window), so the window cannot simply
        // EXPIRE inside this case. The control arm then measures the gap's absence, not the clock.
        for (var i = 0; i < 8; i++)
            cache.RecordNegative(path, miss.Exception!);
        cache.IsStormWindowOpen(path).Should().BeTrue("precondition: the storm-breaker window on the path is open");

        await LoseOneCommit(path,
            () => mesh.Storage(0).Write(Node(ns, "Item"), mesh.Hub(0).JsonSerializerOptions).Select(_ => Unit.Default),
            "silo 0 creates the node while the feed is down", ct);

        var closed = Observable.Interval(Poll).StartWith(0L).Select(_ => cache.IsStormWindowOpen(path)).Where(open => !open);
        if (AnnounceGap)
        {
            await closed.Should().Within(TestTimeouts.Convergence)
                .Emit("the gap reset silo 1's failure state for every path that held some (Plugins#3000)", ct);
            var node = await Access(1).RunAsSystem(() => cache.GetStream(path, mesh.Hub(1).JsonSerializerOptions))
                .Where(n => n is not null)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("with the window closed, a read re-probes the owner and answers the node created during the gap", ct);
            node!.Path.Should().Be(path);
        }
        else
        {
            await closed.Should().NotEmit(StaleWindow,
                "with the Created notification lost and no gap declared, nothing resets the window, which "
                + "fast-fails reads of a node that now exists for the rest of its back-off", ct);
            var read = await Access(1).RunAsSystem(() =>
                    cache.GetStream(path, mesh.Hub(1).JsonSerializerOptions).Materialize())
                .Where(n => n.Kind != NotificationKind.OnNext || n.Value is not null)
                .Should().Within(TestTimeouts.Convergence).Emit("the read of the path settles", ct);
            read.Kind.Should().Be(NotificationKind.OnError,
                "the stale window still fast-fails the read of a node the store holds, which is the pre-fix staleness");
        }
    }

    /// <summary>(3) The per-node hub re-reads its own node after a commit to it during the gap.</summary>
    [Fact(Timeout = 120_000)]
    public async Task OwnNodeMirror_ReReadsTheStore_AfterACommitToItDuringTheGap()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"gap-own-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";
        var (host, other, _) = await HostTheNode(ns, path, ct);

        var stored = mesh.Stored(path)!;
        await LoseOneCommit(path,
            () => mesh.Storage(other).Write(stored with { Name = "during", Version = stored.Version + 1 },
                mesh.Hub(other).JsonSerializerOptions).Select(_ => Unit.Default),
            $"silo {other}, which does not host the node, commits to it while the feed is down", ct);

        var adopted = Access(host).RunAsSystem(() => Cache(host).GetStream(path, mesh.Hub(host).JsonSerializerOptions))
            .Where(n => n?.Name == "during");
        if (AnnounceGap)
            await adopted.Should().Within(TestTimeouts.Convergence)
                .Emit("the gap made the owning hub re-read its node from the store and adopt the commit "
                      + "it never heard about (Plugins#3000)", ct);
        else
            await adopted.Should().NotEmit(StaleWindow,
                "with the notification lost and no gap declared, the owning hub serves its pre-gap mirror, "
                + "and its next write would build on it", ct);
    }

    /// <summary>(4) The NodeType rebind watcher recycles the hub after a retype during the gap.</summary>
    [Fact(Timeout = 120_000)]
    public async Task RebindWatcher_RecyclesTheHub_AfterARetypeDuringTheGap()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"gap-rebind-{Guid.NewGuid():N}";
        var path = $"{ns}/Item";
        var (_, other, hosted) = await HostTheNode(ns, path, ct);

        var disposed = new AsyncSubject<Unit>();
        hosted.RegisterForDisposal(_ =>
        {
            disposed.OnNext(Unit.Default);
            disposed.OnCompleted();
        });

        var stored = mesh.Stored(path)!;
        await LoseOneCommit(path,
            () => mesh.Storage(other).Write(stored with { NodeType = "Code", Version = stored.Version + 1 },
                mesh.Hub(other).JsonSerializerOptions).Select(_ => Unit.Default),
            $"silo {other}, which does not host the node, retypes it while the feed is down", ct);

        if (AnnounceGap)
            await disposed.Should().Within(TestTimeouts.Convergence)
                .Emit("the gap made the rebind watcher read the node's type from the store, find it changed, "
                      + "and recycle the hub that bound the old type (Plugins#3000)", ct);
        else
            await disposed.Should().NotEmit(StaleWindow,
                "with the retype's notification lost and no gap declared, the hub keeps the configuration "
                + "of a type the node no longer has", ct);
    }

    /// <summary>
    /// Arms the relay's drop, commits through <paramref name="commit"/>, proves the commit's
    /// notification was LOST, and releases the drop, which declares the gap only in the fixed arm.
    /// </summary>
    private async Task LoseOneCommit(string path, Func<IObservable<Unit>> commit, string because, CancellationToken ct)
    {
        var drop = mesh.Relay.Drop(announceGap: AnnounceGap);
        try
        {
            await commit().Should().Within(TestTimeouts.Convergence).Emit(because, ct);
            await drop.Arrivals.Where(a => a.Contains(path, StringComparison.OrdinalIgnoreCase))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the commit's notification reached the relay and was LOST, otherwise this case injected nothing", ct);
        }
        finally
        {
            drop.Release();
        }
    }

    /// <summary>
    /// Creates the node, activates its hub, waits until the hub's activation writes have settled in the
    /// store (so no late persist of the pre-gap mirror can overwrite the commit made during the gap), and
    /// names the silo that hosts it, the one that does not, and the hosted hub instance.
    /// </summary>
    private async Task<(int Host, int Other, IMessageHub Hosted)> HostTheNode(string ns, string path, CancellationToken ct)
    {
        await mesh.Storage(0).Write(Node(ns, "Item"), mesh.Hub(0).JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit("the node commits while the relay is healthy", ct);
        await Access(1).RunAsSystem(() =>
                Cache(1).Update(path, n => n with { Name = "before" }, mesh.Hub(1).JsonSerializerOptions))
            .Should().Within(TestTimeouts.Convergence).Emit("a routed write activates the node's hub", ct);
        await Observable.Interval(Poll).StartWith(0L).Where(_ => mesh.Stored(path)?.Name == "before")
            .Should().Within(TestTimeouts.Convergence).Emit("the hub's write has reached the store of record", ct);

        var (host, hosted) = await Observable.Interval(Poll).StartWith(0L)
            .Select(_ => Enumerable.Range(0, 2)
                .Select(s => (Silo: s, Hub: mesh.Hub(s).GetHostedHub(new Address(path), HostedHubCreation.Never)))
                .FirstOrDefault(h => h.Hub is not null))
            .Where(h => h.Hub is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("one silo hosts the node's hub", ct);
        return (host, 1 - host, hosted!);
    }

    /// <summary>Re-asks silo 1's resolver every <see cref="Poll"/>, one resolution at a time.</summary>
    private static IObservable<AddressResolution?> Resolutions(IPathResolver resolver, string path)
        => Observable.Interval(Poll).StartWith(0L)
            .Select(_ => resolver.ResolvePath(path).Take(1))
            .Concat();

    private static MeshNode Node(string ns, string id)
        => new(id, ns) { Name = id, NodeType = "Markdown", State = MeshNodeState.Active, Version = 1 };
}

/// <summary>The fix: the reconnect declares the gap, and every consumer re-reads.</summary>
public class AChangeFeedGapReachesEveryConsumerTest(AChangeFeedGapReachesEveryConsumerTest.Cluster mesh)
    : AChangeFeedGapReachesEveryConsumer(mesh), IClassFixture<AChangeFeedGapReachesEveryConsumerTest.Cluster>
{
    /// <inheritdoc />
    protected override bool AnnounceGap => true;

    /// <summary>Two silos.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>Negative control: the pre-fix listener. Notifications are lost, nobody is told, and every consumer stays stale.</summary>
public class ASilentChangeFeedGapLeavesEveryConsumerStaleTest(ASilentChangeFeedGapLeavesEveryConsumerStaleTest.Cluster mesh)
    : AChangeFeedGapReachesEveryConsumer(mesh), IClassFixture<ASilentChangeFeedGapLeavesEveryConsumerStaleTest.Cluster>
{
    /// <inheritdoc />
    protected override bool AnnounceGap => false;

    /// <summary>Two silos.</summary>
    public class Cluster : FaultInjectionCluster;
}
