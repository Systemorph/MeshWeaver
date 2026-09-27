#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The shared-rule gate's exemption is written TWICE — on the <c>shared-rules</c> job and on the
/// <c>collect-results</c> step that fails the build when that job did not succeed. Both files'
/// comments assert the two "can never disagree". Nothing enforced that, which is the shape
/// <c>AGENTS.md</c> warns about: prose asserting a guard that does not exist.
///
/// <para><b>What drift would cost.</b> If the gate exempts an event that the failure step does not,
/// the job is skipped and <c>collect-results</c> then fails the build for a gate that was never
/// meant to run — a red nobody can fix from a pull request. If the failure step exempts an event the
/// gate does not, a genuine drift in the shared rule blocks passes silently. Neither direction is
/// detectable by reading one of the two in isolation.</para>
///
/// <para><b>And the exemption must stay EVENT-shaped.</b> <c>AGENTS.md</c> → "A gate NEVER tests its
/// own inputs": an <c>if:</c> that asks whether a secret is set turns "the credential is missing"
/// into a green tick, because GitHub paints a skipped job the same colour as a passed one. The ONE
/// sanctioned exemption names a situation in which GitHub withholds the credential BY DESIGN — a
/// fork PR — so the job genuinely cannot run. That is a statement about the event, not the input.</para>
///
/// <para>🚨 <b>Dependabot is NOT exempt any more, and this guard now forbids the clause</b> (policy
/// <c>merge-on-own-green</c>). From 2026-08-31 both sites carried
/// <c>github.actor != 'dependabot[bot]'</c>, and it was sound for ONE reason: the workflow also ran on
/// <c>merge_group</c>, where the Actions secret store is available, so a Dependabot PR still had
/// these gates checked before it landed. With core's merge queue retired a PR lands on its OWN green,
/// so the same clause would skip the gate outright — a skip that reads as a pass. The credential
/// (<c>FLEET_READER_APP_*</c>) is provisioned in the Dependabot store, so a Dependabot PR runs the
/// gate like any other; were it missing there, the gate's own assertion reds naming it.</para>
/// </summary>
public class SharedRuleExemptionIsOneEventPredicateGuard
{
    private const string Workflow = "dotnet-test.yml";

    /// <summary>The clauses that together ARE the exemption. Both sites must carry all of them.</summary>
    private static readonly string[] ExemptionClauses =
    [
        "github.event.pull_request.head.repo.fork != true",
    ];

    /// <summary>A clause no site may carry: with no merge queue it would skip the gate outright.</summary>
    private const string RetiredDependabotClause = "dependabot[bot]";

    private static string WorkflowText() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", Workflow));

    /// <summary>
    /// The actual <c>if:</c> EXPRESSIONS carrying the exemption — never comments. The first version
    /// of this matched any line containing <c>head.repo.fork</c> and immediately failed on the
    /// explanatory comment beside the predicate, which is the right failure for the wrong reason:
    /// a guard that reads prose is measuring the documentation, not the workflow.
    /// </summary>
    private static string[] ExemptionIfExpressions(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("#", StringComparison.Ordinal))
            .Where(l => l.StartsWith("if:", StringComparison.Ordinal))
            .Where(l => l.Contains("head.repo.fork", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void BothSitesCarryTheSameExemption()
    {
        var sites = ExemptionIfExpressions(WorkflowText());

        Assert.True(sites.Length >= 2,
            $"expected the fork exemption on BOTH the shared-rules job and the collect-results "
            + $"step in {Workflow}; found {sites.Length}:\n  " + string.Join("\n  ", sites));

        foreach (var clause in ExemptionClauses)
        {
            var missing = sites.Where(s => !s.Contains(clause, StringComparison.Ordinal)).ToArray();
            Assert.True(missing.Length == 0,
                $"every site expressing the shared-rule exemption must carry `{clause}` — the gate "
                + $"and the step that fails on it must never disagree about who is exempt. Missing "
                + $"from:\n  " + string.Join("\n  ", missing));
        }
    }

    [Fact]
    public void NoSiteExemptsDependabot_ThereIsNoQueueToMoveTheGateTo()
    {
        var offenders = ExemptionIfExpressions(WorkflowText())
            .Where(s => s.Contains(RetiredDependabotClause, StringComparison.Ordinal))
            .ToArray();
        Assert.True(offenders.Length == 0,
            "a site exempts Dependabot again. That was sound only while this workflow also ran on "
            + "`merge_group` (policy merge-on-own-green retired the queue): now it SKIPS the gate on "
            + "every Dependabot PR, and a skipped gate reads as a passed one. Provision the credential "
            + "in the Dependabot store instead. Offending:\n  " + string.Join("\n  ", offenders));

        // Negative control: the detector must fire on the retired shape.
        Assert.Contains("if: ${{ github.event.pull_request.head.repo.fork != true && github.actor != 'dependabot[bot]' }}",
            ExemptionIfExpressions("    if: ${{ github.event.pull_request.head.repo.fork != true && github.actor != 'dependabot[bot]' }}\n")
                .Where(x => x.Contains(RetiredDependabotClause, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheExemptionNeverAsksWhetherASecretIsSet()
    {
        var offenders = ExemptionIfExpressions(WorkflowText())
            .Where(s => s.Contains("secrets.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "the shared-rule exemption must be expressed on the EVENT, never on the input. An `if:` "
            + "reading `secrets.*` makes a missing credential skip the job, and GitHub paints a "
            + "skipped job with the same tick as a passed one — so 'the gate never ran' and 'the "
            + "gate passed' become indistinguishable. Offending:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 🚨 The guard must be able to FAIL. A matcher that finds nothing would satisfy both tests
    /// above vacuously — the failure mode this whole file exists to prevent, one level up.
    /// </summary>
    [Fact]
    public void TheMatcherActuallyFindsTheSites()
    {
        var sites = ExemptionIfExpressions(WorkflowText());
        Assert.True(sites.Length >= 2, $"the matcher found {sites.Length} site(s) in {Workflow}");

        // And it must reject a doctored line, or "contains the clause" proves nothing.
        var doctored = "if: ${{ github.event.pull_request.head.repo.fork == false }}";
        Assert.False(doctored.Contains(ExemptionClauses[0], StringComparison.Ordinal),
            "the negative control must genuinely lack the clause the guard requires");
        Assert.Single(ExemptionIfExpressions("    " + doctored + "\n"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (no .github directory found)");
        return dir!.FullName;
    }
}
