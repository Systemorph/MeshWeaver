using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A test host that DIED must never be summarised as a pass</b> (#2495).
///
/// <para><b>The defect this pins.</b> Pass/fail evidence and liveness evidence used to live in two
/// separate channels: the <c>.trx</c> (what the host managed to stream) and the
/// <c>[CI] &lt;name&gt; exit=&lt;n&gt;</c> marker (whether the host survived). Every reporter in
/// <c>dotnet-test.yml</c> reads only the first — the shard's failure summary, the per-shard GitHub
/// check, the consolidated check, and the collector's summary. So when
/// <c>MeshWeaver.Content.Test</c> took an <c>exit=139</c> after streaming three green results,
/// all four announced a pass over a crashed process. Only the last step in the job, which reports
/// a number rather than a test name, disagreed.</para>
///
/// <para><b>Why a seventh checker would not have been the fix.</b> Adding one more place that also
/// consults the exit marker leaves the next reporter — added by someone who never read this — with
/// the identical blind spot. The fix makes the evidence single-channel: the crash is written INTO
/// the trx as a <c>&lt;project&gt;.HOST_CRASHED</c> failure, so a pass over a dead host becomes
/// unstateable by anything that parses the file.</para>
///
/// <para>🚨 <b>This guard is the negative control, and it runs the real script.</b> A diagnostic
/// change that cannot itself fail is worth nothing — which is the whole point of the issue it
/// closes — so this does not pattern-match the workflow and call it proven. It executes
/// <c>.github/scripts/record-host-crash.py</c> against both shapes a dead host actually leaves
/// behind (a trx full of green results, and no trx at all) and asserts with the shard gate's OWN
/// grep that the outcome is red and names the crash.</para>
/// </summary>
public class CrashedHostIsNeverAPassGuard
{
    private const string Workflow = ".github/workflows/dotnet-test.yml";
    private const string Recorder = ".github/scripts/record-host-crash.py";
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>
    /// The exact grep the shard's "Summarize test failures" step and the collector's
    /// "Summarize test failures (all shards)" step both run. Reproduced rather than approximated:
    /// this guard's claim is about what THOSE steps will do, so it must ask the question the way
    /// they ask it.
    /// </summary>
    private static readonly Regex ShardGate =
        new(@"<UnitTestResult[^>]*outcome=""Failed""[^>]*>", RegexOptions.Compiled);

    /// <summary>
    /// The shape that shipped the bug: a host that streamed green results and then died. The trx
    /// is genuine and complete as far as it goes — which is exactly why every reporter believed it.
    /// </summary>
    [Fact]
    public void AHostThatCrashedAfterStreamingGreenResults_IsRed_AndNamesTheCrash()
    {
        var dir = NewTempDir();
        try
        {
            var trx = Path.Combine(dir, "MeshWeaver.Content.Test.trx");
            File.WriteAllText(trx, ThreeGreenResults());

            // Before: this is precisely what the gate saw for Content.Test's exit=139.
            Assert.Empty(ShardGate.Matches(File.ReadAllText(trx)));

            var (exitCode, stdout, stderr) = RunRecorder(trx, "MeshWeaver.Content.Test", 139,
                "SIGNAL SIGSEGV: the host died on a signal, not an assertion.");

            Assert.True(exitCode == 0, $"the recorder failed: {stdout}\n{stderr}");

            var body = File.ReadAllText(trx);
            var failures = ShardGate.Matches(body);

            Assert.True(failures.Count == 1,
                "The crash did not become a failed result, so the shard summary, the per-shard "
                + "check and the consolidated check would all still report '3 passed' over a "
                + "process that died. That is the defect #2495 exists to remove.");

            Assert.Contains("MeshWeaver.Content.Test.HOST_CRASHED", failures[0].Value);
            Assert.Contains("exited 139", body);

            var doc = XDocument.Parse(body);
            var counters = doc.Descendants(Trx + "Counters").Single();

            // The three real results are KEPT. A crash does not make the tests that genuinely
            // passed disappear — it makes the RUN a non-verdict, which is a different statement
            // and the one the extra result carries.
            Assert.Equal("3", counters.Attribute("passed")!.Value);
            Assert.Equal("1", counters.Attribute("failed")!.Value);
            Assert.Equal("4", counters.Attribute("total")!.Value);
            Assert.Equal("Failed", doc.Descendants(Trx + "ResultSummary").Single().Attribute("outcome")!.Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The other shape, and the one with no evidence at all: a host killed at the 8-minute
    /// wall-clock cap writes NO trx. The gate then has nothing to parse — and "nothing to parse"
    /// is silence, which reads exactly like "nothing went wrong".
    /// </summary>
    [Fact]
    public void AHostKilledBeforeItWroteAnyTrx_StillProducesRedEvidence()
    {
        var dir = NewTempDir();
        try
        {
            var trx = Path.Combine(dir, "MeshWeaver.Hosting.Orleans.Test.trx");
            Assert.False(File.Exists(trx));

            var (exitCode, stdout, stderr) = RunRecorder(trx, "MeshWeaver.Hosting.Orleans.Test", 137,
                "TIMEOUT: killed at the 8m wall-clock cap.");

            Assert.True(exitCode == 0, $"the recorder failed: {stdout}\n{stderr}");
            Assert.True(File.Exists(trx),
                "A killed host leaves no trx, so if the recorder does not CREATE one there is "
                + "nothing for any reporter to be red about.");

            var body = File.ReadAllText(trx);
            Assert.Single(ShardGate.Matches(body));
            Assert.Contains("MeshWeaver.Hosting.Orleans.Test.HOST_CRASHED", body);
            Assert.Contains("exited 137", body);

            // Well-formed enough for the GitHub reporters, which parse the whole document rather
            // than grepping it: a malformed trx would make the per-shard check silently report
            // nothing, reintroducing the same silence one level along.
            var doc = XDocument.Parse(body);
            Assert.Single(doc.Descendants(Trx + "TestDefinitions").Single().Elements(Trx + "UnitTest"));
            Assert.Single(doc.Descendants(Trx + "TestEntries").Single().Elements(Trx + "TestEntry"));
            Assert.Equal("Failed", doc.Descendants(Trx + "ResultSummary").Single().Attribute("outcome")!.Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A truncated trx — the host died mid-write — must not be treated as "no crash to report"
    /// by an exception that escapes. It is itself crash evidence, and the recorder must say so.
    /// </summary>
    [Fact]
    public void AnUnparseableTrx_IsTreatedAsEvidenceOfTheCrash_NotAsAnError()
    {
        var dir = NewTempDir();
        try
        {
            var trx = Path.Combine(dir, "MeshWeaver.FutuRe.Test.trx");
            File.WriteAllText(trx, ThreeGreenResults()[..400]);   // cut mid-element

            var (exitCode, stdout, stderr) = RunRecorder(trx, "MeshWeaver.FutuRe.Test", 139,
                "SIGNAL SIGSEGV.");

            Assert.True(exitCode == 0, $"the recorder failed: {stdout}\n{stderr}");

            var body = File.ReadAllText(trx);
            Assert.Single(ShardGate.Matches(body));
            Assert.Contains("died mid-write", body);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 The wiring half. Every exit code the classifier can name as a death must ALSO set
    /// <c>crash=</c>, which is the single switch that decides whether the crash reaches the trx.
    /// Without this, someone adding <c>136) marker=…</c> tomorrow gets a marker and no trx entry —
    /// i.e. exactly the pre-#2495 behaviour, reintroduced for one exit code, silently.
    /// </summary>
    [Fact]
    public void EveryDeathBranchOfTheClassifier_AlsoRecordsTheCrashIntoTheTrx()
    {
        var body = File.ReadAllText(Path.Combine(SourceScan.FindRepoRoot(), Workflow));

        var caseStart = body.IndexOf("case \"$rc\" in", StringComparison.Ordinal);
        Assert.True(caseStart > 0,
            $"{Workflow} no longer classifies the host exit code with `case \"$rc\" in`. This guard "
            + "can no longer see the classifier — re-point it deliberately rather than letting it rot.");
        var caseEnd = body.IndexOf("esac", caseStart, StringComparison.Ordinal);
        var classifier = body[caseStart..caseEnd];

        // Each branch runs from its `<pattern>)` to the next one.
        var branches = Regex.Matches(classifier, @"^\s*(?<pattern>[0-9|*]+)\)", RegexOptions.Multiline)
            .Select(m => (Pattern: m.Groups["pattern"].Value, Start: m.Index))
            .ToList();

        Assert.True(branches.Count >= 6,
            $"expected the classifier to still enumerate the signal/timeout exit codes; found "
            + $"{branches.Count} branch(es): {string.Join(", ", branches.Select(b => b.Pattern))}");

        var missing = new List<string>();
        for (var i = 0; i < branches.Count; i++)
        {
            var (pattern, start) = branches[i];
            var end = i + 1 < branches.Count ? branches[i + 1].Start : classifier.Length;
            var branch = classifier[start..end];

            // `0)` is a healthy host. `*)` contains BOTH the ordinary-test-failure sub-branch
            // (which must NOT be recorded as a crash — the host completed) and the two MASKED
            // sub-branches (which must). Requiring `crash=` to appear at all in `*)` is the
            // strongest claim that holds for a branch with mixed outcomes; the MASKED text is
            // asserted separately below.
            if (pattern == "0")
                continue;

            if (!branch.Contains("crash=\"", StringComparison.Ordinal))
                missing.Add(pattern);
        }

        Assert.True(missing.Count == 0,
            "These classifier branches name a dead test host but never set `crash=`, so the death "
            + "would be recorded ONLY in the exit marker — and every trx reader (the shard summary, "
            + "the per-shard check, the consolidated check) would keep reporting whatever the host "
            + "streamed, over a process that died: " + string.Join(", ", missing));

        // Both MASKED sub-branches, individually — they are the ones that carry a trx and are
        // therefore the ones a reporter can most convincingly misreport.
        var masked = Regex.Matches(classifier, @"MASKED").Count;
        Assert.True(masked >= 2, $"expected both MASKED sub-branches to survive; found {masked}");

        // 🚨 No skip-trapdoor on ANY recorder call — every one of them, not just the main branch.
        // A gate that swallows its own input's failure is this repo's standing lesson (AGENTS.md →
        // "A gate NEVER tests its own inputs"); here the equivalent is discarding the recorder's
        // exit code so the trx quietly keeps saying "passed". Two of the three call sites shipped
        // in the first revision of this change doing exactly that, which is why the assertion
        // counts invocations rather than finding one.
        var invocations = Regex.Matches(body,
            @"(?<guarded>if ! )?python3 \.github/scripts/record-host-crash\.py[^\n]*\n(?<body>(?:.*\n)*?)\s*fi\n");

        Assert.True(invocations.Count >= 3,
            $"{Workflow} should invoke {Recorder} on every path that produces no verdict — the "
            + $"classifier's death branches, MISSING_BIN and NO_CLASSES. Found {invocations.Count}.");

        foreach (Match invocation in invocations)
        {
            Assert.True(invocation.Groups["guarded"].Success,
                "A recorder call whose exit code is discarded is the same silence with an extra "
                + "step: the trx keeps reporting whatever the host streamed. Wrap it in "
                + "`if ! python3 …; then … fi`.");
            Assert.Contains("::error::", invocation.Groups["body"].Value);
            Assert.Contains("CRASH_RECORD_FAILED", invocation.Groups["body"].Value);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-2495-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunRecorder(
        string trxPath, string label, int exitCode, string classification)
    {
        var script = Path.Combine(SourceScan.FindRepoRoot(), Recorder);
        Assert.True(File.Exists(script), $"{Recorder} is missing — the crash evidence has no writer.");

        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { script, trxPath, label, exitCode.ToString(), classification })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "python3 could not be started. It is required by this guard AND by the CI job that "
                + "runs the recorder (and by .github/scripts/check-licenses.py), so this is a real "
                + "missing prerequisite, not a reason to skip the control.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// A minimal but faithful xUnit-v3-native trx: three passed results, the counters that go with
    /// them, and <c>ResultSummary outcome="Completed"</c> — i.e. the file that said "3 passed" over
    /// <c>MeshWeaver.Content.Test</c>'s dead process.
    /// </summary>
    private static string ThreeGreenResults()
    {
        var results = string.Concat(Enumerable.Range(1, 3).Select(i =>
            $"""
                 <UnitTestResult testName="MeshWeaver.Content.Test.SomeTest.Case{i}" outcome="Passed" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" testListId="8c84fa94-04c1-424b-9868-57a2d4851a1d" testId="00000000-0000-0000-0000-00000000000{i}" executionId="00000000-0000-0000-0000-00000000000{i}" computerName="unknown" duration="00:00:00.1000000" startTime="2026-08-27T00:00:0{i}.0000000+00:00" endTime="2026-08-27T00:00:0{i}.1000000+00:00" />

             """));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="11111111-1111-1111-1111-111111111111" name="proof" runUser="ci" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times creation="2026-08-27T00:00:00.0000000+00:00" queuing="2026-08-27T00:00:00.0000000+00:00" start="2026-08-27T00:00:00.0000000+00:00" finish="2026-08-27T00:00:03.0000000+00:00" />
              <TestSettings name="default" id="22222222-2222-2222-2222-222222222222" />
              <Results>
            {results}  </Results>
              <TestDefinitions />
              <TestEntries />
              <TestLists>
                <TestList name="Results Not in a List" id="8c84fa94-04c1-424b-9868-57a2d4851a1d" />
              </TestLists>
              <ResultSummary outcome="Completed">
                <Counters total="3" executed="3" passed="3" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
              </ResultSummary>
            </TestRun>
            """;
    }
}
