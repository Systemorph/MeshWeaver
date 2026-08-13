using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the liveness contract of the reference-keyed stream caches:
/// <b>a cached reduced stream whose SOURCE is dead is never served</b>, and asking for one cannot
/// spin.
///
/// <para><b>The defect (#1455).</b> <c>Workspace._localStreamCache</c> keyed on the
/// <c>WorkspaceReference</c> alone and judged liveness from the cached stream's OWN sub-hub. But
/// <c>WorkspaceStreams.CreateReducedStream</c> hosts that sub-hub on <c>stream.Host</c>, making the
/// child the parent's SIBLING — it outlives the parent for the whole teardown cascade. The
/// predicate was character-for-character the one <c>ReduceShared</c> had already been given a
/// parent guard for (#1425), and it had no such guard. Both halves were measured on this exact
/// fixture before the fix:</para>
/// <list type="number">
/// <item><description>Dispose the source, ask again → <b>the identical stream instance</b>
/// (<c>same=True</c>, <c>hubRunLevel=Started</c>, <c>hubDisposing=False</c>), which replays the
/// snapshot it last saw and then neither emits nor completes, ever.</description></item>
/// <item><description>Wait for the cascade to dispose that child, ask again → <c>GetStream</c>
/// <b>never returned</b>. A reduce off a disposed parent is disposed on birth
/// (<c>SynchronizationStream.RegisterForDisposal</c>), so the replacement failed the same check
/// and the retry loop went round forever, minting a <c>SynchronizationStream</c> and its
/// <c>sync/{id}</c> sub-hub every turn. Confirmed with a temporary iteration cap that threw at 20
/// turns.</description></item>
/// </list>
///
/// <para>Disposing the data source's primary stream is the same lever
/// <see cref="SilentReadNackTest"/> uses, and for the same reason: it is the one way to make a
/// source permanently dead while the owning hub stays healthy and keeps serving messages.</para>
/// </summary>
public class StreamCacheLivenessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// 🚨 The window between "the source died" and "the cascade disposed its children" — the regime
    /// the child-only predicate could not see at all.
    ///
    /// <para>The assertion is deliberately two-sided. The identity check names the defect (the
    /// cache handed back the very same object); the terminal check names the HARM, and is the half
    /// that would still fail if a future change made the cache mint a fresh stream that is somehow
    /// still attached to the corpse. A reader wants an answer or an end — a stale replay followed
    /// by permanent silence is the one outcome it cannot act on, because it is indistinguishable
    /// from a source that is merely quiet.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CachedStream_WhoseSourceIsDisposed_IsNeverServed()
    {
        var path = $"{TestPartition}/cache-liveness-window";
        await NodeFactory.CreateNode(
            new MeshNode("cache-liveness-window", TestPartition)
            {
                Name = "Cache Liveness Window",
                NodeType = "Markdown"
            }).Should().Emit();

        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("the read above must have activated the owning per-node hub");
        var workspace = owner!.GetWorkspace();

        // Populate the cache while the source is healthy, and prove the entry is real by taking
        // the same instance back on a second ask.
        var cached = workspace.GetStream(new MeshNodeReference());
        Assert.NotNull(cached);
        Assert.Same(cached, workspace.GetStream(new MeshNodeReference()));

        var dataSource = workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
        dataSource.Should().NotBeNull("the per-node hub owns a MeshNode data source");
        // Assert.NotNull, not FluentAssertions: ISynchronizationStream is an IObservable, so
        // `.Should()` binds the observable-assertion extension instead of the object one.
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);
        primary.Dispose();
        Output.WriteLine("[TEST] disposed the MeshNode data-source stream — the cache's source is now dead");

        var served = workspace.GetStream(new MeshNodeReference());
        Assert.NotNull(served);
        Assert.NotSame(cached, served);

        // The stream that IS handed out must be terminal: completed, with nothing to replay.
        // Before the fix this was `cached` itself, whose replay buffer still held the pre-disposal
        // snapshot and whose store never completed — the assertion below timed out.
        var terminal = await served!
            .Select(x => (object?)x.Value)
            .IsEmpty()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask(TestContext.Current.CancellationToken);

        terminal.Should().BeTrue(
            "a stream reduced from a dead source has no value to give and must say so by "
            + "completing — a reader that gets a stale replay and then eternal silence has no way "
            + "to tell 'gone' from 'quiet' and hangs for its whole budget");
    }

    /// <summary>
    /// 🚨 The regime AFTER the cascade: the cached child is genuinely disposed, so the cache
    /// correctly evicts — and every replacement it can build is disposed on birth. Re-reducing is
    /// not a repair, and repeating it is not a strategy.
    ///
    /// <para>Before the fix this call never returned. The observation is bounded and runs off the
    /// test thread so a regression fails in seconds rather than hanging the run — but note that a
    /// failure here means the process is <b>spinning and allocating hubs</b>, so treat the whole
    /// shard as compromised rather than just this test.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetStream_WhenEveryPossibleReduceIsBornDead_ReturnsInsteadOfRetrying()
    {
        var path = $"{TestPartition}/cache-liveness-cascade";
        await NodeFactory.CreateNode(
            new MeshNode("cache-liveness-cascade", TestPartition)
            {
                Name = "Cache Liveness Cascade",
                NodeType = "Markdown"
            }).Should().Emit();

        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull();
        var workspace = owner!.GetWorkspace();

        var cached = workspace.GetStream(new MeshNodeReference());
        Assert.NotNull(cached);

        var dataSource = workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);
        primary.Dispose();

        // Wait for the parent's teardown to reach the child it registered for disposal. Only then
        // does the cache start evicting-and-replacing, which is the loop under test.
        await Observable.Interval(TimeSpan.FromMilliseconds(25)).StartWith(0L)
            .Where(_ => (cached!.Hub as MessageHub)?.IsDisposing == true
                        || cached.Hub?.RunLevel > MessageHubRunLevel.Started)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(TestContext.Current.CancellationToken);
        Output.WriteLine($"[TEST] cascade reached the cached child: RunLevel={cached!.Hub?.RunLevel}");

        // Off the test thread and bounded: a regression is an unbounded spin, and the point of
        // this test is that the call TERMINATES at all.
        var resolved = await Observable
            .Start(() => workspace.GetStream(new MeshNodeReference()), NewThreadScheduler.Default)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Output.WriteLine("[TEST] GetStream returned instead of spinning");

        var terminal = await resolved!
            .Select(x => (object?)x.Value)
            .IsEmpty()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask(TestContext.Current.CancellationToken);

        terminal.Should().BeTrue(
            "the only stream a dead source can produce is a completed one — handing it back is "
            + "what the plain uncached reduce has always done, and is what ReduceShared's parent "
            + "guard falls through to");
    }

    /// <summary>
    /// 🚨 THE ANTI-DIVERGENCE GUARD. The two reference-keyed reduce caches —
    /// <c>Workspace._localStreamCache</c> and <c>SynchronizationStream.sharedReduceCache</c> — must
    /// return the SAME verdict about the same dead source.
    ///
    /// <para>They are separate caches with separate keys and separate call sites, and they have
    /// already drifted once: #1425 gave <c>ReduceShared</c> a parent guard and recorded exactly why
    /// the child-only predicate was inadequate, while the copy in <see cref="Workspace"/> kept it
    /// for another four months. Sharing one <c>StreamLiveness.IsUsable</c> is the structural half
    /// of the fix; this test is the half that fails if someone re-inlines a predicate into either
    /// one.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task BothReduceCaches_AgreeThatADeadSourceIsNotServable()
    {
        var path = $"{TestPartition}/cache-liveness-agree";
        await NodeFactory.CreateNode(
            new MeshNode("cache-liveness-agree", TestPartition)
            {
                Name = "Cache Liveness Agreement",
                NodeType = "Markdown"
            }).Should().Emit();

        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull();
        var workspace = owner!.GetWorkspace();

        var dataSource = workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);

        // Warm BOTH caches off the same primary: the shared intermediate that MeshDataSource's
        // own-node factory reduces through, and the workspace's own entry for the leaf.
        var sharedIntermediate = primary!.ReduceShared<InstanceCollection>(
            new CollectionReference(nameof(MeshNode)));
        Assert.NotNull(sharedIntermediate);
        Assert.Same(sharedIntermediate,
            primary.ReduceShared<InstanceCollection>(new CollectionReference(nameof(MeshNode))));

        var localCached = workspace.GetStream(new MeshNodeReference());
        Assert.NotNull(localCached);

        primary.Dispose();

        Assert.NotSame(sharedIntermediate,
            primary.ReduceShared<InstanceCollection>(new CollectionReference(nameof(MeshNode))));
        Assert.NotSame(localCached, workspace.GetStream(new MeshNodeReference()));
    }
}
