using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// <c>shard-assign.sh</c> must EXIT ZERO in every selection mode, <c>none</c> included.
///
/// <para>🚨 Why this guard exists (2026-09-20, MeshWeaver#4979). <c>select_projects</c>'s
/// <c>none</c> branch wrote its message to stderr and returned WITHOUT READING STDIN. That closes
/// the pipe while <c>find test -name '*.csproj'</c> is still writing into it, so <c>find</c> takes
/// SIGPIPE and exits 141 — and <c>set -o pipefail</c> at the top of the script turns that into the
/// whole script failing. <c>none</c> is reached only by a change set no test project reads, i.e. a
/// documentation- or skill-only pull request, so the REQUIRED <c>Build solution (once)</c> job died
/// with a bare <c>Process completed with exit code 1</c> printed four lines AFTER
/// <c>Build succeeded. 0 Warning(s) 0 Error(s)</c> — the one shape that reads as "the build broke"
/// when the build was fine and nothing was owed.</para>
///
/// <para>The rule this pins is not "the none branch drains stdin" (an implementation), but "every
/// mode exits zero, and <c>none</c> selects nothing while <c>all</c> selects something" — the
/// non-vacuity half matters, because a script that exits zero having printed nothing in EVERY mode
/// would satisfy the exit-code half alone.</para>
/// </summary>
public class ShardAssignSelectionGuard
{
    private const string ScriptPath = ".github/scripts/shard-assign.sh";

    [Theory(Timeout = 120000)]
    [InlineData("none", false)]
    [InlineData("all", true)]
    [InlineData("", true)]
    public void EverySelectionModeExitsZero(string selection, bool expectProjects)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var root = SourceScan.FindRepoRoot();
        var script = Path.Combine(root, ScriptPath);
        File.Exists(script).Should().BeTrue($"{ScriptPath} is the subject of this guard");

        var (exitCode, stdout, stderr) = Run(root, script, selection);

        exitCode.Should().Be(0,
            $"shard-assign.sh must exit zero for TEST_SELECTION='{selection}' — 141 is SIGPIPE from a "
            + $"branch that never read its stdin, and pipefail makes it the build job's failure. "
            + $"stderr: {stderr}");

        var projects = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(".csproj", StringComparison.Ordinal))
            .ToArray();

        if (expectProjects)
            projects.Should().NotBeEmpty(
                $"TEST_SELECTION='{selection}' owes the whole suite, so shard 0 must receive projects — "
                + "an empty answer here means the selection or the weighting stopped working, and the "
                + "exit-code assertion above would pass over it");
        else
            projects.Should().BeEmpty("TEST_SELECTION=none owes nothing, so no project may be assigned");
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string root, string script, string selection)
    {
        var psi = new ProcessStartInfo("/bin/bash", $"\"{script}\"")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["SHARD_INDEX"] = "0";
        psi.Environment["SHARD_TOTAL"] = "6";
        if (selection.Length > 0)
            psi.Environment["TEST_SELECTION"] = selection;
        else
            psi.Environment.Remove("TEST_SELECTION");

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
