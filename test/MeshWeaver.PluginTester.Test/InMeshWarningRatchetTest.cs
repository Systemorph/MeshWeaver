#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using MeshWeaver.Compiler;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>THE GATE MUST BE ABLE TO FAIL.</b> In-mesh C# is the only C# in the fleet that no
/// <c>-warnaserror</c> build ever sees: <c>NodeSetCompiler</c> collected the emit's warnings and
/// dropped the list on the floor, so a bake reported every NodeType green over source that would
/// not have built in <c>src/</c>. The maintainer's reading of the consequence was exact — *"does
/// NOT surface warnings as errors ==&gt; we have lots of missing xml comments"*.
///
/// <para>These cases are the CONTROL for the fix, run end-to-end through the real bake
/// (<see cref="TreeBake.Run"/>) rather than against the ratchet's arithmetic alone:</para>
/// <list type="number">
///   <item>an EMPTY baseline over source carrying a deliberate <c>CS0219</c> goes RED, and the
///     ratchet names the exact line to add;</item>
///   <item>the SAME source with that pair baselined goes GREEN — so the red above is the ratchet
///     and not something incidental about the fixture;</item>
///   <item>a baseline entry whose type compiles CLEAN goes RED as STALE — the shrink-only half,
///     which is what stops the debt from being carried after it is paid;</item>
///   <item>doc COMPLETENESS is centrally suppressed (<see cref="CompileWarning.NotReported"/>), so
///     the fixture's undocumented record measures nothing and can red nothing — and a baseline
///     line naming a suppressed code is INERT rather than stale, which is what stops retiring a
///     code from redding a repo that has not trimmed its file yet.</item>
/// </list>
///
/// <para>🚨 <b>The negative control is <c>Ctrl/Clean</c></b>, and it is what makes the rest
/// non-vacuous: a fully documented, warning-free NodeType in the SAME bake must measure ZERO
/// warnings. Without it, "the ratchet fired" could equally mean "every type produces warnings
/// here", and case 3 could never distinguish a working stale-check from a fixture that cannot
/// produce a clean type at all.</para>
/// </summary>
public class InMeshWarningRatchetTest(ITestOutputHelper output)
{
    private const string PackageIndexJson =
        """{"$type":"MeshNode","id":"Ctrl","namespace":"","path":"Ctrl","mainNode":"Ctrl","name":"Ctrl","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Warning ratchet control."}}""";

    private static string NodeTypeJson(string name) =>
        $$$"""{"$type":"MeshNode","id":"{{{name}}}","namespace":"Ctrl","path":"Ctrl/{{{name}}}","mainNode":"Ctrl/{{{name}}}","name":"{{{name}}}","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"{{{name}}}","configuration":"config => config.WithContentType<{{{name}}}>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    /// <summary>The NEGATIVE CONTROL: fully documented, no unused locals, nothing to report.</summary>
    private const string CleanSource =
        """
        /// <summary>A fully documented record — the negative control.</summary>
        public record Clean
        {
            /// <summary>The name.</summary>
            public string Name { get; init; } = string.Empty;
        }
        """;

    /// <summary>One deliberate diagnostic per ratchet, and nothing else.</summary>
    private const string DebtSource =
        """
        /// <summary>A record carrying one deliberate defect per ratchet.</summary>
        public record Debt
        {
            /// <summary>The name.</summary>
            public string Name { get; init; } = string.Empty;

            /// <summary>Produces a deliberate CS0219 — an assigned-but-never-used local.</summary>
            /// <returns>One.</returns>
            public int Go()
            {
                int neverUsed = 42;
                return 1;
            }

            /// <summary>
            /// The SAME CS0219 message at a DIFFERENT line — one local, one name, two methods.
            /// EmitPipeline.Collect dedupes on (id, message, LINE), so this is a second occurrence
            /// that folds to ONE site, which is precisely the shape that makes a per-code count
            /// derived from site×type disagree with the raw total.
            /// </summary>
            /// <returns>Two.</returns>
            public int GoAgain()
            {
                int neverUsed = 43;
                return 2;
            }

