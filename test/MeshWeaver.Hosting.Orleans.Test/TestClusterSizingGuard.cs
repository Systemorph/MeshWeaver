using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Governance guard for the invariant <b>"every test cluster in this assembly is sized for a TEST
/// cluster"</b> — see <see cref="TestGrainDirectorySizing"/> and issue #2346.
///
/// <para><b>Why a guard and not care.</b> The cost is invisible at the call site: a class writes
/// <c>new TestClusterBuilder()</c>, which reads as completely ordinary, and Orleans quietly
/// pre-allocates a ~9.3 MB grain-directory bucket array per silo because that is the production
/// default. It shows up nowhere except as a slowly growing heap and, eventually, as some UNRELATED
/// test elsewhere in the process failing its budget during a multi-second GC pause. That is a defect
/// nobody attributes correctly by reading a diff — which is exactly the shape a control catches and
/// review does not.</para>
///
/// <para><b>The rule.</b> Any file that constructs a <see cref="Orleans.TestingHost.TestClusterBuilder"/>
/// must also name <see cref="TestGrainDirectorySizing"/>. Most classes get it for free by going
/// through <c>OrleansTestCluster.DeployAsync</c> (which registers it centrally, so the natural path
/// needs no opt-in and this guard never fires for it); the ten classes that hand-roll their own
/// builder opt in on the line after they create it.</para>
///
/// <para>Scoped to this assembly's own sources on purpose: it is a statement about how THIS test
/// project builds clusters, not a repo-wide policy.</para>
/// </summary>
public class TestClusterSizingGuard(ITestOutputHelper output)
{
    private const string BuilderConstruction = "new TestClusterBuilder()";
    private const string Sizing = nameof(TestGrainDirectorySizing);

    [Fact]
    public void EveryFileThatBuildsATestCluster_AlsoSizesItsGrainDirectory()
    {
        var files = SourceFiles();

        // A scanner that matches nothing passes vacuously — assert it found the sites first, so this
        // guard cannot quietly become a no-op if the directory layout or the API name changes.
        var builders = files.Where(f => File.ReadAllText(f).Contains(BuilderConstruction, StringComparison.Ordinal))
            .ToArray();
        builders.Should().NotBeEmpty(
            $"the scanner must find the '{BuilderConstruction}' sites it exists to check — finding none "
            + "means the guard is inspecting the wrong tree, not that the invariant holds");

        var unsized = builders
            .Where(f => !File.ReadAllText(f).Contains(Sizing, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        output.WriteLine($"{builders.Length} file(s) build a TestCluster; {unsized.Length} unsized.");

        unsized.Should().BeEmpty(
            "a test cluster left at Orleans' production GrainDirectoryOptions.CacheSize (1,000,000) "
            + "pre-allocates ~9.3 MB of buckets per silo, and this assembly starts ~194 silos in one "
            + "process — the multi-GB heap that produces stalls the WHOLE process for seconds under "
            + "workstation GC and expires whichever unrelated test's budget is open (#2346). Add "
            + $"`builder.AddSiloBuilderConfigurator<{Sizing}>();`, or build the cluster through "
            + "OrleansTestCluster.DeployAsync, which does it for you. Unsized: "
            + string.Join(", ", unsized));
    }

    /// <summary>
    /// This project's own <c>.cs</c> files, located from the repo root the same way
    /// <see cref="GrainActivationSiteRatchetGuard"/> does (walk up to <c>MeshWeaver.slnx</c>) — the
    /// resolution that is already proven to work from a CI shard's working directory.
    /// </summary>
    private static string[] SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);

        // Both homes of cluster-building code: this project's tests AND the machinery that moved to
        // src/MeshWeaver.Hosting.Orleans.TestBase (OrleansTestCluster and friends) so that suites in
        // other repositories can build on it. A guard follows its subject — scanning only this
        // directory would have let the builder itself drop the sizing without a red test.
        var homes = new[]
        {
            Path.Combine(dir.FullName, "test", "MeshWeaver.Hosting.Orleans.Test"),
            Path.Combine(dir.FullName, "src", "MeshWeaver.Hosting.Orleans.TestBase"),
        };
        foreach (var home in homes)
            if (!Directory.Exists(home))
                throw new InvalidOperationException("Could not locate the sources at " + home);

        return homes
            .SelectMany(home => Directory.EnumerateFiles(home, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
    }
}
