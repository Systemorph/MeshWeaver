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
    /// 🚨 THE LANE ENDS BY CALLING MEMEX AND DISPATCHES TO NOBODY. Maintainer, 2026-09-03: "end of
    /// github pipeline must call memex, which must register release and publish event". After a
    /// successful publish the lane's LAST job POSTs the signed publication record
    /// (<c>event: bundle-publication</c>) into the control instance's inbox; memex registers it and
    /// emits <c>meshweaver-upstream-published</c> to the subscribed repositories. The
    /// <c>dispatch-dependents</c> job that used to read every installed repo's ci.yml and POST to
    /// their <c>/dispatches</c> was CI-to-CI coupling and is gone. Pinned: the call exists, it is
    /// signed, it is RED (never skipped, never a warning) when unprovisioned or refused, and no
    /// dispatch of any kind remains in the lane.
    /// </summary>
    [Fact]
    public void TheLaneEndsByRegisteringWithMemex_AndDispatchesToNobody()
    {
        var body = Body();
        Assert.DoesNotContain("dependent-dispatch-token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  dispatch-dependents:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/dispatches", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event_type", body, StringComparison.Ordinal);

        var job = JobBlock(body, "register-publication:");
        Assert.Contains("needs.publish-bake.outputs.published == 'true'", job, StringComparison.Ordinal);
        Assert.Contains("inputs.webhook-url", job, StringComparison.Ordinal);
        Assert.Contains("secrets.webhook-secret", job, StringComparison.Ordinal);
        Assert.Contains("\"bundle-publication\"", job, StringComparison.Ordinal);
        Assert.Contains("X-Hub-Signature-256", job, StringComparison.Ordinal);
        Assert.Contains("openssl dgst -sha256 -hmac", job, StringComparison.Ordinal);
        Assert.Contains("code=000", job, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);
        Assert.DoesNotContain("::warning", job, StringComparison.Ordinal);
        Assert.Contains("exit 1", job, StringComparison.Ordinal);
        // The job may depend only on the run's publish outcome — never on whether its inputs exist.
        var condition = job[..job.IndexOf("runs-on:", StringComparison.Ordinal)];
        Assert.DoesNotContain("inputs.webhook", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", condition, StringComparison.Ordinal);
    }

    /// <summary>Everything from a job's key to the next job key at the same indent.</summary>
    private static string JobBlock(string body, string jobKey)
    {
        var start = body.IndexOf("\n  " + jobKey, StringComparison.Ordinal);
        Assert.True(start >= 0, $"job '{jobKey}' is gone from {Workflow} — it has been removed or "
                                + "renamed, which no other check would catch.");
        var next = body.IndexOf("\n  ", start + jobKey.Length + 3, StringComparison.Ordinal);
        while (next >= 0 && next + 3 < body.Length && body[next + 3] == ' ')
            next = body.IndexOf("\n  ", next + 1, StringComparison.Ordinal);
        return next < 0 ? body[start..] : body[start..next];
    }
}
