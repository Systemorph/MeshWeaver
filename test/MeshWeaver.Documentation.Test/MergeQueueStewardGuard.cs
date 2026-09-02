using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The merge-queue steward re-queues only on EVIDENCE, and its catalogue cannot rot</b>
/// (Doc/Architecture/MergeQueue).
///
/// <para><b>What this pins.</b> <c>.github/scripts/merge-queue-steward.py</c> is the hand that puts a
/// dequeued pull request back into the merge queue — the job a human had to do on 2026-08-30/31
/// every time a flake ejected an entry. It may do so only when every failed assertion matches an
/// entry in <c>.github/known-flakes.json</c>, and an entry is a <i>temporary, evidence-bearing</i>
/// allowance: a regex over the failure MESSAGE (never the test name), an issue tracking the root
/// cause, run URLs, and an expiry at most 30 days out. A catalogue that could carry a bare
/// <c>.*</c>, an entry with no issue, or an entry from months ago would turn the steward into an
/// automatic re-runner — the one thing AGENTS.md forbids it to be.</para>
///
/// <para><b>Why the expiry reds the build.</b> The steward already treats an expired entry as
/// uncatalogued, so nothing re-queues on it; this guard makes the expiry <i>visible</i> instead of
/// letting the ledger of tolerated defects grow stale entries nobody re-reads. Delete the entry or
/// renew it with fresh evidence — the same choice an allow-file ratchet forces here.</para>
///
/// <para>🚨 <b>This guard carries its own negative control and runs the real classifier.</b> The
/// catalogue validator is proven to REFUSE a malformed entry before its verdict on the committed
/// file means anything, and the Python self-test — every row of the decision table, every negative
/// row rejecting — is executed, not pattern-matched. A gate that cannot fail is not a gate.</para>
/// </summary>
public class MergeQueueStewardGuard
{
    private const string Catalogue = ".github/known-flakes.json";
    private const string Script = ".github/scripts/merge-queue-steward.py";
    private const string Workflow = ".github/workflows/merge-queue-steward.yml";
    private const string BuildAndTest = ".github/workflows/dotnet-test.yml";
    private const int MaxCatalogueDays = 30;
    private const int MaxJobMinutes = 10;

    private static readonly Regex IssueUrl =
        new(@"^https://github\.com/[\w.-]+/[\w.-]+/issues/\d+$", RegexOptions.Compiled);
    private static readonly Regex RunUrl =
        new(@"^https://github\.com/[\w.-]+/[\w.-]+/actions/runs/\d+(/.*)?$", RegexOptions.Compiled);

