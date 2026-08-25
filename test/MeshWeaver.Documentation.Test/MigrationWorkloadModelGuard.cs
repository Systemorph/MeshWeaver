using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The database migration is a RUN-ONCE workload, and nothing in this repo may say otherwise</b>
/// (#1788).
///
/// <para><b>What went wrong.</b> The migration container runs the migration, prints
/// <c>Database migration completed. Version: N</c>, and exits 0. Modelled as a <b>Deployment</b>
/// that is a crash loop by construction: the kubelet restarts it, forever. Three production
/// namespaces sat at 50/53/38 restarts (memex-cloud reached 87 in nine hours), and each restart
/// rebuilt <c>public.top_level_index</c> across every partition schema — so the "benign
/// CrashLoopBackOff" the docs told everyone to expect was a CPU storm.</para>
///
/// <para><b>Why it is a correctness problem, not tidiness.</b> A standing crash loop makes "is
/// anything crash-looping in prod?" — the first question anyone asks in an incident — permanently
/// unanswerable: a genuine migration failure looks exactly like the documented noise. That is this
/// repo's central failure mode (silence indistinguishable from success), and the cure is to model
/// the workload correctly so the ambiguity cannot exist.</para>
///
/// <para><b>The chart is already right; the PROSE was what kept the wrong model alive.</b>
/// <c>deploy/helm/templates/memex-migration/job.yaml</c> has rendered a <c>Job</c> since #145 —
/// there is no Deployment template and there never was one to port. What survived was a set of
/// commands, in <c>AGENTS.md</c> and the deploy scripts, telling operators to
/// <c>kubectl set image</c> / <c>rollout restart</c> a <c>memex-migration-deployment</c> the chart
/// does not define. Every one of those either fails or keeps a cluster-only orphan alive. This
/// guard is what stops them coming back — a text guard because the invariant lives across a Helm
/// template, several shell scripts and a markdown file, where no unit test can observe it.</para>
///
/// <para>🚨 What this guard deliberately does NOT assert: that no file MENTIONS
/// <c>memex-migration-deployment</c>. Several must — <c>deploy/aks/SELF-UPDATE.md</c> and
/// <c>Doc/Architecture/DeploymentAKS.md</c> carry the warning that the self-updater still targets
/// that name, and the chart's own RBAC still grants it (removing that grant before the updater
/// stops patching it would turn a harmless call into a 403 mid-roll). Naming the problem is the
/// opposite of repeating it. The assertion is on COMMANDS.</para>
/// </summary>
public class MigrationWorkloadModelGuard
{
    private const string MigrationTemplates = "deploy/helm/templates/memex-migration";
    private const string Job = "deploy/helm/templates/memex-migration/job.yaml";

