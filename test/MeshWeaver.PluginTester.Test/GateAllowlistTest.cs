#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The known-debt ratchet, pinned check by check: parsing (including the malformed-line throw —
/// a typo that silently allowed nothing would defeat the ratchet), the classification of every
/// failure into new-vs-known, the STALE rule (a passing check fails the run until its entry is
/// removed — the list only shrinks), and the unverifiable escape (a skipped check or absent
/// scope proves nothing, so it warns and never fails).
/// </summary>
public class GateAllowlistTest
{
    // ── parsing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsEntries_SkipsCommentsAndBlanks()
    {
        var allow = GateAllowlist.Parse(
        [
            "# the debt list",
            "",
            "Claims idempotence   # tracked in #235",
            "Edu/Exercise tests",
        ]);
        Assert.Equal(2, allow.Entries.Count);
        Assert.True(allow.Allows("Claims", "idempotence"));
        Assert.True(allow.Allows("Edu/Exercise", "tests"));
        Assert.False(allow.Allows("Claims", "install"));
    }

    [Fact]
    public void Parse_MatchesCaseInsensitively()
    {
        var allow = GateAllowlist.Parse(["claims IDEMPOTENCE"]);
        Assert.True(allow.Allows("Claims", "idempotence"));
    }

    [Theory]
    [InlineData("Claims")]                      // missing check
    [InlineData("Claims idempotence extra")]    // too many tokens
    [InlineData("Claims flakiness")]            // unknown check name
    public void Parse_ThrowsOnMalformedLine(string line)
    {
        var ex = Assert.Throws<FormatException>(() => GateAllowlist.Parse([line]));
        Assert.Contains("line 1", ex.Message);
    }

    // ── a ratchet that is not there ──────────────────────────────────────────────────────────

