using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The assembly-cache retention sweep, the sibling of the prebuilt-bundle walk that
/// Systemorph/MeshWeaver#7049 fixed: <c>pool.InvokeBlocking(_ =&gt; SweepCore(…))</c> discarded the
/// pool's token at the call site, so a pass listing (and, armed, deleting) the cache on a network share
/// kept <c>IoPool.Drain</c> waiting until it finished on its own.
///
/// <para><b>What these tests pin.</b> The pass is parked INSIDE one removal when the drain arrives.
/// That removal is allowed to finish (a synchronous unlink cannot be interrupted), and the pass must
/// then stop at the NEXT step boundary rather than delete the rest of its plan while the silo waits.
/// Every removal after the first is deliberately slower than the drain budget, so a pass that ignores
/// the cancel is measured as a residual, not inferred.</para>
///
/// <para>🚨 <b>What they do NOT change</b>: WHAT a pass collects. The uncancelled control runs the same
/// fixture through the same pooled entry point and removes exactly the plan.</para>
/// </summary>
public class AssemblyCacheSweepUnwindsOnTheDrainTest : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long every removal after the first takes: a slow share, longer than <see cref="DrainBudget"/>.</summary>
    private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(4);

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private const string LiveTag = "aaaaaaaa";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-assembly-cache-drain-{Guid.NewGuid():N}");
    private readonly AssemblyCacheRetention armed =
        AssemblyCacheRetention.ReportOnly with { Delete = true, KeepGenerations = 1 };

    public AssemblyCacheSweepUnwindsOnTheDrainTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteAssembly(string tag, DateTimeOffset writtenAt)
    {
        var directory = Path.Combine(root, "Acme_Pricing");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"v7-{tag}-{Guid.NewGuid().ToString("N")[..12]}.dll");
        File.WriteAllBytes(path, new byte[64]);
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
        return path;
    }

    /// <summary>The live generation plus three abandoned ones, each 60+ days old: three collectable files.</summary>
    private (string Live, string[] Collectable) CacheWithThreeCollectable() =>
        (WriteAssembly(LiveTag, Now - TimeSpan.FromHours(1)),
            [
                WriteAssembly("bbbbbbbb", Now - TimeSpan.FromDays(60)),
                WriteAssembly("cccccccc", Now - TimeSpan.FromDays(70)),
                WriteAssembly("dddddddd", Now - TimeSpan.FromDays(80)),
            ]);

    private IObservable<AssemblyCacheSweepResult> SweepThroughThePool(IIoPool pool, Action<string, CancellationToken> deleteFile) =>
        AssemblyCacheGenerations.Sweep(root, LiveTag, armed, pool, logger: null, deleteFile, Now);

    [Fact]
    public async Task ADrainArrivingMidPass_StopsThePassAtTheNextStep_AndTheJoinIsReal()
    {
        var (live, collectable) = CacheWithThreeCollectable();

        using var pool = new IoPool(2, drainTimeout: DrainBudget, drainGrace: TimeSpan.FromMilliseconds(200));
        var entered = new AsyncSubject<Unit>();
        var outcome = new AsyncSubject<string>();
        var removals = 0;

        SweepThroughThePool(pool, (path, ct) =>
            {
                if (Interlocked.Increment(ref removals) == 1)
                {
                    entered.OnNext(Unit.Default);
                    entered.OnCompleted();
                    // The removal in flight when the pod stops: it returns once the drain has
                    // cancelled the leaf.
                    SpinWait.SpinUntil(() => ct.IsCancellationRequested, Bound);
                }
                else
                {
                    // Every later removal is a slow share. Only a pass that walked on after the
                    // cancel ever gets here.
                    SpinWait.SpinUntil(() => false, SlowStep);
                }
                File.Delete(path);
            })
            .Subscribe(
                result =>
                {
                    outcome.OnNext($"completed, deleted {result.DeletedFiles}");
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
            "the assembly-cache sweep unwound once the drain cancelled it: the join is real, and nothing is "
            + "left walking the cache when the silo releases the mesh (the MeshWeaver#7049 shape)");
        Volatile.Read(ref removals).Should().Be(1,
            "the pass must stop at the step boundary after the drain's cancel, not delete the rest of its "
            + "plan while the silo's teardown join waits on it");
        collectable.Count(File.Exists).Should().Be(2, "the rest of the plan is the next pass's");
        File.Exists(live).Should().BeTrue();
        (await outcome.Should().Within(Bound).Emit("the leaf reports how it ended"))
            .Should().Be(nameof(OperationCanceledException),
                "a pass the pool ended is a cancellation, never a finished result");
    }

    [Fact]
    public async Task AnUncancelledPass_ThroughThePool_StillRemovesExactlyThePlan()
    {
        // The positive control: a pass that stopped early for any reason would satisfy the test above,
        // so the same fixture through the same pooled entry point must remove the whole plan, and
        // nothing else, when nothing cancels it.
        var (live, collectable) = CacheWithThreeCollectable();
        using var pool = new IoPool(2);

        var result = await SweepThroughThePool(pool, (path, _) => File.Delete(path))
            .Should().Within(Bound).Emit("an uncancelled pass completes");

        result.AbortReason.Should().BeNull();
        result.DeletedFiles.Should().Be(3);
        collectable.Should().OnlyContain(path => !File.Exists(path));
        File.Exists(live).Should().BeTrue();
    }

    [Fact]
    public void TheCacheWalk_StopsOnACancelledToken_AndThePassDoesNotCallItUnreadable()
    {
        // The report-only half of a pass (and the first half of an armed one) is the walk itself. A
        // cancel there propagates; it is never folded into "cache could not be read".
        var (_, collectable) = CacheWithThreeCollectable();
        AssemblyCacheGenerations.AssertClaim(root, "bbbbbbbb", "a-peer");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => AssemblyCacheGenerations.Scan(root, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => AssemblyCacheGenerations.ReadClaims(root, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => AssemblyCacheGenerations.SweepCore(
            root, LiveTag, armed, Now, cancelled.Token));
        collectable.Should().OnlyContain(path => File.Exists(path));
    }
}
