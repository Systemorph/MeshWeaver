using System;
using System.IO;
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
/// Systemorph/MeshWeaver#2480 — the leaf the silo's teardown join waited on, once the report could
/// name it: <c>prebuilt:files=1 [ShippedPrebuiltBundles+&lt;&gt;c__DisplayClass24_0.&lt;SeedBundles&gt;b__8]</c>,
/// which was <c>pool.InvokeBlocking(_ =&gt; enumerateBundles())</c>.
///
/// <para><b>Why a blocking leaf holds the drain at all.</b> <c>IoPool.Drain</c> joins blocking leaves
/// on their own idle signal under the same budget as the gate join, so a blocking leaf that never
/// looks at its token keeps the drain waiting until the synchronous work finishes on its own. The
/// bundle walk is many synchronous steps against a network share — a pointer, a seal and an
/// existence probe per source, per identity — and none of them looked.</para>
///
/// <para><b>What these tests pin.</b> The walk is parked INSIDE one step (a seal read) when the drain
/// arrives, which is exactly where a real walk on a slow share is when a pod stops. The step is
/// allowed to finish — a synchronous read cannot be interrupted, and pretending otherwise would be
/// a test of the seam — and the walk must then stop at the NEXT step boundary rather than walk the
/// rest of the share. Before the fix it read every remaining seal and handed back a full list to a
/// process that was tearing down.</para>
///
/// <para>🚨 <b>What they do NOT pin</b>, so it is not over-read: that each PRODUCTION call site hands
/// the pool's token to the walk. That half is carried by the compiler instead — the enumerator
/// delegate is now <c>Func&lt;CancellationToken, List&lt;string&gt;&gt;</c> handed to the pool as-is,
/// and <see cref="ShippedPrebuiltBundles.CompletePublishedBundlesOf"/> takes the token as a REQUIRED
/// parameter, so no caller can drop it by omission.</para>
/// </summary>
public class BundleShareWalkSettlesOnTheDrainTest : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-seed-drain-" + Guid.NewGuid().ToString("N"));

    private string IdentityDirectory => Path.Combine(root, "s72c27afab89c100727e79e0559c59e32");

    private void SealedSource(string name)
    {
        var dir = Path.Combine(IdentityDirectory, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".zip"), "zip");
        File.WriteAllText(
            Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), name + ".zip\n");
    }

    [Fact]
    public async Task ADrainArrivingMidWalk_StopsTheWalkAtTheNextStep_AndTheJoinIsReal()
    {
        SealedSource("alpha");
        SealedSource("bravo");
        SealedSource("charlie");

        // A short grace: nothing in the walk completes while the first read is parked, so the drain
        // moves to its cancel as soon as the grace has seen no progress.
        using var pool = new IoPool(2, drainTimeout: Bound, drainGrace: TimeSpan.FromMilliseconds(200));
        var entered = new AsyncSubject<Unit>();
        var outcome = new AsyncSubject<string>();
        var sealReads = 0;

        pool.InvokeBlocking(ct => ShippedPrebuiltBundles.CompletePublishedBundlesOf(
                IdentityDirectory, logger: null, ct,
                readLines: path =>
                {
                    if (Interlocked.Increment(ref sealReads) == 1)
                    {
                        entered.OnNext(Unit.Default);
                        entered.OnCompleted();
                        // The step in flight when the pod stops: a read on a slow share that returns
                        // once it returns — here, once the drain has cancelled the leaf.
                        SpinWait.SpinUntil(() => ct.IsCancellationRequested, Bound);
                    }
                    return File.ReadAllLines(path);
                }))
            .Subscribe(
                bundles =>
                {
                    outcome.OnNext($"completed with {bundles.Count} bundle(s)");
                    outcome.OnCompleted();
                },
                ex =>
                {
                    outcome.OnNext(ex.GetType().Name);
                    outcome.OnCompleted();
                });

        await entered.Should().Within(Bound).Emit("the walk reaches its first seal read");

        var drain = Task.Run(pool.Drain, TestContext.Current.CancellationToken);
        var residual = await drain.WaitAsync(Bound, TestContext.Current.CancellationToken);

        residual.Should().Be(0,
            "the blocking leaf unwound once the drain cancelled it — the join is real, nothing is "
            + "left running when the silo releases the mesh");
        Volatile.Read(ref sealReads).Should().Be(1,
            "the walk must stop at the step boundary after the drain's cancel, not read the seals of "
            + "the sources it had not reached yet — a walk that ignores its token walks the whole "
            + "share while the silo's teardown join waits on it (MeshWeaver#2480)");
        (await outcome.Should().Within(Bound).Emit("the leaf reports how it ended"))
            .Should().Be(nameof(OperationCanceledException),
                "a walk the pool ended is a cancellation, never a finished listing handed to a "
                + "process that is tearing down");
    }

    [Fact]
    public void AnUncancelledWalk_StillListsEverySealedSource()
    {
        // The positive control: a walk that stopped early for any reason at all would satisfy the
        // test above, so the same fixture must still list everything when nothing cancels it.
        SealedSource("alpha");
        SealedSource("bravo");
        SealedSource("charlie");

        ShippedPrebuiltBundles.CompletePublishedBundlesOf(IdentityDirectory, logger: null, CancellationToken.None)
            .Should().HaveCount(3);
    }

    [Fact]
    public void TheImageDirectoryListing_StopsOnACancelledToken()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "b.zip"), "zip");
        File.WriteAllText(Path.Combine(root, "a.zip"), "zip");

        ShippedPrebuiltBundles.ImageBundlesOf(root, CancellationToken.None)
            .Should().Equal([Path.Combine(root, "a.zip"), Path.Combine(root, "b.zip")],
                "an uncancelled listing is every archive, in ordinal order");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ShippedPrebuiltBundles.ImageBundlesOf(root, cancelled.Token));
    }

    [Fact]
    public void TheInventoryRead_PropagatesACancel_ItDoesNotCallTheShelfUnreadable()
    {
        // PrebuiltBundleInventory.Read folds every I/O failure into Unreadable — correctly, "I could
        // not look" is not "there is nothing here". A drain's cancel is neither: the process is
        // stopping. Folded in, it would be reported to the hold gate as an unreadable shelf.
        SealedSource("alpha");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PrebuiltBundleInventory.Read(
            imageDirectory: null, root, "s72c27afab89c100727e79e0559c59e32", logger: null, cancelled.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