    /// <summary>
    /// A MISSING allow file is a configuration error, not an empty ratchet (#1741). Substituting
    /// an empty list would be the STRICTER verdict — with no entries every failure is new — so it
    /// could never turn a red run green; it would instead make the gate's configuration
    /// unverifiable, since <c>known-debt allowlist: 0 entr(ies)</c> would mean either "the ratchet
    /// is empty" or "the gate never found the ratchet you passed". An empty ratchet has two honest
    /// spellings: an empty FILE, or omitting <c>--allow</c>.
    /// </summary>
    [Fact]
    public void Load_ThrowsAnActionableMessage_WhenTheFileIsMissing()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), $"no-such-ratchet-{Guid.NewGuid():N}.allow");

        var ex = Assert.Throws<FileNotFoundException>(() => GateAllowlist.Load(missing));

        // Not the framework's bare "Could not find file": the flag, the resolved path, and how an
        // empty ratchet is actually spelled.
        Assert.Contains("--allow", ex.Message, StringComparison.Ordinal);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        Assert.Contains("empty file", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An EMPTY file is a legitimate ratchet — it is how "no known debt" is recorded,
    /// and it must load as an empty list rather than as an error.</summary>
    [Fact]
    public void Load_ReadsAnEmptyFile_AsAnEmptyRatchet()
    {
        var file = Path.Combine(Path.GetTempPath(), $"empty-ratchet-{Guid.NewGuid():N}.allow");
        File.WriteAllText(file, "");
        try
        {
            Assert.Empty(GateAllowlist.Load(file).Entries);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The diagnostic path resolver must never throw on the very path it is describing —
    /// an empty <c>--allow</c> value would otherwise blow up inside the error message.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_DegradesToTheRawText_WhenThePathCannotBeResolved(string path)
    {
        Assert.Equal(path, GateAllowlist.Describe(path));
        Assert.Contains("--allow", GateAllowlist.MissingFileMessage(path), StringComparison.Ordinal);
    }

    // ── classification ───────────────────────────────────────────────────────────────────────

    private static GateReport Report(params PackageResult[] packages) => new([.. packages]);

    private static PackageResult IdempotenceBroken(string id) =>
        new(id) { IdempotenceError = "re-install wrote 1 node(s)" };

    private static PackageResult WithType(string id, NodeTypeResult type) =>
        new(id) { NodeTypes = [type] };

    private static NodeTypeResult TestsRed(string path) =>
        new(path, path.Split('/')[0]) { Tests = CheckOutcome.Failed, TestsDetail = "1 red row" };

    [Fact]
    public void Evaluate_SplitsFailures_IntoKnownDebtAndNew()
    {
        var report = Report(IdempotenceBroken("Claims"), IdempotenceBroken("Video"));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Claims idempotence"]));

        Assert.Single(verdict.KnownDebt);
        Assert.True(verdict.IsKnownDebt("Claims", "idempotence"));
        Assert.Single(verdict.NewFailures);
        Assert.Equal("Video", verdict.NewFailures[0].Scope);
        Assert.False(verdict.Success);
    }

    [Fact]
    public void Evaluate_AllKnown_IsGreen()
    {
        var report = Report(
            IdempotenceBroken("Claims"),
            WithType("Edu", TestsRed("Edu/Exercise")));
        var verdict = GateVerdict.Evaluate(
            report, GateAllowlist.Parse(["Claims idempotence", "Edu/Exercise tests"]));

        Assert.Empty(verdict.NewFailures);
        Assert.Empty(verdict.Stale);
        Assert.Equal(2, verdict.KnownDebt.Count);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_TypeChecks_MatchOnTheTypePath()
    {
        var report = Report(WithType("Edu", TestsRed("Edu/Exercise")));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Edu tests"]));

        // The package-scoped entry does NOT cover a type-level failure: exact scope only,
        // or the ratchet stops ratcheting.
        Assert.Single(verdict.NewFailures);
        Assert.False(verdict.Success);
    }

    // ── the stale rule ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_IntermittentEntryWhoseCheckPasses_IsNotStale()
    {
        // The ratchet's rule — a listed check that starts passing must be removed — assumes the
        // check is DETERMINISTIC, so one green run proves the debt is paid. For a flapping check
        // that inference is wrong. PensionFund idempotence was dropped as stale and restored one
        // commit later for exactly this reason, after which the file carried the intent in a
        // comment the parser strips, and the ratchet kept failing runs for obeying the file.
        var healthy = new PackageResult("Claims");
        var verdict = GateVerdict.Evaluate(
            Report(healthy), GateAllowlist.Parse(["Claims idempotence intermittent"]));

        Assert.Empty(verdict.Stale);
        Assert.Empty(verdict.NewFailures);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_IntermittentEntry_StillSuppressesItsOwnFailure()
    {
        // Marking an entry intermittent gives up stale-detection for that line and NOTHING else:
        // it still tolerates the failure it names, exactly as before.
        var verdict = GateVerdict.Evaluate(
            Report(IdempotenceBroken("Claims")),
            GateAllowlist.Parse(["Claims idempotence intermittent"]));

        Assert.Empty(verdict.NewFailures);
        Assert.Single(verdict.KnownDebt);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Parse_RejectsAnUnknownThirdToken()
    {
        // A typo in the marker must not silently degrade to a plain entry — that would restore the
        // stale-failure the marker exists to prevent, with nothing pointing at the cause.
        Assert.Throws<FormatException>(
            () => GateAllowlist.Parse(["Claims idempotence intermitent"]));
    }

    [Fact]
    public void Evaluate_EntryWhoseCheckPasses_IsStale_AndFailsTheRun()
    {
        var healthy = new PackageResult("Claims");
        var verdict = GateVerdict.Evaluate(
            Report(healthy), GateAllowlist.Parse(["Claims idempotence"]));

        Assert.Single(verdict.Stale);
        Assert.Empty(verdict.NewFailures);
        Assert.False(verdict.Success);
    }

    [Fact]
    public void Evaluate_TypeEntryWhoseTestsPass_IsStale()
    {
        var green = new NodeTypeResult("Edu/Exercise", "Edu") { Tests = CheckOutcome.Passed };
        var verdict = GateVerdict.Evaluate(
            Report(WithType("Edu", green)), GateAllowlist.Parse(["Edu/Exercise tests"]));

        Assert.Single(verdict.Stale);
        Assert.False(verdict.Success);
    }

    // ── the unverifiable escape ──────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_AbsentScope_IsUnverifiable_NotStale()
    {
        var verdict = GateVerdict.Evaluate(
            Report(new PackageResult("Claims")), GateAllowlist.Parse(["Gone/Type tests"]));

        Assert.Single(verdict.Unverifiable);
        Assert.Empty(verdict.Stale);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_SkippedTests_AreUnverifiable_NotStale()
    {
        var skipped = new NodeTypeResult("Edu/Exercise", "Edu") { Tests = CheckOutcome.Skipped };
        var verdict = GateVerdict.Evaluate(
            Report(WithType("Edu", skipped)), GateAllowlist.Parse(["Edu/Exercise tests"]));

        Assert.Single(verdict.Unverifiable);
        Assert.Empty(verdict.Stale);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_IdempotenceEntry_BehindAFailedInstall_IsUnverifiable()
    {
        // The idempotence pin never ran if the install itself failed — its entry must not be
        // stale (that would demand removing an entry for debt that may well still exist).
        var broken = new PackageResult("Claims") { InstallError = "boom" };
        var verdict = GateVerdict.Evaluate(
            Report(broken), GateAllowlist.Parse(["Claims idempotence"]));

        Assert.Single(verdict.Unverifiable);
        Assert.Empty(verdict.Stale);
        // The install failure itself is unlisted → a new failure → the run still fails.
        Assert.Single(verdict.NewFailures);
        Assert.False(verdict.Success);
    }

    // ── the summary states ONE verdict ───────────────────────────────────────────────────────

    [Fact]
    public void Summary_LabelsKnownDebt_AndStatesTheVerdictLine()
    {
        var report = Report(IdempotenceBroken("Claims"));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Claims idempotence"]));
        var output = new StringWriter();
        report.WriteSummary(output, verdict);
        var text = output.ToString();

        Assert.Contains("[DEBT] Claims", text);
        Assert.Contains("[known-debt]", text);
        Assert.Contains("GREEN — 1 known-debt failure(s) allowed", text);
        Assert.DoesNotContain("GATE FAILED", text);
    }

    [Fact]
    public void Summary_NamesStaleEntries_AndFails()
    {
        var report = Report(new PackageResult("Claims"));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Claims idempotence"]));
        var output = new StringWriter();
        report.WriteSummary(output, verdict);
        var text = output.ToString();

        Assert.Contains("STALE allow entry", text);
        Assert.Contains("Claims idempotence", text);
        Assert.Contains("GATE FAILED", text);
    }
}
