using System.Linq;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ordering used to resolve "the latest framework".
///
/// <para>🚨 <b>The failure this prevents is silent.</b> Continuous framework builds are versioned
/// <c>3.0.0-rc3.ci.&lt;run-number&gt;</c>. Ordered as TEXT, <c>ci.900</c> sorts above
/// <c>ci.3758</c> — `9` &gt; `3` — so "latest" picks a framework thousands of runs stale, every
/// plugin compiles against it successfully (it is a real framework), and the only symptom is a
/// missing API surfacing much later.</para>
/// </summary>
public class NuGetVersionComparerTest
{
    private static string Latest(params string[] versions) =>
        versions.OrderBy(v => v, NuGetVersionComparer.Instance).Last();

    [Fact]
    public void CiBuildNumbersCompareNumerically_NotAsText()
    {
        // The case the type exists for.
        Assert.Equal("3.0.0-rc3.ci.3758", Latest("3.0.0-rc3.ci.900", "3.0.0-rc3.ci.3758"));
        Assert.Equal("3.0.0-rc3.ci.10", Latest("3.0.0-rc3.ci.10", "3.0.0-rc3.ci.9"));
    }

    [Fact]
    public void MoreIdentifiersOutrankFewer()
        // A continuous build is NEWER than the bare pre-release it derives from, which is why a
        // floor of `3.0.0-rc3` would accept any ci build and truncating the suffix loses the point.
        => Assert.Equal("3.0.0-rc3.ci.1", Latest("3.0.0-rc3", "3.0.0-rc3.ci.1"));

    [Fact]
    public void ReleaseOutranksItsPreReleases()
        => Assert.Equal("3.0.0", Latest("3.0.0-rc3.ci.9999", "3.0.0-rc3", "3.0.0"));

    [Fact]
    public void PreReleaseLabelsOrderAlphabetically()
        => Assert.Equal("3.0.0-rc2", Latest("3.0.0-preview1", "3.0.0-rc1", "3.0.0-rc2"));

    [Fact]
    public void NumericCoreComparesNumerically()
        => Assert.Equal("3.10.0", Latest("3.9.0", "3.10.0"));

    [Fact]
    public void BuildMetadataIsIgnoredForOrdering()
    {
        // CIRun stamps +build.<ticks> into InformationalVersion; SemVer excludes it from ordering,
        // so it must not decide which package is newest.
        Assert.Equal(0, NuGetVersionComparer.Instance.Compare("3.0.0-rc3.ci.5+build.1", "3.0.0-rc3.ci.5+build.9"));
    }

    [Fact]
    public void TheRealNugetOrgSetOrdersAsPublished()
        => Assert.Equal("3.0.0-rc2", Latest("3.0.0-preview1", "3.0.0-rc1", "3.0.0-rc2"));
}
