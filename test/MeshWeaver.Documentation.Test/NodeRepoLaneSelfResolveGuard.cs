#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the shape the node-repo gate and publish-bake lanes took on 2026-09-12
/// (maintainer rule, verbatim: <i>"for compile always find latest package of platform and
/// plugins"</i>): <b>a lane called with EMPTY digests resolves the platform itself</b> — with core's
/// <c>.github/scripts/resolve-platform.py</c>, fetched at the lane's <c>scripts-ref</c> like every
/// other lane script — and a lane called with PINNED digests takes them verbatim.
///
/// <para>Why a text guard: a satellite cannot refresh a <c>uses:</c> job (its digests are call-time
/// inputs from the caller's stored <c>platform-ref</c> outputs), so the lane resolving itself is the
/// ONLY way a "re-run failed jobs" on the gate or the bake tests the newest packages. The property
/// lives in CI, not in the product: a lane that quietly went back to reading
/// <c>inputs.image-digest</c> at the image step would still be valid YAML, still green on a pinned
/// caller, and would hand every unpinned caller a red "image-digest is empty" the day the satellites
/// stop passing digests. Nothing in a green run distinguishes the two shapes; this does.</para>
/// </summary>
public class NodeRepoLaneSelfResolveGuard
{
    private const string PublishBake = ".github/workflows/node-repo-publish-bake.yml";
    private const string Gate = ".github/workflows/node-repo-gate.yml";
    private const string Resolver = ".github/scripts/resolve-platform.py";

