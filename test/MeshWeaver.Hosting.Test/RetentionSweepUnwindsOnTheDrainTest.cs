using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7049 — the leaf the silo's teardown join waited on:
/// <c>FileSystem=1 [MeshWeaver.Hosting.PrebuiltBundleStore+&lt;&gt;c__DisplayClass7_0.&lt;Sweep&gt;b__0]</c>,
/// which was <c>pool.InvokeBlocking(_ =&gt; SweepCore(…))</c>: the retention pass's walk of the bundle
/// share with the pool's token discarded at the call site — the #2480 shape, on a sibling walk.
///
/// <para><b>Why it holds the drain.</b> <c>IoPool.Drain</c> joins blocking leaves under the same
/// budget as the gate join, so a blocking leaf that never looks at its token keeps the join waiting
/// until the synchronous work finishes on its own. The retention pass is many synchronous steps
/// against a network share — a stat per file of every identity, then (armed, as it is on the
/// owning instance) a recursive removal per collectable identity — and none of them looked.</para>
///
/// <para><b>What these tests pin.</b> The pass is parked INSIDE one removal when the drain arrives,
/// which is where a real armed pass on a slow share is when a pod stops. That step is allowed to
/// finish — a synchronous removal cannot be interrupted, and pretending otherwise would test the
/// seam — and the pass must then stop at the NEXT step boundary, leaving the rest of the plan for
/// the next pass, rather than removing every remaining collectable while the silo waits. Every step
/// after the first is made slow on purpose, so a pass that ignores the cancel also overruns the
/// drain budget and the residual is measured, not inferred.</para>
///
/// <para>🚨 <b>What they do NOT change</b>: WHAT a pass collects. The uncancelled control below runs
/// the same fixture through the same pooled entry point and removes exactly the plan.</para>
/// </summary>
public class RetentionSweepUnwindsOnTheDrainTest : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long every removal after the first takes — a slow share. Longer than
    /// <see cref="DrainBudget"/>, so a pass that walks on after the cancel is a residual.
    /// </summary>
    private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(4);

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);
    private const string Live = "s-live";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-retention-drain-{Guid.NewGuid():N}");
    private readonly PrebuiltBundleRetention retention = PrebuiltBundleRetention.Default with { Delete = true };

    public RetentionSweepUnwindsOnTheDrainTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Sealed(string identity, string source, DateTimeOffset sealedAt)
    {
        var dir = Path.Combine(root, identity, source);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "A.zip"), new byte[1024]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "A.zip"), sealedAt.UtcDateTime);
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(sentinel, "A.zip\n");
        File.SetLastWriteTimeUtc(sentinel, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(dir, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, identity), sealedAt.UtcDateTime);
    }

    private void Marker(string version, string identity)
    {
        var dir = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, version);
        File.WriteAllText(path, identity + "\n");
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromDays(100)).UtcDateTime);
    }

    /// <summary>The running identity plus three superseded CI builds nothing references — all three collectable.</summary>
    private void StoreWithThreeCollectable()
    {
        Marker("3.1.0-ci.9000", Live);
        Sealed(Live, "plugins", Now - TimeSpan.FromDays(1));
        Marker("3.0.0-ci.1", "s-c1"); Sealed("s-c1", "plugins", Now - TimeSpan.FromDays(230));
        Marker("3.0.0-ci.2", "s-c2"); Sealed("s-c2", "plugins", Now - TimeSpan.FromDays(220));
        Marker("3.0.0-ci.3", "s-c3"); Sealed("s-c3", "plugins", Now - TimeSpan.FromDays(210));
    }

    private IObservable<PrebuiltBundleSweepResult> SweepThroughThePool(
        IIoPool pool, Action<string, CancellationToken> deleteDirectory) =>
        PrebuiltBundleStore.Sweep(root, Live, "4.0.0-ci.1", [], ImmutableList<PinnedPlatformReference>.Empty,
            retention, pool, logger: null, deleteDirectory, Now);

    [Fact]
    public async Task ADrainArrivingMidPass_StopsThePassAtTheNextStep_AndTheJoinIsReal()
    {
        StoreWithThreeCollectable();

        using var pool = new IoPool(2, drainTimeout: DrainBudget, drainGrace: TimeSpan.FromMilliseconds(200));
        var entered = new AsyncSubject<Unit>();
        var outcome = new AsyncSubject<string>();
        var removals = 0;

        SweepThroughThePool(pool, (dir, ct) =>
            {
                if (Interlocked.Increment(ref removals) == 1)
                {
                    entered.OnNext(Unit.Default);
                    entered.OnCompleted();
                    // The removal in flight when the pod stops: it returns once it returns — here,
                    // once the drain has cancelled the leaf.
                    SpinWait.SpinUntil(() => ct.IsCancellationRequested, Bound);
                }
                else
                {
                    // Every later removal is a slow share. Only a pass that walked on after the
                    // cancel ever gets here.
                    SpinWait.SpinUntil(() => false, SlowStep);
                }
                Directory.Delete(dir, recursive: true);
            })
            .Subscribe(
                result =>
                {
                    outcome.OnNext($"completed, removed {result.DeletedIdentities}");
                    outcome.OnCompleted();
                },
                ex =>
                {
                    outcome.OnNext(ex.GetType().Name);
                    outcome.OnCompleted();
                });

        await entered.Should().Within(Bound).Emit("the armed pass reaches its first removal");

        var residual = await Task.Run(pool.Drain, TestContext.Current.CancellationToken)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);

        residual.Should().Be(0,
            "the retention pass unwound once the drain cancelled it — the join is real, nothing is left "
            + "walking the bundle share when the silo releases the mesh (MeshWeaver#7049)");
        Volatile.Read(ref removals).Should().Be(1,
            "the pass must stop at the step boundary after the drain's cancel, not remove the rest of "
            + "its plan while the silo's teardown join waits on it");
        Directory.Exists(Path.Combine(root, "s-c2")).Should().BeTrue("the rest of the plan is the next pass's");
        Directory.Exists(Path.Combine(root, "s-c3")).Should().BeTrue("the rest of the plan is the next pass's");
        (await outcome.Should().Within(Bound).Emit("the leaf reports how it ended"))
            .Should().Be(nameof(OperationCanceledException),
                "a pass the pool ended is a cancellation, never a finished result recorded as the pass outcome");
        File.ReadAllLines(PrebuiltBundleStore.LedgerPathOf(root)).Should().Contain(l => l.Contains("removed s-c1"),
            "what the interrupted pass DID remove is still recorded in the ledger");
    }

    [Fact]
    public async Task AnUncancelledPass_ThroughThePool_StillRemovesExactlyThePlan()
    {
        // The positive control: a pass that stopped early for any reason would satisfy the test
        // above, so the same fixture through the same pooled entry point must remove the whole plan
        // — and nothing else — when nothing cancels it.
        StoreWithThreeCollectable();
        using var pool = new IoPool(2);

        var result = await SweepThroughThePool(pool, (dir, _) => Directory.Delete(dir, recursive: true))
            .Should().Within(Bound).Emit("an uncancelled pass completes");

        result.AbortReason.Should().BeNull();
        result.DeletedIdentities.Should().Be(3);
        Directory.Exists(Path.Combine(root, Live)).Should().BeTrue();
        new[] { "s-c1", "s-c2", "s-c3" }.Should().OnlyContain(id => !Directory.Exists(Path.Combine(root, id)));
    }

    [Fact]
    public void TheStoreWalk_StopsOnACancelledToken_AndThePassDoesNotCallItUnreadable()
    {
        // The report-only half of a pass (and the first half of an armed one) is the walk itself —
        // one stat per file of every identity on the share. A cancel there propagates; it is never
        // folded into "store could not be read" and written to the ledger as an aborted pass.
        StoreWithThreeCollectable();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PrebuiltBundleStore.Scan(root, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PrebuiltBundleStore.SweepCore(
            root, Live, "4.0.0-ci.1", [], [], retention with { Delete = false }, Now, cancelled.Token));
        File.Exists(PrebuiltBundleStore.LedgerPathOf(root)).Should().BeFalse(
            "a pass the pool cancelled before it read anything is not an aborted pass to record");
        new[] { "s-c1", "s-c2", "s-c3" }.Should().OnlyContain(id => Directory.Exists(Path.Combine(root, id)));
    }

    // ---- review on #7096: every round trip of the walk is a step boundary -----------------------

    /// <summary>
    /// The real file system, with ONE chosen operation turned into "the drain arrives here": the
    /// trigger cancels the token while that operation is in flight, and every IO call the walk makes
    /// after it is counted. A walk that honours its token makes none.
    /// </summary>
    private sealed class CancelAt(CancellationTokenSource cts, Func<string, string, bool> trigger) : BundleShareIo
    {
        private int fired;

        /// <summary>IO calls issued after the cancel.</summary>
        public int CallsAfterCancel;

        private void Step(string op, string path)
        {
            if (cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref CallsAfterCancel);
                return;
            }
            if (trigger(op, path) && Interlocked.Exchange(ref fired, 1) == 0)
                cts.Cancel();
        }

        private IEnumerable<string> Lazily(string op, string path, IEnumerable<string> entries)
        {
            Step(op, path);
            foreach (var entry in entries)
            {
                // The entry the in-flight read already produced is returned; asking for ANOTHER one
                // after the cancel is a further round trip.
                yield return entry;
                if (cts.IsCancellationRequested)
                    Interlocked.Increment(ref CallsAfterCancel);
            }
        }

        public override bool DirectoryExists(string path) { Step(nameof(DirectoryExists), path); return base.DirectoryExists(path); }
        public override bool FileExists(string path) { Step(nameof(FileExists), path); return base.FileExists(path); }
        public override string[] ReadAllLines(string path) { Step(nameof(ReadAllLines), path); return base.ReadAllLines(path); }
        public override string ReadAllText(string path) { Step(nameof(ReadAllText), path); return base.ReadAllText(path); }
        public override IEnumerable<string> ListDirectories(string path) =>
            Lazily(nameof(ListDirectories), path, base.ListDirectories(path));
        public override IEnumerable<string> ListFiles(string path, SearchOption option = SearchOption.TopDirectoryOnly) =>
            Lazily(nameof(ListFiles), path, base.ListFiles(path, option));
    }

    /// <summary>A sealed source whose seal lists THREE archives — three existence probes.</summary>
    private void SealedWithThreeArchives(string identity, string source, DateTimeOffset sealedAt)
    {
        Sealed(identity, source, sealedAt);
        var dir = Path.Combine(root, identity, source);
        foreach (var name in new[] { "B.zip", "C.zip" })
        {
            File.WriteAllBytes(Path.Combine(dir, name), new byte[16]);
            File.SetLastWriteTimeUtc(Path.Combine(dir, name), sealedAt.UtcDateTime);
        }
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(sentinel, "A.zip\nB.zip\nC.zip\n");
        File.SetLastWriteTimeUtc(sentinel, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(dir, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, identity), sealedAt.UtcDateTime);
    }

    private string SourceOf(string identity) => Path.Combine(root, identity, "plugins");

    [Theory]
    // The identity listing: the drain lands while the root is being listed.
    [InlineData("root listing")]
    // The generation listing of one source.
    [InlineData("generation listing")]
    // The publication pointer of one source.
    [InlineData("pointer probe")]
    // The first of three seal probes.
    [InlineData("seal probe")]
    public void ADrainInsideTheStoreWalk_StopsItBeforeTheNextRoundTrip(string where)
    {
        StoreWithThreeCollectable();
        SealedWithThreeArchives("s-c2", "plugins", Now - TimeSpan.FromDays(220));
        using var cts = new CancellationTokenSource();
        var c1 = SourceOf("s-c1");
        var io = new CancelAt(cts, where switch
        {
            "root listing" => (op, path) => op == nameof(BundleShareIo.ListDirectories) && path == root,
            "generation listing" => (op, path) => op == nameof(BundleShareIo.ListDirectories) && path == c1,
            "pointer probe" => (op, path) => op == nameof(BundleShareIo.FileExists)
                && path == Path.Combine(c1, ShippedPrebuiltBundles.PublicationPointerFileName),
            "seal probe" => (op, path) => op == nameof(BundleShareIo.FileExists)
                && path == Path.Combine(SourceOf("s-c2"), "A.zip"),
            _ => throw new ArgumentOutOfRangeException(nameof(where)),
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PrebuiltBundleStore.Scan(root, cts.Token, logger: null, io));

        io.CallsAfterCancel.Should().Be(0,
            $"a drain arriving during the {where} must end the walk before its next round trip to the share — "
            + "a listing sorted before it is checked, a pointer resolved without the token, or a seal probed to the "
            + "end each keep the silo's teardown join waiting (review on #7096)");
    }

    [Fact]
    public void ADrainWhileAnIdentityIsBeingUnsealed_LeavesItInPlace_AndIsNotCountedAsAFailedRemoval()
    {
        StoreWithThreeCollectable();
        using var cts = new CancellationTokenSource();
        var pointer = Path.Combine(SourceOf("s-c1"), ShippedPrebuiltBundles.PublicationPointerFileName);
        var pointerProbes = 0;
        // The scan probes s-c1's pointer once; the removal resolves it again before unsealing. The
        // drain lands on that second probe — inside the removal of the first collectable identity.
        var io = new CancelAt(cts, (op, path) =>
            op == nameof(BundleShareIo.FileExists) && path == pointer && Interlocked.Increment(ref pointerProbes) == 2);
        var removed = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => PrebuiltBundleStore.SweepCore(
            root, Live, "4.0.0-ci.1", [], [], retention, Now, cts.Token, logger: null,
            deleteDirectory: dir => { removed++; Directory.Delete(dir, recursive: true); }, io));

        removed.Should().Be(0, "the recursive removal is its own step, and the drain arrived before it");
        Directory.Exists(Path.Combine(root, "s-c1")).Should().BeTrue("a removal the drain interrupted leaves the identity for the next pass");
        File.Exists(Path.Combine(SourceOf("s-c1"), ShippedPrebuiltBundles.CompletionSentinelFileName)).Should().BeTrue(
            "the seal is removed only after the pointer resolved under the token");
        var ledger = File.ReadAllLines(PrebuiltBundleStore.LedgerPathOf(root));
        ledger.Should().ContainSingle(l => l.Contains("interrupted by the pool's cancel"),
            "the pool's cancel takes the interrupted-ledger path");
        ledger.Should().NotContain(l => l.Contains("failed"), "a cancelled removal is not a failed removal");
    }

    [Fact]
    public async Task TheHostedServiceWrapper_ReportsADrainedPassAsCancelled_NeverAsAFault()
    {
        // The schedule wraps every pass in GuardedPass. A pass the pool ended — here an armed pass
        // parked in its first removal and then drained — must reach the CANCELLED report and never the
        // fault report, whose "FAULTED … nothing was collected" would contradict the INTERRUPTED
        // outcome of a pass that had already removed an identity.
        StoreWithThreeCollectable();
        using var pool = new IoPool(2, drainTimeout: DrainBudget, drainGrace: TimeSpan.FromMilliseconds(200));
        var entered = new AsyncSubject<Unit>();
        var ended = new AsyncSubject<Unit>();
        var faults = 0;
        var cancellations = 0;
        var records = 0;

        PrebuiltBundleRetentionHostedService.GuardedPass(
                SweepThroughThePool(pool, (dir, ct) =>
                {
                    entered.OnNext(Unit.Default);
                    entered.OnCompleted();
                    SpinWait.SpinUntil(() => ct.IsCancellationRequested, Bound);
                    Directory.Delete(dir, recursive: true);
                }),
                _ => Interlocked.Increment(ref records),
                _ => Interlocked.Increment(ref faults),
                _ => Interlocked.Increment(ref cancellations))
            .Subscribe(_ => { }, _ => { }, () => { ended.OnNext(Unit.Default); ended.OnCompleted(); });

        await entered.Should().Within(Bound).Emit("the armed pass reaches its first removal");
        var residual = await Task.Run(pool.Drain, TestContext.Current.CancellationToken)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);
        await ended.Should().Within(Bound).Emit("the guarded pass completes as one pass");

        residual.Should().Be(0);
        Volatile.Read(ref cancellations).Should().Be(1, "the drained pass is reported once, as cancelled");
        Volatile.Read(ref faults).Should().Be(0, "a pass the pool cancelled is not a faulted pass");
        Volatile.Read(ref records).Should().Be(0, "an interrupted pass records no finished result");
    }
}
