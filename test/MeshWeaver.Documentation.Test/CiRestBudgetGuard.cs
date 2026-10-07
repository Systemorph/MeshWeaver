#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Guard for Doc/Architecture/CiRestBudget: the reusable node-repo lanes read the platform's files
/// over GIT, never through the REST contents API.
///
/// <para>Why it is a guard and not a convention: every REST read a lane makes is charged to the
/// CALLER's <c>github.token</c>, whose budget is per repository and small (1,000 requests an hour on
/// the organisation's plan). The lanes' script fetches were the largest consumer of it — measured
/// on MeshWeaver.Plugins at 20–46 counted per CI run (a floor), ~1,000–2,000 an hour — and when it ran out every job of
/// every pull request went red on "API rate limit exceeded for installation" while fetching a file
/// (2026-10-07 11:18–11:25Z). A single re-introduced <c>gh api …/contents/…</c> line is valid YAML,
/// green on a quiet afternoon, and costs one call per job execution in every satellite.</para>
/// </summary>
public class CiRestBudgetGuard
{
    private const string Reader = "\"$RUNNER_TEMP/mw-core-file\"";
    private const string ReaderAction = "Systemorph/MeshWeaver/.github/actions/core-file@";

    /// <summary>A REST read of core's bytes: the contents API, in either the gh or the curl shape.</summary>
    private static readonly Regex RestRead = new(@"repos/Systemorph/MeshWeaver/contents/", RegexOptions.Compiled);

    public static IEnumerable<object[]> Lanes() =>
        Directory.GetFiles(Path.Combine(FindRepoRoot(), ".github", "workflows"), "node-repo-*.yml")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n! });

    [Theory]
    [MemberData(nameof(Lanes))]
    public void NoLane_ReadsThePlatformThroughTheRestContentsApi(string lane)
    {
        var offenders = RestReadsIn(File.ReadAllText(LanePath(lane)));
        Assert.True(offenders.Count == 0,
            $"{lane} reads core through the REST contents API, which spends the CALLER's per-repository token " +
            $"budget once per job execution — use {Reader} \"$REF\" \"<path>\" (Doc/Architecture/CiRestBudget):\n  " +
            string.Join("\n  ", offenders));
    }

    [Theory]
    [MemberData(nameof(Lanes))]
    public void EveryJobThatCallsTheReader_InstallsItFirst(string lane)
    {
        var problems = JobsCallingTheReaderUninstalled(File.ReadAllText(LanePath(lane)));
        Assert.True(problems.Count == 0,
            $"{lane}: a job calls {Reader} without installing it first — on the runner the file does not exist " +
            $"(exit 127). Make `uses: {ReaderAction}main` the job's first step:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>Negative control for the two detectors: the shapes they exist to catch DO fire, so a
    /// green on the real lanes is a measurement and not a detector that cannot see.</summary>
    [Fact]
    public void TheDetectors_FireOnTheShapesTheyExistToCatch()
    {
        const string ghShape = "jobs:\n  a:\n    steps:\n      - run: |\n          gh api \"repos/Systemorph/MeshWeaver/contents/.github/scripts/x.py?ref=${SCRIPTS_REF}\" --jq .content | base64 -d > x.py\n";
        const string curlShape = "jobs:\n  a:\n    steps:\n      - run: |\n          curl -fsSL -o x \\\n            \"${GITHUB_API_URL:-https://api.github.com}/repos/Systemorph/MeshWeaver/contents/.github/scripts/x.sh?ref=${SCRIPTS_REF}\"\n";
        const string commented = "jobs:\n  a:\n    steps:\n      # gh api \"repos/Systemorph/MeshWeaver/contents/x\" was the old shape\n      - run: echo hi\n";
        Assert.Single(RestReadsIn(ghShape));
        Assert.Single(RestReadsIn(curlShape));
        Assert.Empty(RestReadsIn(commented));

        const string uninstalled = "jobs:\n  a:\n    steps:\n      - run: |\n          \"$RUNNER_TEMP/mw-core-file\" \"${SCRIPTS_REF}\" \".github/scripts/x.py\" > x.py\n";
        const string installedLate = "jobs:\n  a:\n    steps:\n      - run: |\n          \"$RUNNER_TEMP/mw-core-file\" main .github/scripts/x.py > x.py\n      - uses: Systemorph/MeshWeaver/.github/actions/core-file@main\n";
        const string installed = "jobs:\n  a:\n    steps:\n      - uses: Systemorph/MeshWeaver/.github/actions/core-file@main\n      - run: |\n          \"$RUNNER_TEMP/mw-core-file\" main .github/scripts/x.py > x.py\n";
        Assert.Single(JobsCallingTheReaderUninstalled(uninstalled));
        Assert.Single(JobsCallingTheReaderUninstalled(installedLate));
        Assert.Empty(JobsCallingTheReaderUninstalled(installed));
    }

    private static List<string> RestReadsIn(string yaml) =>
        yaml.Split('\n').Select((line, i) => (line, i))
            .Where(x => !x.line.TrimStart().StartsWith('#') && RestRead.IsMatch(x.line))
            .Select(x => $"line {x.i + 1}: {x.line.Trim()}").ToList();

    private static List<string> JobsCallingTheReaderUninstalled(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var problems = new List<string>();
        if (!root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobsNode) || jobsNode is not YamlMappingNode jobs)
            return problems;
        foreach (var (name, jobNode) in jobs.Children)
        {
            if (jobNode is not YamlMappingNode job
                || !job.Children.TryGetValue(new YamlScalarNode("steps"), out var stepsNode)
                || stepsNode is not YamlSequenceNode steps)
                continue;
            var installedAt = -1;
            for (var i = 0; i < steps.Children.Count; i++)
            {
                if (steps.Children[i] is not YamlMappingNode step) continue;
                if (Scalar(step, "uses")?.StartsWith(ReaderAction, StringComparison.Ordinal) == true && installedAt < 0)
                    installedAt = i;
                var run = Scalar(step, "run");
                if (run is not null && run.Contains("mw-core-file", StringComparison.Ordinal) && (installedAt < 0 || installedAt > i))
                {
                    problems.Add($"job `{name}` step {i + 1} ({Scalar(step, "name") ?? "unnamed"})");
                    break;
                }
            }
        }
        return problems;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    private static string LanePath(string lane) => Path.Combine(FindRepoRoot(), ".github", "workflows", lane);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