    [Fact]
    public void EveryCatalogueEntry_IsEvidenceBearing_AndUnexpired()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Catalogue));
        var problems = Validate(text, DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.True(problems.Count == 0,
            $"{Catalogue} carries entries the steward must not re-queue on:\n  - "
            + string.Join("\n  - ", problems)
            + "\nAn entry is a stopgap with a deadline: issue URL, run URLs, an assertion-MESSAGE regex, "
            + "and an expiry at most 30 days after addedOn. Expired ⇒ delete it or renew it with fresh evidence.");
    }

    /// <summary>
    /// The negative control: the validator must refuse each defect it claims to catch. Without this
    /// row, an empty catalogue passes the test above having proven nothing about the validator.
    /// </summary>
    [Fact]
    public void TheValidator_RefusesEachDefectItClaimsToCatch()
    {
        var today = new DateOnly(2026, 9, 2);
        string Entry(string overrides) => """{"entries":[{"id":"x","assertionPattern":"timed out after","testName":"T",""" +
            """"issue":"https://github.com/Systemorph/MeshWeaver/issues/9","expires":"2026-09-30","addedOn":"2026-09-02",""" +
            """"addedBy":"me","evidence":["https://github.com/Systemorph/MeshWeaver/actions/runs/1"]""" + overrides + "}]}";

        Assert.Empty(Validate(Entry(""), today));
        Assert.Empty(Validate("""{"entries":[]}""", today));

        Assert.NotEmpty(Validate(Entry(""","issue":""""), today));
        Assert.NotEmpty(Validate(Entry(""","issue":"https://example.com/1""""), today));
        Assert.NotEmpty(Validate(Entry(""","expires":"2026-11-30""""), today));      // > 30 days out
        Assert.NotEmpty(Validate(Entry(""","expires":"2026-09-01""""), today));      // already expired
        Assert.NotEmpty(Validate(Entry(""","assertionPattern":"(""""), today));      // invalid regex
        Assert.NotEmpty(Validate(Entry(""","assertionPattern":".*""""), today));     // matches everything
        Assert.NotEmpty(Validate(Entry(""","evidence":[]"""), today));
        Assert.NotEmpty(Validate(Entry(""","evidence":["https://github.com/Systemorph/MeshWeaver/pull/1"]"""), today));
        Assert.NotEmpty(Validate("""{"entries":[{"id":"x"}]}""", today));
        Assert.NotEmpty(Validate("not json", today));
    }

    /// <summary>
    /// The decision table is executed, not read: build error ⇒ rejected, catalogued ⇒ re-queued,
    /// uncatalogued ⇒ rejected, expired ⇒ uncatalogued, cap ⇒ rejected, multi-PR group with an own
    /// green run ⇒ bisect. The Python self-test asserts every one of those and fails non-zero if any
    /// row stops holding.
    /// </summary>
    [Fact]
    public void TheClassifierSelfTest_Passes()
    {
        var root = FindRepoRoot();
        var psi = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Script);
        psi.ArgumentList.Add("--self-test");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "python3 could not be started. It is required by this guard AND by the steward workflow, "
                + "which runs the same self-test before every real decision.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0 && stdout.Contains("self-test PASSED"),
            $"{Script} --self-test failed (exit {process.ExitCode}). A classifier whose own table no longer "
            + $"holds must not be handed a pull request.\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
    }

    [Fact]
    public void TheSteward_FiresOnDequeuedOnly_IsCappedAtTenMinutes_AndNeverRerunsAWorkflow()
    {
        var root = FindRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, Workflow));
        var script = File.ReadAllText(Path.Combine(root, Script));

        Assert.Matches(new Regex(@"^\s*pull_request:\s*\n\s*types:\s*\[\s*dequeued\s*\]", RegexOptions.Multiline), workflow);

        var caps = Regex.Matches(workflow, @"^\s*timeout-minutes:\s*(\d+)\s*$", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.True(caps.Count > 0, $"{Workflow} declares no timeout-minutes; the steward is a ten-minute job.");
        Assert.All(caps, c => Assert.True(c <= MaxJobMinutes,
            $"{Workflow} caps a job at {c} minutes; the steward reads a run and re-queues — anything past {MaxJobMinutes} is stuck."));

        // The steward re-QUEUES (a new tree against a moved main). It never re-runs the tree that
        // failed — that hides the bug the failing run found. Neither file may reach for the command.
        var rerun = new Regex(@"gh\s+run\s+rerun");
        Assert.DoesNotMatch(rerun, workflow);
        Assert.DoesNotMatch(rerun, script);

        // The self-test is also part of every pull request's CI, in the job that proves CI's own
        // tooling — so a classifier regression cannot merge on a branch that never dequeued anything.
        var buildAndTest = File.ReadAllText(Path.Combine(root, BuildAndTest));
        Assert.Contains("merge-queue-steward.py --self-test", buildAndTest);
    }

    private static List<string> Validate(string json, DateOnly today)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            problems.Add($"not valid JSON: {e.Message}");
            return problems;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                problems.Add("no 'entries' array");
                return problems;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var e in entries.EnumerateArray())
            {
                var where = $"entries[{index++}]";
                string? Str(string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var id = Str("id");
                if (string.IsNullOrWhiteSpace(id)) problems.Add($"{where}: missing id");
                else if (!ids.Add(id)) problems.Add($"{where}: duplicate id '{id}'");
                where = $"{where} ({id ?? "?"})";

                var pattern = Str("assertionPattern");
                if (string.IsNullOrEmpty(pattern)) problems.Add($"{where}: missing assertionPattern");
                else
                {
                    try
                    {
                        var rx = new Regex(pattern);
                        if (rx.IsMatch(string.Empty)) problems.Add($"{where}: assertionPattern matches the empty string — it would match every failure");
                    }
                    catch (ArgumentException ex)
                    {
                        problems.Add($"{where}: assertionPattern does not compile: {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(Str("testName"))) problems.Add($"{where}: missing testName");
                if (string.IsNullOrWhiteSpace(Str("addedBy"))) problems.Add($"{where}: missing addedBy");

                var issue = Str("issue");
                if (string.IsNullOrEmpty(issue) || !IssueUrl.IsMatch(issue))
                    problems.Add($"{where}: issue must be a GitHub issue URL, got '{issue}'");

                var addedOk = DateOnly.TryParseExact(Str("addedOn") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var addedOn);
                var expiresOk = DateOnly.TryParseExact(Str("expires") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires);
                if (!addedOk) problems.Add($"{where}: addedOn must be an ISO date");
                if (!expiresOk) problems.Add($"{where}: expires must be an ISO date");
                if (addedOk && expiresOk)
                {
                    if (expires < addedOn) problems.Add($"{where}: expires {expires:yyyy-MM-dd} is before addedOn {addedOn:yyyy-MM-dd}");
                    if (expires.DayNumber - addedOn.DayNumber > MaxCatalogueDays)
                        problems.Add($"{where}: expires {expires:yyyy-MM-dd} is more than {MaxCatalogueDays} days after addedOn {addedOn:yyyy-MM-dd}");
                    if (expires < today)
                        problems.Add($"{where}: EXPIRED on {expires:yyyy-MM-dd} — delete it, or renew it with fresh evidence and a new expiry");
                }

                if (!e.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() == 0)
                    problems.Add($"{where}: evidence must list at least one workflow-run URL");
                else
                    foreach (var url in evidence.EnumerateArray())
                        if (url.ValueKind != JsonValueKind.String || !RunUrl.IsMatch(url.GetString() ?? ""))
                            problems.Add($"{where}: evidence '{url}' is not a workflow-run URL");
            }
        }

        return problems;
    }

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