            /// <summary>
            /// A deliberate CS1574 — a doc comment that EXISTS and is WRONG. It is the second
            /// distinct code the per-code table needs, and it is deliberately a DOC diagnostic:
            /// doc COMPLETENESS is centrally suppressed, a broken <c>cref</c> is not, and the
            /// fixture has to be able to tell those two apart.
            /// </summary>
            /// <returns>See <see cref="NoSuchMember"/>.</returns>
            public int Broken() => 3;
        }

        public record Undocumented
        {
            public string Value { get; init; } = string.Empty;
        }
        """;

    private const string DebtPath = "Ctrl/Debt";
    private const string CleanPath = "Ctrl/Clean";

    [Fact(Timeout = 300_000)]
    public void AnEmptyBaseline_RedsTheBake_AndEachRatchetNamesItsOwn()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse([]));

        // The fixture actually produced what the experiment needs — otherwise everything below is
        // a statement about nothing.
        Assert.Contains(bake.Report.Warnings.Sites, s => s.Code == "CS0219");

        Assert.False(bake.Report.WarningsAccepted);
        Assert.Equal(1, bake.Report.ExitCode);

        var real = Ratchet(bake.Report, WarningClass.Real);
        Assert.Equal([(DebtPath, "CS0219"), (DebtPath, "CS1574")], real.New);
        // The log tells the reader the exact line to add — a verdict nobody can act on is not one.
        Assert.Contains($"warnings NEW {DebtPath} CS0219", bake.Log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 DOC COMPLETENESS IS CENTRALLY SUPPRESSED, and this is the control for it. The fixture's
    /// <c>Undocumented</c> record is public with a public property and no doc comment anywhere, so
    /// it produced <c>CS1591</c> — twice — on every bake before
    /// <see cref="CompileWarning.NotReported"/>. The in-mesh compile now carries the same
    /// <c>NoWarn</c> core carries for <c>src/</c> (<c>CS1591;CS1573;CS1712</c>), so a missing doc
    /// comment is measured by nobody and can red nothing.
    ///
    /// <para>This is NOT a warning being ignored: the doc comments that EXIST and are WRONG —
    /// <c>CS1574</c>/<c>CS1584</c>/<c>CS0419</c> (a <c>cref</c> resolving to nothing, or to two
    /// things), <c>CS1570</c> (malformed XML), <c>CS1571</c>/<c>CS1572</c>/<c>CS1734</c> (a tag
    /// naming a parameter that is not there) — stay in the <c>warnings</c> ratchet, where the
    /// tolerated set is now empty.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void DocCompletenessIsCentrallySuppressed_SoAMissingDocCommentMeasuresNothing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse([$"{DebtPath} CS0219", $"{DebtPath} CS1574"]));

        // The undocumented record compiled — "no CS1591" must not be "it never ran".
        Assert.Contains(DebtPath, bake.Report.Warnings.CompiledTypes);
        Assert.DoesNotContain(bake.Report.Warnings.Sites, s => s.Code == "CS1591");
        Assert.Empty(Ratchet(bake.Report, WarningClass.DocComment).New);
        Assert.True(bake.Report.WarningsAccepted, bake.Log);
    }

    /// <summary>
    /// 🚨 A baseline line naming a centrally-suppressed code is INERT — never STALE. Retiring a
    /// code must not be able to red a repo that has not trimmed its file yet: the moment
    /// <c>CS1701</c> and <c>CS1591</c> stopped being reported, MeshWeaver.Plugins' 210 such lines
    /// would otherwise have become stale in one platform roll, on a bake whose image timing that
    /// repo does not control, with no pull request in flight having touched anything related.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void ABaselineLineNamingASuppressedCode_IsInert_NeverStale()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse(
            [$"{DebtPath} CS0219", $"{DebtPath} CS1574", $"{DebtPath} CS1591", $"{CleanPath} CS1701"]));

        Assert.True(bake.Report.WarningsAccepted, bake.Log);
        Assert.Equal(0, bake.Report.ExitCode);
        Assert.Empty(Ratchet(bake.Report, WarningClass.DocComment).Stale);
        Assert.Empty(Ratchet(bake.Report, WarningClass.Real).Stale);
        // …and it says so, naming the codes, so the dead lines get deleted rather than kept.
        Assert.Contains("INERT baseline entr(ies) naming CS1591, CS1701",
            bake.Log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, asserted rather than assumed. If a warning-free NodeType still
    /// measured warnings, every "the ratchet fired" above would be unearned and the STALE case
    /// below could not be written at all.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void AWarningFreeNodeType_MeasuresZero_SoTheOtherCasesAreNotVacuous()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse([]));

        Assert.All(bake.Report.Warnings.Sites,
            site => Assert.DoesNotContain(CleanPath, site.Types));
        // It compiled — "no warnings" must not be "it never ran".
        Assert.Contains(CleanPath, bake.Report.Warnings.CompiledTypes);
        Assert.Contains(bake.Report.Types, t => t.NodePath == CleanPath && t.Success);
    }

    [Fact(Timeout = 300_000)]
    public void TheSameSource_WithBothPairsBaselined_IsGreen()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse([$"{DebtPath} CS0219", $"{DebtPath} CS1574"]));

        Assert.True(bake.Report.WarningsAccepted, bake.Log);
        Assert.Equal(0, bake.Report.ExitCode);
        Assert.Equal([(DebtPath, "CS0219"), (DebtPath, "CS1574")],
            Ratchet(bake.Report, WarningClass.Real).Known);
    }

    /// <summary>
    /// The SHRINK-ONLY half. A baseline entry for a type that compiles clean of that code is the
    /// debt already paid, and carrying the line anyway is how a ratchet quietly stops ratcheting —
    /// so it fails the run until the line goes.
    ///
    /// <para>🚨 The stale entry names <c>Ctrl/Clean</c> — the negative control, which compiles and
    /// produces nothing. That is what makes this a statement about the ratchet rather than about
    /// the fixture: the type was MEASURED and was clean, which is the only condition under which a
    /// line may be deleted.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void ABaselineEntryWhoseTypeCompilesClean_IsStale_AndRedsTheBake()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse(
            [$"{DebtPath} CS0219", $"{DebtPath} CS1574", $"{CleanPath} CS0219"]));

        Assert.False(bake.Report.WarningsAccepted);
        Assert.Equal(1, bake.Report.ExitCode);

        var real = Ratchet(bake.Report, WarningClass.Real);
        Assert.Equal(CleanPath, Assert.Single(real.Stale).Scope);
        Assert.False(real.Success);
        // The debt that is still real stays KNOWN — a stale line must not swallow a live one.
        Assert.Equal([(DebtPath, "CS0219"), (DebtPath, "CS1574")], real.Known);
        Assert.Contains($"STALE baseline entry", bake.Log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 A baseline entry naming a type this run did NOT compile is UNVERIFIABLE, never stale. A
    /// failed (or absent) type emits no warnings at all, so deleting its line would discard a real
    /// record on a measurement nobody took — the same rule <c>GateVerdict</c> applies to allow
    /// entries whose scope a narrowed run never reached.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void AnEntryForATypeThisRunNeverCompiled_IsUnverifiable_NotStale()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse(
            [$"{DebtPath} CS0219", $"{DebtPath} CS1574", "Ctrl/NotInThisTree CS0219"]));

        var real = Ratchet(bake.Report, WarningClass.Real);
        Assert.Empty(real.Stale);
        Assert.Equal("Ctrl/NotInThisTree", Assert.Single(real.Unverifiable).Scope);
        // Warned, never failed.
        Assert.True(bake.Report.WarningsAccepted, bake.Log);
        Assert.Equal(0, bake.Report.ExitCode);
    }

    /// <summary>
    /// OBSERVE-ONLY is the default and it PRINTS that it is. A repo with no baseline yet has
    /// nothing to enforce, and a run that enforced nothing must never read like a run that found
    /// nothing — the same rule <see cref="GateAllowlist.MissingFileMessage"/> states from the other
    /// side. It also still MEASURES, which is what makes adoption a copy-paste.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void WithNoBaseline_TheBakeMeasures_EnforcesNothing_AndSaysSo()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.ObserveOnly);

        Assert.True(bake.Report.WarningsAccepted);
        Assert.Equal(0, bake.Report.ExitCode);
        Assert.NotEmpty(bake.Report.Warnings.Sites);
        Assert.Contains("warnings ratchet — OBSERVE-ONLY", bake.Log, StringComparison.Ordinal);
        Assert.Contains("doc-comments ratchet — OBSERVE-ONLY", bake.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("ratchet — ENFORCED", bake.Log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE LOG IS AGGREGATED, NOT A TRANSCRIPT — the second half of the maintainer's report
    /// (*"baking … is very verbose"*). The bake must never print one line per warning per compile:
    /// measured on samples/Graph/Data, that is 375 raw occurrences folded to four lines.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void TheReportIsFolded_NotOneLinePerOccurrence()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.Parse([$"{DebtPath} CS0219", $"{DebtPath} CS1574"]));

        var lines = bake.Log.ReplaceLineEndings("\n").Split('\n')
            .Where(l => l.StartsWith(WarningReportWriter.Prefix, StringComparison.Ordinal))
            .ToList();
        // Totals + per-code table + one verdict per ratchet. Nothing else on a run with no NEW
        // pair: every site behind a tolerated entry is a NUMBER in the table above.
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Contains("raw occurrence(s) folded to", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("by code — ", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 THE TWO PRINTED NUMBERS ARE ONE MEASUREMENT. The totals line says "N raw occurrence(s)"
    /// and the per-code table says "CODE n×"; if those are counted on different passes they can
    /// silently disagree, and a reader has no way to tell which one is the measurement.
    ///
    /// <para>They used to be: the table summed each SITE's type count, which equals the raw count
    /// only while no single type produces one (id, message) at two different LINES — and
    /// <c>EmitPipeline.Collect</c> keeps those as two entries (two unused locals of the same name
    /// in two methods is the shape). It happened to hold on every tree measured, which is exactly
    /// how a reporting defect survives. The per-code counts now come off the same pass as the
    /// total, so this identity is true by construction and this case is what keeps it so.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void ThePerCodeTable_SumsToTheTotal()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var bake = Bake(WarningBaseline.ObserveOnly);

        var table = bake.Report.Warnings.ByCode();
        Assert.NotEmpty(table);
        Assert.Equal(bake.Report.Warnings.Occurrences, table.Sum(c => c.Occurrences));
        // …and the fixture has more than one code, so the sum is not the trivial one-row case.
        Assert.True(table.Count > 1, "the fixture must produce at least two distinct codes");

        // 🚨 THE DISCRIMINATOR, asserted so this case cannot go vacuous. `Debt` carries the SAME
        // CS0219 message in two methods, so that code has MORE occurrences than it has (site ×
        // type) pairs — which is exactly the arithmetic the old derivation got wrong. Without this
        // the identity above holds under both implementations and proves nothing.
        var unusedLocal = Assert.Single(table, c => c.Code == "CS0219");
        Assert.Equal(2, unusedLocal.Occurrences);
        Assert.Equal(1, unusedLocal.Sites);
        Assert.Equal(1, unusedLocal.Types);
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────────

    private sealed record BakeRun(TreeBake.Report Report, string Log);

    private BakeRun Bake(WarningBaseline baseline)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var repo = TempDirectory("mw-warn-repo");
        var bakeDir = TempDirectory("mw-warn-bake");
        try
        {
            Write(repo, "Ctrl/index.json", PackageIndexJson);
            Write(repo, "Ctrl/Clean.json", NodeTypeJson("Clean"));
            Write(repo, "Ctrl/Debt.json", NodeTypeJson("Debt"));
            Write(repo, "Ctrl/Clean/Source/Clean.cs", CleanSource);
            Write(repo, "Ctrl/Debt/Source/Debt.cs", DebtSource);

            var log = new StringWriter();
            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bakeDir,
                SourceSha = "deadbeef",
                Output = log,
                Warnings = baseline,
            });
            output.WriteLine(log.ToString());
            Assert.Null(report.FatalError);
            // Both types must COMPILE, or the warning verdict is about a bake that did not happen.
            Assert.All(report.Types, t => Assert.Null(t.Error));
            Assert.Equal(2, report.Types.Length);
            return new BakeRun(report, log.ToString());
        }
        finally
        {
            Cleanup(repo);
            Cleanup(bakeDir);
        }
    }

    private static WarningRatchet Ratchet(TreeBake.Report report, WarningClass warningClass) =>
        Assert.Single(report.WarningRatchets.Where(r => r.Class == warningClass));

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory costs disk, never correctness.
        }
    }
}
