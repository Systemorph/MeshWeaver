using System;
using System.IO;
using System.Linq;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The test job's <c>Evidence: kernel log of a failed shard</c> step prints the runner's kernel log
/// when a test host failed, so a dead <c>createdump</c> is NAMED (an OOM kill, a segfault) instead of
/// being reported only as "no file". Its trigger is a grep over <c>test/test-results.log</c>. If that
/// grep ever drifted from the marker the run step writes, the step would say "every test host exited
/// 0" on a failed shard and the evidence would be silently lost (review of core #8360). So the trigger
/// is pinned to the verdict step's OWN grep (the one that reds the shard) and to the marker the run
/// step writes for every host, crashes and signals included.
/// </summary>
public class KernelLogEvidenceStepGuard
{
    private const string Workflow = ".github/workflows/dotnet-test.yml";
    private const string Evidence = "- name: \"Evidence: kernel log of a failed shard (createdump / OOM / segfault)\"";
    private const string Verdict = "- name: Fail on non-zero project exit (this shard)";
    private const string RunTests = "- name: Run Tests";
    private const string Marker = "exit=[1-9]";
    private const string Log = "test/test-results.log";

    /// <summary>The evidence step fires on exactly the marker the shard's verdict step fails on, over the same file.</summary>
    [Fact]
    public void TheEvidenceStepFiresOnTheVerdictStepsOwnMarker()
    {
        var body = File.ReadAllText(Path.Combine(RepoPaths.Root, Workflow));
        var verdict = Step(body, Verdict);
        var evidence = Step(body, Evidence);

        verdict.Should().Contain($"grep -E '{Marker}' {Log}", "the verdict step gates the shard on this marker");
        evidence.Should().Contain($"grep -qE '{Marker}' {Log}",
            "the evidence step must fire on the verdict step's own marker, or a failed shard can read as all-green to it");
        evidence.Should().Contain("if: always()", "a failed run step must not skip the evidence");
        evidence.Should().Contain("exit 0", "evidence never fails the shard");
        body.IndexOf(Evidence, StringComparison.Ordinal).Should().BeLessThan(body.IndexOf(Verdict, StringComparison.Ordinal),
            "the evidence runs before the verdict, so it is printed even though the verdict ends the job");
    }

    /// <summary>The run step writes that marker into that file for every host, a timeout and a signal death included.</summary>
    [Fact]
    public void TheRunStepWritesTheMarkerTheEvidenceStepReads()
    {
        var run = Step(File.ReadAllText(Path.Combine(RepoPaths.Root, Workflow)), RunTests);

        run.Should().Contain($"\"$marker elapsed=${{elapsed}}s\" >> {Log}", "every host's classified marker lands in the file");
        run.Should().Contain("marker=\"[CI] $label exit=$rc TIMEOUT", "a wall-clock kill is an exit= marker");
        run.Should().Contain("marker=\"[CI] $label exit=$rc SIGNAL SIGSEGV", "a signal death is an exit= marker");
    }

    /// <summary>One step's text: from its <c>- name:</c> line to the next step of the same job.</summary>
    private static string Step(string workflow, string nameLine)
    {
        var start = workflow.IndexOf("    " + nameLine, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the step '{0}' must exist in {1}", nameLine, Workflow);
        var lines = workflow[start..].Split('\n');
        var step = lines.Skip(1).TakeWhile(l => !l.StartsWith("    - ", StringComparison.Ordinal)
                                                && !(l.Length > 0 && !l.StartsWith("    ", StringComparison.Ordinal)));
        return string.Join('\n', new[] { lines[0] }.Concat(step));
    }
}
