using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The guard that guards the guards (#2844).
///
/// <para><see cref="SourceScan.SourceFiles"/> is the one seam every source-tree guard and ratchet in
/// this project passes through — 34 of them. For a zero-tolerance guard, "no offenders found" and
/// "nothing was scanned" are the SAME result, so an empty scan reports green while enforcing
/// nothing. Only 4 of the 34 asserted that their scan found anything; the rest inherited it from
/// here once <c>SourceFiles</c> started refusing an empty result.</para>
///
/// <para>This is not hypothetical. Relocating <c>MeshWeaver.Documentation.Test</c> into another
/// repository — a one-line csproj move, seriously considered on 2026-08-30 as part of a test
/// migration — would have pointed <c>FindRepoRoot()</c> at a tree with no <c>src/</c>, dropping
/// every root and disarming ~30 rules silently and permanently.</para>
///
/// <para>🚨 <b>Why the existing <c>TheScannerSeesWhatItClaimsTo</c> self-tests do not cover this.</b>
/// Planting a temp tree and running the real scanner over it proves the SCANNER works. It cannot
/// prove the scanner found the PRODUCTION tree. Those two fail independently, and only the second
/// produces a wall of green over an unenforced rule — so it needs its own test, which is this one.</para>
/// </summary>
public class SourceScanFloorTest
{
    /// <summary>
    /// A root with no source files is a BROKEN SCAN. Fail-without: an empty sequence, and every
    /// caller reports clean. Pass-with: a throw naming the roots.
    /// </summary>
    [Fact]
    public void AnEmptyScanThrows_RatherThanReportingACleanTree()
    {
        var dir = Directory.CreateTempSubdirectory("sourcescan-floor");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "src"));   // exists, but empty

            var ex = Assert.Throws<InvalidOperationException>(
                () => SourceScan.SourceFiles(dir.FullName, ["src"]).ToArray());

            Assert.Contains("BROKEN SCAN", ex.Message, StringComparison.Ordinal);
            Assert.Contains("checked nothing", ex.Message, StringComparison.Ordinal);
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// The wrong-tree case, which is the one that actually happens: the roots are not there at all
    /// because the repo root resolved somewhere unexpected. The message must NAME the missing roots,
    /// or the reader cannot tell a mis-rooted scan from a genuinely empty one.
    /// </summary>
    [Fact]
    public void AMissingRootThrows_AndNamesWhatWasNotThere()
    {
        var dir = Directory.CreateTempSubdirectory("sourcescan-wrongtree");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SourceScan.SourceFiles(dir.FullName, ["src", "memex"]).ToArray());

            Assert.Contains("do not exist", ex.Message, StringComparison.Ordinal);
            Assert.Contains("src", ex.Message, StringComparison.Ordinal);
            Assert.Contains("memex", ex.Message, StringComparison.Ordinal);
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// 🚨 And it must NOT fire on the real tree, or the cure is worse than the disease — a floor
    /// that false-positives gets removed, taking the protection with it. An optional root that is
    /// absent stays tolerated, because the check is on the RESULT, not on every root existing.
    /// </summary>
    [Fact]
    public void TheRealTreeScansFine_AndAnAbsentOptionalRootIsStillTolerated()
    {
        var root = SourceScan.FindRepoRoot();

        SourceScan.SourceFiles(root, ["src"]).Should().NotBeEmpty();
        SourceScan.SourceFiles(root, ["src", "no-such-root-here"]).Should().NotBeEmpty(
            "one missing root among several must not break a scan that still found the tree — "
            + "the floor is on the RESULT, so callers naming an optional root keep working");
    }
}
