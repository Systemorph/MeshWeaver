using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#5223. On silo teardown the I/O drain reported
/// <c>Did NOT report: prebuilt:files=1 [ShippedPrebuiltBundles+&lt;&gt;c__DisplayClass24_0.&lt;SeedBundles&gt;b__8]</c>
/// three times in two hours: the bundle-enumeration leaf held the drain for the whole 30 s budget
/// and never answered the cooperative cancel. The leaf was <c>_ => enumerateBundles()</c> — the
/// pool's token discarded at the door — around a walk of a network share (every admitted identity
/// directory, every source, its seal, every listed file) that took no token at all.
///
/// <para>These pin the walk's half: cancelled between two sources, it stops at the next one and
/// says so with <see cref="OperationCanceledException"/> — never by returning the sources it had
/// reached as if they were the whole publication. The pool's half is a method group now
/// (<c>InvokeBlocking(enumerateBundles)</c>), so the token cannot be dropped at the door again
/// without changing the signature every caller compiles against.</para>
/// </summary>
public class SeedingWalkObservesItsPoolTokenTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-seed-token-" + Guid.NewGuid().ToString("N"));

    private string IdentityDirectory => Path.Combine(root, "s72c27afab89c100727e79e0559c59e32");

    private string SealedSource(string name, string bundle)
    {
        var dir = Path.Combine(IdentityDirectory, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, bundle), "zip");
        File.WriteAllText(
            Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), bundle + "\n");
        return dir;
    }

    [Fact]
    public void CancelledMidWalk_StopsAtTheNextSource_AndSaysSo()
    {
        SealedSource("alpha", "Alpha.zip");
        SealedSource("zulu", "Zulu.zip");
        using var pool = new CancellationTokenSource();
        var sealsRead = new List<string>();

        // The pool cancels while the walk is reading "alpha" — the teardown arriving mid-leaf.
        string[] ReadAndCancel(string seal)
        {
            sealsRead.Add(Path.GetFileName(Path.GetDirectoryName(seal))!);
            pool.Cancel();
            return File.ReadAllLines(seal);
        }

        Assert.Throws<OperationCanceledException>(() => ShippedPrebuiltBundles.CompletePublishedBundlesOf(
            IdentityDirectory, logger: null, ReadAndCancel, cancellationToken: pool.Token));

        Assert.Equal(["alpha"], sealsRead);
    }

    [Fact]
    public void TheImageMountWalk_ObservesItsTokenToo()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "A.zip"), "zip");
        using var pool = new CancellationTokenSource();
        pool.Cancel();

        Assert.Throws<OperationCanceledException>(() => ShippedPrebuiltBundles.ZipsIn(root, pool.Token));
    }

    /// <summary>Control: an uncancelled walk is unchanged — every sealed source, in order.</summary>
    [Fact]
    public void AnUncancelledWalk_SeedsEverySealedSource()
    {
        var alpha = SealedSource("alpha", "Alpha.zip");
        var zulu = SealedSource("zulu", "Zulu.zip");
        using var pool = new CancellationTokenSource();

        Assert.Equal(
            [Path.Combine(alpha, "Alpha.zip"), Path.Combine(zulu, "Zulu.zip")],
            ShippedPrebuiltBundles.CompletePublishedBundlesOf(
                IdentityDirectory, logger: null, cancellationToken: pool.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
