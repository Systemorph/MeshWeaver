using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the ONE invariant of the platform's prebuilt-NodeType delivery lane
/// (issue #1725): <b>a bake must be produced by the binaries the consumer runs.</b>
///
/// <para>The framework build identity (<c>FrameworkBuildIdentity</c>) is derived from the exact
/// assemblies a host ships — implementation MVIDs for the toolchain closure, reference-assembly
/// hashes for the rest — so it is a property of the BINARIES, not of the source they were built
/// from. Two compilations of one commit resolve DIFFERENT identities. Measured on <c>babb3bc</c>,
/// same sources and same runner path, between the Build-and-Test job's build output and the image
/// that same commit shipped: every implementation assembly's MVID differed (controls included),
/// and 4 of the 37 canonical reference assemblies differed too. The two hosts resolved
/// <c>sd0d0daa…</c> and <c>s377941f…</c>.</para>
///
/// <para>🚨 What that cost, and what this guard prevents from recurring: <c>main-cd</c> used to
/// publish the Build-and-Test run's <c>baked-assemblies-*</c> artifact — a different compilation —
/// so <c>prebuilt-bundles/&lt;runtime-identity&gt;/</c> never contained the platform's own content
/// and every pod Roslyn-compiled ~80 shipped NodeTypes on EVERY boot (64.8 s warm-up,
/// <c>compiled=80 alreadyBaked=4</c>). Nothing went red: the artifact existed, the publish
/// succeeded, and the identity nobody compared was the whole defect. A cross-build hop is
/// therefore not a thing to review case by case — it is banned outright, and the fix (bake inside
/// the shipped image, exactly as every satellite content repo does) is asserted positively so
/// "the lane quietly stopped baking" cannot pass either.</para>
///
/// <para>This is a text guard over the workflow because the invariant lives in CI, not in the
/// product: no C# unit test can observe which build produced a published bundle. See
/// Doc/Architecture/CiContentBake.</para>
/// </summary>
public class PlatformBakeLaneGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";
    private const string JobName = "publish-bake";

    [Fact]
    public void PlatformBake_RunsInsideTheShippedImage()
    {
        var job = ReadJobBlock();

        Assert.True(job.Contains("docker run", StringComparison.Ordinal)
                    && job.Contains("mw-plugin-test", StringComparison.Ordinal)
                    && job.Contains("--bake-output", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must bake the platform's content by running mw-plugin-test "
            + "INSIDE the image this run built (docker run … mw-plugin-test … --bake-output). The "
            + "framework identity is a property of the shipped binaries, so only the image the pods "
            + "run can produce a bake those pods will adopt.");

        Assert.True(job.Contains("--platform linux/amd64", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must pin the bake platform explicitly. Architecture is part "
            + "of the identity — the amd64 and arm64 variants of one multi-arch image resolve "
            + "different identities — and every AKS node is amd64, so inheriting whatever "
            + "architecture the runner happens to be would silently key the bake to an identity no "
            + "pod resolves.");

        Assert.True(job.Contains("publish-bake-bundles.sh", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must publish through .github/scripts/publish-bake-bundles.sh "
            + "— the one script whose '_complete' sentinel matches "
            + "ShippedPrebuiltBundles.CompletionSentinelFileName.");
    }

    [Fact]
    public void PlatformBake_NeverAdoptsAnotherBuildsBundles()
    {
        var workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        var offenders = Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.TopDirectoryOnly)
            .Select(f => (file: Path.GetFileName(f), text: File.ReadAllText(f)))
            .Where(x => x.text.Contains("publish-bake-bundles.sh", StringComparison.Ordinal))
            .Where(x => x.text.Contains("gh run download", StringComparison.Ordinal)
                        || x.text.Contains("download-artifact", StringComparison.Ordinal))
            .Select(x => x.file)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A workflow that publishes prebuilt bundles must never take them from a cross-run "
            + "artifact — that is a DIFFERENT compilation of the same source and resolves a "
            + "different framework identity, so no pod can adopt what it publishes (#1725). "
            + "Bake inside the image being shipped instead. Offending workflow(s): "
            + string.Join(", ", offenders));
    }

    /// <summary>The <c>publish-bake</c> job's own YAML block: from its two-space-indented key to
    /// the next job key at the same indentation. The terminator is matched as a bare
    /// <c>  &lt;name&gt;:</c> line rather than "indented and ends with a colon", so a two-space
    /// comment between jobs cannot truncate the block and turn this guard into a false failure.
    /// </summary>
    private static string ReadJobBlock()
    {
        var path = Path.Combine(FindRepoRoot(), Workflow);
        Assert.True(File.Exists(path), $"expected {Workflow} at the repo root");
        var lines = File.ReadAllLines(path);

        var start = Array.FindIndex(lines, l => l.Equals($"  {JobName}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no '{JobName}:' job in {Workflow} — the platform's own content "
            + "bake is what publishes the shipped NodeType bundles the portals adopt; deleting it "
            + "returns every pod to compiling every shipped type at boot (#1347, #1725).");

        var end = Array.FindIndex(lines, start + 1, IsJobKey);
        if (end < 0)
            end = lines.Length;
        return string.Join("\n", lines[start..end]);
    }

    private static bool IsJobKey(string line) =>
        line.Length > 3
        && line.StartsWith("  ", StringComparison.Ordinal)
        && line[2] != ' '
        && line[^1] == ':'
        && line[2..^1].All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
