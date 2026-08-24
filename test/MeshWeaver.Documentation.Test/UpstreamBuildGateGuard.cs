using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the build gate (#1755) in the reusable <c>node-repo-publish-bake</c>
/// workflow — the ONE lane every node repo bakes and publishes through.
///
/// <para>🚨 <b>Why a guard and not a test:</b> nothing else in this repository can fail when this
/// gate regresses. It runs in OTHER repositories' CI, so a change that quietly makes it skippable —
/// a <c>continue-on-error</c>, an <c>if:</c> that asks whether a secret is set, an exit code
/// swallowed into a warning — would be invisible here and would show up as four satellites
/// publishing bakes gated against the wrong framework. Each assertion below names the failure it
/// would let through.</para>
/// </summary>
public class UpstreamBuildGateGuard
{
    private const string Workflow = ".github/workflows/node-repo-publish-bake.yml";

    private static string ExecutableLinesOf(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    /// <summary>The workflow's steps, comments stripped — a guard must judge what RUNS, never the
    /// prose explaining it.</summary>
    private static string Body() => ExecutableLinesOf(
        File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow)));

    [Fact]
    public void TheGateAsksTheSharedPredicate_AgainstTheIdentityTheImageResolves()
    {
        var body = Body();

        Assert.Contains("--print-framework-identity", body, StringComparison.Ordinal);
        Assert.Contains("check-release-availability.sh", body, StringComparison.Ordinal);
        Assert.Contains("upstream-sources", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The gate must run BEFORE anything is built. A gate that ran after the bake would have
    /// burned the runner and produced the very artifact it exists to prevent — and, worse, would
    /// still look like it was doing its job.
    /// </summary>
    [Fact]
    public void TheGateRunsBeforeTheBake()
    {
        var body = Body();
        var gate = body.IndexOf("check-release-availability.sh", StringComparison.Ordinal);
        var bake = body.IndexOf("--bake-output", StringComparison.Ordinal);

        Assert.True(gate > 0 && bake > 0 && gate < bake,
            "the upstream gate must be evaluated before the bake step — checking afterwards proves "
            + "nothing and wastes the whole run.");
    }

    /// <summary>
    /// 🚨 No skip-trapdoor. GitHub renders a skipped job with the same tick as a passed one, so a
    /// gate that can be skipped or whose failure is downgraded is indistinguishable from one that
    /// passed. This asserts the two shapes that would do it.
    /// </summary>
    [Fact]
    public void TheGateCannotBeSkippedOrDowngraded()
    {
        var body = Body();

        Assert.DoesNotContain("continue-on-error", body, StringComparison.Ordinal);

        // The gate's own refusal path must terminate the job.
        var gateBlock = SectionAfter(body, "Gate: every upstream must be published");
        Assert.Contains("exit 1", gateBlock, StringComparison.Ordinal);
        Assert.Contains("::error", gateBlock, StringComparison.Ordinal);
    }




    /// <summary>Everything from <paramref name="marker"/> to the next step boundary at the same
    /// indent, so an assertion about one step cannot be satisfied by another.</summary>
    private static string SectionAfter(string body, string marker)
    {
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{marker}' is gone from {Workflow} — the gate it belongs to has "
                                + "been removed or renamed, which no other check would catch.");
        var next = body.IndexOf("\n      - name:", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? body[start..] : body[start..next];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// 🚨 The wave is PULLED, and no credential may creep back in.
    ///
    /// <para>This workflow used to POST <c>meshweaver-upstream-published</c> to a declared list of
    /// dependent repos, using a token with write access to each. Both are gone: a publication is a
    /// FACT about the registry, and a dependent repo's scheduled run reads memex for the released
    /// image and rebuilds if it must.</para>
    ///
    /// <para>Pinned because the tempting fix, when a satellite is found a release behind, is to add
    /// the token back — which reintroduces a cross-repo write credential AND a hand-maintained
    /// second copy of a graph memex already holds, whose missing entry fails silently.</para>
    /// </summary>
    [Fact]
    public void ThereIsNoDependentDispatch_AndNoCredentialForOne()
    {
        var body = Body();

        Assert.DoesNotContain("dependent-dispatch-token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("meshweaver-upstream-published", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/dispatches", body, StringComparison.Ordinal);
    }
}