    [Theory]
    [InlineData(PublishBake)]
    [InlineData(Gate)]
    public void TheLane_FetchesTheSharedResolverAtItsScriptsRef_AndSelfTestsItFirst(string workflow)
    {
        var lines = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), workflow)));

        // The same fetch shape as compose-gate-host.sh: core's script, at the LANE's scripts ref —
        // never at the platform's commit (hours behind the lane) and never a vendored copy.
        Assert.Contains(
            "gh api \"repos/Systemorph/MeshWeaver/contents/.github/scripts/resolve-platform.py?ref=${SCRIPTS_REF}\"",
            lines, StringComparison.Ordinal);
        // An unproven resolver is no resolver: the offline cases run before the real API is read.
        Assert.Contains("python3 \"$SCRIPT\" --self-test", lines, StringComparison.Ordinal);
        // The freeze is the CALLER's repository variable — `vars` in a reusable workflow resolve
        // from the caller's repository — and it is passed to the resolver, never dropped.
        Assert.Contains("FREEZE: ${{ vars.MW_PLATFORM_REF }}", lines, StringComparison.Ordinal);
        Assert.Contains("${FREEZE:+--freeze \"$FREEZE\"}", lines, StringComparison.Ordinal);
        // A half-pin is refused by name, never composed.
        Assert.Contains("a half-pin would compose two CD waves", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGate_ResolvesInPlan_AndEveryShardRefreshesAgainstPlansBaseline()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Gate));
        var lines = ExecutableLinesOf(text);

        // plan hands the shards the digests AND its whole resolver row as their baseline.
        Assert.Contains("image-digest: ${{ steps.digests.outputs.image-digest }}", lines, StringComparison.Ordinal);
        Assert.Contains("platform-image-digest: ${{ steps.digests.outputs.platform-image-digest }}", lines, StringComparison.Ordinal);
        Assert.Contains("baseline: ${{ steps.digests.outputs.baseline }}", lines, StringComparison.Ordinal);

        // A shard re-resolves against that baseline (Education#320: a re-run of ONE red shard must
        // test the newest set) …
        Assert.Contains("PLATFORM_BASELINE: ${{ needs.plan.outputs.baseline }}", lines, StringComparison.Ordinal);
        // … and the image step reads the SHARD's answer, never the raw inputs.
        var images = StepBlock(text, "id: images");
        Assert.Contains("IMAGE_DIGEST: ${{ steps.platform.outputs.image-digest }}", images, StringComparison.Ordinal);
        Assert.Contains("PLATFORM_DIGEST: ${{ steps.platform.outputs.platform-image-digest }}", images, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.image-digest }}", images, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.platform-image-digest }}", images, StringComparison.Ordinal);

        // The caller learns what was gated.
        Assert.Contains("value: ${{ jobs.plan.outputs.platform-set }}", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishBake_ResolvesBeforeTheImageSteps_AndTheyReadItsAnswer()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), PublishBake));
        var lines = ExecutableLinesOf(text);

        var digests = lines.IndexOf("id: digests", StringComparison.Ordinal);
        var image = lines.IndexOf("id: image\n", StringComparison.Ordinal);
        var platform = lines.IndexOf("id: platform\n", StringComparison.Ordinal);
        Assert.True(digests > 0 && image > digests && platform > image,
            $"{PublishBake}: the resolve step (id: digests) must precede the bake-image and platform steps (digests={digests}, image={image}, platform={platform})");

        var imageStep = StepBlock(text, "id: image\n");
        Assert.Contains("IMAGE_DIGEST: ${{ steps.digests.outputs.image-digest }}", imageStep, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.image-digest }}", imageStep, StringComparison.Ordinal);
        var platformStep = StepBlock(text, "id: platform\n");
        Assert.Contains("PLATFORM_DIGEST: ${{ steps.digests.outputs.platform-image-digest }}", platformStep, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.platform-image-digest }}", platformStep, StringComparison.Ordinal);

        // The two dispatch shapes keep naming their own image: the resolver steps aside for them.
        var resolve = StepBlock(text, "id: digests");
        Assert.Contains("meshweaver-upstream-published", resolve, StringComparison.Ordinal);
        Assert.Contains("meshweaver-framework-released", resolve, StringComparison.Ordinal);
        // The release poll waits for the set sealing right now, bounded, as the satellites do.
        Assert.Contains("--wait-for-seal \"$wait\"", resolve, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResolver_ExistsAndCarriesTheRuleAndASelfTest()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Resolver));
        Assert.StartsWith("#!/usr/bin/env python3", text, StringComparison.Ordinal);
        // The seal is the TRIO — neither the run's conclusion nor its own Plugins seal decides.
        foreach (var job in new[] { "Promote: tag the full set", "Verify every image shipped",
                     "Bake platform content in the shipped image + publish" })
            Assert.Contains($"\"{job}\"", text, StringComparison.Ordinal);
        Assert.Contains("PLUGINS_SEAL_JOB = (\"plugins seal\", \"Plugins: bake + seal the publication for this identity\")", text, StringComparison.Ordinal);
        Assert.Contains("--self-test", text, StringComparison.Ordinal);
        Assert.Contains("PLATFORM_BASELINE", text, StringComparison.Ordinal);
        // Its output keys are the contract the lanes re-key from; keep them stable across the fleet.
        foreach (var key in new[] { "\"image-digest\"", "\"portal-image-digest\"", "\"set\"", "\"run-number\"", "\"plugins-set\"", "\"override\"" })
            Assert.Contains(key, text, StringComparison.Ordinal);
    }

    /// <summary>The step block that carries <paramref name="marker"/>: from the previous
    /// <c>- name:</c> to the next one at the same indentation.</summary>
    private static string StepBlock(string yaml, string marker)
    {
        var at = yaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{marker.Trim()}' is gone from the lane — the step it belongs to has been removed or renamed");
        var start = yaml.LastIndexOf("\n      - name:", at, StringComparison.Ordinal);
        var end = yaml.IndexOf("\n      - name:", at, StringComparison.Ordinal);
        return yaml.Substring(start, (end < 0 ? yaml.Length : end) - start);
    }

    private static string ExecutableLinesOf(string yaml) =>
        string.Join('\n', yaml.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