    /// <summary>
    /// The workload itself: a Job that runs to completion and stops. <c>restartPolicy: Never</c>
    /// is the line that makes "it exited 0" mean finished rather than "restart it".
    /// </summary>
    [Fact]
    public void TheMigration_IsAJobThatRunsToCompletion_NeverADeployment()
    {
        var dir = Path.Combine(FindRepoRoot(), MigrationTemplates);
        var templates = Directory.GetFiles(dir, "*.yaml");

        foreach (var template in templates)
        {
            var body = ExecutableLinesOf(File.ReadAllText(template));
            Assert.False(
                Regex.IsMatch(body, @"^kind:\s*""?Deployment""?", RegexOptions.Multiline),
                $"{Path.GetRelativePath(FindRepoRoot(), template)} declares a Deployment. The "
                + "migration runs once and exits 0, so a Deployment restarts it forever — 310 "
                + "restarts a day pegging a core, and a permanent CrashLoopBackOff that makes a "
                + "REAL migration failure unreadable (#1788).");
        }

        var job = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Job)));

        Assert.True(Regex.IsMatch(job, @"^kind:\s*""?Job""?", RegexOptions.Multiline),
            $"{Job} must declare kind: Job — the workload model is the fix.");

        Assert.True(job.Contains("restartPolicy: \"Never\"", StringComparison.Ordinal),
            $"{Job} must set restartPolicy: Never. Without it the pod is restarted on exit and the "
            + "Job form buys nothing.");

        Assert.True(job.Contains(".Release.Revision", StringComparison.Ordinal),
            $"{Job}'s name must embed .Release.Revision so a new migration actually RUNS on the "
            + "next helm upgrade — a Job with a stable name is created once and then silently "
            + "skipped, which would leave the schema behind while everything looked fine.");

        Assert.True(job.Contains("ttlSecondsAfterFinished", StringComparison.Ordinal),
            $"{Job} must set ttlSecondsAfterFinished so completed Jobs clean themselves up rather "
            + "than accumulating one object per upgrade.");
    }

    /// <summary>
    /// 🚨 And no command anywhere in the repo may roll the migration as a Deployment. Each of
    /// these was live until #1788: <c>AGENTS.md</c> — the file every agent loads first — carried
    /// both a <c>set image</c> and a <c>rollout restart</c> against
    /// <c>deployment/memex-migration-deployment</c>, plus the instruction to treat the resulting
    /// crash loop as benign. <c>deploy/aks/scripts/deploy.sh</c> carried the same two, one of them
    /// unguarded, so a documented deploy always printed an error nobody read.
    ///
    /// <para>The scan is over COMMANDS: a <c>kubectl</c> verb applied to that object name. Prose
    /// that names it in order to warn about it is untouched, and must stay that way — the
    /// self-updater and the chart's RBAC still target it, and the day that changes is the day the
    /// warnings can go.</para>
    /// </summary>
    [Fact]
    public void NoCommandInThisRepo_RollsTheMigrationAsADeployment()
    {
        var root = FindRepoRoot();

        // kubectl <verb> ... deployment/memex-migration-deployment  (or "deployment memex-…")
        var command = new Regex(
            @"kubectl[^\n]*?\bdeployment[/ ]memex-migration-deployment\b",
            RegexOptions.IgnoreCase);

        var offenders = ScannedFiles(root)
            .Where(file => command.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these files still issue a kubectl command against 'memex-migration-deployment', a "
            + "workload the chart does not define (it renders a Job — see "
            + $"{Job}): {string.Join(", ", offenders)}. Such a command either errors, or keeps a "
            + "cluster-only orphan Deployment alive that re-runs the migration forever (#1788).");
    }

    /// <summary>
    /// 🚨 And nothing may teach the reader to EXPECT the crash loop. "Benign `CrashLoopBackOff`" is
    /// how the wrong model survived so many deploys: it told every operator and every agent that a
    /// crash-looping migration pod is the normal state, which is exactly what made a real migration
    /// failure invisible. The pod restarting is not benign and, with the Job model, does not happen.
    ///
    /// <para>🚨 <b>A claim, not a rebuttal.</b> The phrase must stay quotable, because several
    /// files exist specifically to refute it — <c>job.yaml</c>'s own header ("the 'benign
    /// CrashLoopBackOff' the docs assumed was actually a CPU storm") and the verify steps in
    /// <c>DeploymentAKS.md</c> / <c>MemexCloudDeployment.md</c>. So a match is an offence only when
    /// the SENTENCE carrying it does not also negate or historicise it. That is a tripwire on the
    /// wording, deliberately weaker than the command scan above: a determined rewording gets
    /// through, but the operative regression — a command that rolls the migration as a Deployment —
    /// is caught structurally either way.</para>
    /// </summary>
    [Fact]
    public void NoDocument_TeachesThatACrashLoopingMigrationIsNormal()
    {
        var root = FindRepoRoot();

        // "benign"/"harmless"/"expected" within the same sentence as the crash loop.
        var excuse = new Regex(
            @"[^.\n]*?(?:(?:benign|harmless|expected|normal)[^.\n]{0,80}CrashLoopBackOff"
            + @"|CrashLoopBackOff[^.\n]{0,80}(?:is|are)\s+(?:benign|harmless|expected|normal))[^.\n]*",
            RegexOptions.IgnoreCase);

        // A sentence that says the phrase in order to deny it, or to report that it USED to be
        // said, is the opposite of the defect and must keep working.
        string[] rebuttals =
            ["not ", "n't", "never", "no longer", "used to", "assumed", "previously", "was ", "were ",
             "replaced", "legacy", "wrongly", "falsely", "instead of"];

        var offenders = ScannedFiles(root)
            .Select(file => (File: file, Claims: excuse.Matches(File.ReadAllText(file))
                .Select(m => m.Value)
                .Where(sentence => !rebuttals.Any(r =>
                    sentence.Contains(r, StringComparison.OrdinalIgnoreCase)))
                .ToArray()))
            .Where(x => x.Claims.Length > 0)
            .Select(x => $"{Path.GetRelativePath(root, x.File)} ({x.Claims[0].Trim()})")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these files describe a CrashLoopBackOff as benign/expected: "
            + $"{string.Join(" · ", offenders)}. A standing crash loop makes 'is anything "
            + "crash-looping in prod?' permanently unanswerable, so a genuine failure reads as the "
            + "documented noise. Model the workload correctly instead of documenting the symptom "
            + "(#1788).");
    }

    /// <summary>
    /// The files this guard reads: the operator-facing prose and scripts that can put a command in
    /// front of a human. Deliberately NOT the whole tree — <c>bin/</c>, <c>obj/</c> and the test
    /// sources (this file names both strings) would make it match itself.
    /// </summary>
    private static string[] ScannedFiles(string root) =>
    [
        .. new[] { "AGENTS.md", "CLAUDE.md" }
            .Select(name => Path.Combine(root, name))
            .Where(File.Exists),
        .. Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal)
                        || f.EndsWith(".sh", StringComparison.Ordinal)
                        || f.EndsWith(".yaml", StringComparison.Ordinal)
                        // .yml as well as .yaml: deploy/ genuinely carries both (e.g.
                        // deploy/whisper/docker-compose.yml). Scanning only one spelling
                        // would let a reintroduced command hide in the other while this
                        // guard still claimed "no command anywhere in the repo".
                        || f.EndsWith(".yml", StringComparison.Ordinal)),
        .. Directory.EnumerateFiles(
                Path.Combine(root, "src", "MeshWeaver.Documentation", "Data"),
                "*.md", SearchOption.AllDirectories),
        .. Directory.EnumerateFiles(Path.Combine(root, "content"), "*.md", SearchOption.AllDirectories),
    ];

    /// <summary>
    /// Comment lines are stripped before probing the templates, for the same reason
    /// <see cref="DrainDeadlineGuard"/> strips them: <c>job.yaml</c>'s header explains the
    /// Deployment it REPLACED, so an unstripped scan would find "Deployment" in the explanation
    /// and fail on the very comment that documents the fix.
    /// </summary>
    private static string ExecutableLinesOf(string yaml) =>
        string.Join("\n", yaml.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));

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
