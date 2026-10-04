using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 No job downstream of the staged pipeline's <c>stage-gate</c> may rely on GitHub's IMPLICIT
/// <c>success()</c> (Doc/Architecture/StagedPullRequestPipeline).
///
/// <para><b>What went wrong.</b> #6070 put <c>stage-gate</c> in front of <c>build</c>, and the gate
/// needs the stage-0 controls — several of which skip BY DESIGN on a push, in the merge queue and on a
/// fork. GitHub's implicit status check reads skipped ancestors TRANSITIVELY: on main run 37197998805
/// (199912a, 2026-10-04) <c>stage-gate</c> and <c>Build solution (once)</c> both succeeded, and all six
/// test shards, the doc gate and platform-compat were SKIPPED. <c>Consolidate test results</c> found no
/// test evidence and went red, CD produced no new seal, and every Plugins pull request stayed red
/// behind it (fixed by #6086). Nothing in the workflow looked wrong: each job's <c>needs</c> was green.</para>
///
/// <para><b>The rule.</b> Every job that transitively needs <c>stage-gate</c> carries an explicit
/// status function in its job-level <c>if:</c> — <c>!cancelled()</c>, <c>always()</c>,
/// <c>success()</c>, <c>failure()</c> or <c>cancelled()</c> — so it decides on its DIRECT needs'
/// results, never on what a skipped grandparent did. And the suites (<c>test</c>, <c>doc-gate</c>,
/// <c>platform-compat</c>) state <c>needs.build.result == 'success'</c> as their precondition.</para>
/// </summary>
public class StagedPipelineNeverSkipsTheSuitesOnTrunkGuard
{
    private static readonly Regex StatusFunction = new(@"!\s*cancelled\(\)|\balways\(\)|\bsuccess\(\)|\bfailure\(\)|\bcancelled\(\)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EveryJobDownstreamOfTheStageGate_StatesItsOwnStatusFunction()
    {
        var jobs = Jobs();
        Assert.True(jobs.ContainsKey("stage-gate"), "dotnet-test.yml has no `stage-gate` job — this guard's subject moved");

        var downstream = Downstream(jobs, "stage-gate");
        Assert.Contains("build", downstream);
        Assert.Contains("test", downstream);

        var offenders = downstream
            .Where(j => !StatusFunction.IsMatch(jobs[j].If))
            .Select(j => $"{j}: if: {(jobs[j].If.Length == 0 ? "(none — the implicit success())" : jobs[j].If)}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(offenders.Count == 0,
            "These jobs sit downstream of `stage-gate` and rely on GitHub's implicit success(), which reads the "
            + "stage-0 controls that SKIP on push / merge_group / forks transitively and skips the job on trunk even "
            + "when its own needs are green (main run 37197998805: build green, every shard skipped). Give each an "
            + "explicit `!cancelled() && needs.<direct need>.result == 'success'`:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("doc-gate")]
    [InlineData("platform-compat")]
    public void TheSuites_RequireAGreenBuild_Explicitly(string job)
    {
        var jobs = Jobs();
        Assert.True(jobs.TryGetValue(job, out var j), $"dotnet-test.yml has no `{job}` job");
        Assert.Contains("needs.build.result == 'success'", j!.If, StringComparison.Ordinal);
        Assert.Matches(StatusFunction, j.If);
    }

    /// <summary>The guard can fail: the pre-#6086 shape (no `if:` on `test`) is named.</summary>
    [Fact]
    public void TheGuard_NamesTheImplicitGuardShape()
    {
        var jobs = Jobs();
        var broken = new Dictionary<string, Job>(jobs) { ["test"] = jobs["test"] with { If = "" } };
        var offenders = Downstream(broken, "stage-gate").Where(j => !StatusFunction.IsMatch(broken[j].If)).ToList();
        Assert.Contains("test", offenders);
    }

    private sealed record Job(IReadOnlyList<string> Needs, string If);

    private static IReadOnlyDictionary<string, Job> Jobs()
    {
        var path = Path.Combine(FindRepoRoot(), ".github", "workflows", "dotnet-test.yml");
        var stream = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(path)))
            stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var result = new Dictionary<string, Job>(StringComparer.Ordinal);
        foreach (var (key, value) in jobs.Children)
        {
            var body = (YamlMappingNode)value;
            var needs = body.Children.TryGetValue(new YamlScalarNode("needs"), out var n)
                ? n switch
                {
                    YamlSequenceNode seq => seq.Children.Select(c => ((YamlScalarNode)c).Value ?? "").ToList(),
                    YamlScalarNode s => new List<string> { s.Value ?? "" },
                    _ => new List<string>(),
                }
                : new List<string>();
            var cond = body.Children.TryGetValue(new YamlScalarNode("if"), out var i) ? ((YamlScalarNode)i).Value ?? "" : "";
            result[((YamlScalarNode)key).Value ?? ""] = new Job(needs, cond);
        }
        return result;
    }

    private static List<string> Downstream(IReadOnlyDictionary<string, Job> jobs, string root)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>(new[] { root });
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var (name, job) in jobs)
                if (job.Needs.Contains(current) && found.Add(name))
                    frontier.Enqueue(name);
        }
        return found.ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
