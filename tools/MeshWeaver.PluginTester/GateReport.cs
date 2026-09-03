using System.Collections.Immutable;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Outcome of one gate check (compile / render / Tests area) on one NodeType.
///
/// <para>🚨 <b>Three ways for a check not to pass, and they have DIFFERENT OWNERS.</b> They were
/// one member (<see cref="Failed"/>) until #2454/#2463, and collapsing them is the most expensive
/// reporting defect this gate can have: a verdict that names the wrong component sends the reader
/// to the one place the cause provably is not.</para>
///
/// <list type="table">
///   <item><term><see cref="Failed"/></term><description>the check produced a NEGATIVE verdict —
///     the compiler emitted diagnostics, the area rendered an error, a test went red. <b>Fix the
///     source.</b></description></item>
///   <item><term><see cref="Inconclusive"/></term><description>the check produced NO verdict —
///     nothing answered within the budget. "I did not get an answer" is not "the answer was no".
///     <b>Investigate the mesh, not the source.</b></description></item>
///   <item><term><see cref="Unrecorded"/></term><description>the WORK succeeded and the mesh
///     failed to RECORD it — the bake this run consumed carries the type's assembly, so the
///     compile is proven, and only the status write was lost. <b>Fix the writer; the content is
///     fine.</b></description></item>
/// </list>
///
/// <para>Modelled on <c>MeshWeaver.Hosting.PreWarmStatus</c>, which already separates
/// <c>CompileError</c> from <c>TimedOut</c> / <c>NoSources</c> / <c>UpstreamUnevaluated</c> for
/// exactly this reason — "I don't know" propagates as "I don't know" there rather than becoming a
/// verdict. This is the same distinction at the CI gate.</para>
///
/// <para>🚨 All three still FAIL the run. A gate that cannot judge must not look like a gate that
/// passed (the node-repo CI policy's invariants #3/#4), and neither non-verdict is evidence the
/// content is good. What changes is WHICH CAUSE IS NAMED — see
/// <c>GateVerdict.Headline</c>.</para>
/// </summary>
public enum CheckOutcome
{
    /// <summary>The check passed.</summary>
    Passed,

    /// <summary>
    /// The check produced a NEGATIVE verdict: the compiler emitted diagnostics, the area rendered
    /// an error control, a test row went red. This is a CONTENT failure — the gate exits non-zero
    /// and the reader fixes the source.
    /// </summary>
    Failed,

    /// <summary>The check does not apply (e.g. the type declares no Tests area).</summary>
    Skipped,

    /// <summary>
    /// NO VERDICT: the check never produced an answer within its budget — the mesh wrote no
    /// terminal compile status, an area never emitted. The gate exits non-zero (an unjudged check
    /// is not a passing one) but the failure is a TIMEOUT or a WEDGE, not a diagnostic: nothing
    /// reported an error and no source was found wanting.
    ///
    /// <para>🚨 Reported under its own headline label so a CI annotation cannot send the reader to
    /// diff the PR's source. #2454: a PR whose entire diff was one markdown line, a test ledger
    /// and a test-list row was annotated <i>"a public API change here broke plugin node
    /// source"</i> — the gate had observed <c>no terminal compile status within 300s</c> and
    /// scored it <see cref="Failed"/>.</para>
    /// </summary>
    Inconclusive,

    /// <summary>
    /// The WORK SUCCEEDED and the mesh failed to RECORD it. Reachable only on a run that CONSUMES
    /// a bake (<see cref="GateOptions.Seed"/>): the bake declares an assembly for this type, so
    /// the compile is PROVEN by bytes on disk, and what is missing is the mesh-side status write.
    ///
    /// <para>🚨 This is INFRASTRUCTURE, never content. #2463: the compiler logged
    /// <c>ok RolePlay/Scenery</c> and baked four assemblies; six minutes later <c>MergeGuard</c>
    /// refused the adoption stamp as a stale/reordered cross-hub write, <c>CompileWatcher</c> said
    /// <c>the write did not converge</c> in as many words — and the gate reported
    /// <c>compile=FAILED</c>, turning main red and holding a production rollout for ~11 hours on a
    /// compile that had never failed. Same shape in #2454, where the owning hub was disposed with
    /// a <c>CreateOrUpdateNodeRequest</c> still in flight.</para>
    ///
    /// <para>It still fails the run: the type never settled, so the gate could judge neither its
    /// render nor its <c>Tests</c> area, and a green here would be a claim about checks that never
    /// ran. Making it PASS would be a separate, deliberate policy change — it needs the render and
    /// Tests halves to run against the adopted bytes first.</para>
    /// </summary>
    Unrecorded,
}

/// <summary>Outcome classification helpers shared by the report and the verdict.</summary>
public static class CheckOutcomes
{
    /// <summary>
    /// Whether <paramref name="outcome"/> fails the gate. Every non-verdict fails: a check that
    /// produced no answer is not a check that passed. 🚨 Never write <c>!= Failed</c> — that is
    /// the test that made <see cref="CheckOutcome.Inconclusive"/> and
    /// <see cref="CheckOutcome.Unrecorded"/> silently green when they were introduced.
    /// </summary>
    public static bool Fails(this CheckOutcome outcome) =>
        outcome is CheckOutcome.Failed or CheckOutcome.Inconclusive or CheckOutcome.Unrecorded;

    /// <summary>
    /// The headline label for a failing <paramref name="outcome"/> on <paramref name="check"/>:
    /// the bare check name for a real verdict, a distinct token for each non-verdict — so the ONE
    /// line CI lifts cannot say "compile" about a compile that succeeded.
    /// </summary>
    public static string Label(string check, CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Inconclusive => $"{check}-no-verdict",
        CheckOutcome.Unrecorded => $"{check}-status-unrecorded",
        _ => check,
    };

    /// <summary>
    /// The one clause that tells the reader WHERE TO LOOK, appended once per non-verdict kind in
    /// the headline. Null for a real verdict — the check name already says it.
    /// </summary>
    public static string? Guidance(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Inconclusive =>
            "no result was observed — a timeout or a wedge in the mesh, NOT a compiler "
            + "diagnostic; investigate the mesh, not the source",
        CheckOutcome.Unrecorded =>
            "the work SUCCEEDED and the mesh did not record it — infrastructure, not the "
            + "content; fix the writer",
        _ => null,
    };
}

/// <summary>The gate results for one NodeType node of a package.</summary>
/// <param name="Path">The NodeType node's mesh path (e.g. <c>Edu/CourseInvite</c>).</param>
/// <param name="Package">The package (top-level folder) the type ships in.</param>
public sealed record NodeTypeResult(string Path, string Package)
{
    /// <summary>Terminal compile state, or null when the type has nothing to compile.</summary>
    public CompilationStatus? CompilationStatus { get; init; }

    /// <summary>Whether the compile gate passed.</summary>
    public CheckOutcome Compile { get; init; } = CheckOutcome.Skipped;

    /// <summary>Roslyn diagnostics / error detail when <see cref="Compile"/> failed.</summary>
    public string? CompileDetail { get; init; }

    /// <summary>Whether the type node's default area rendered without an error control.</summary>
    public CheckOutcome Render { get; init; } = CheckOutcome.Skipped;

    /// <summary>Failure detail when <see cref="Render"/> failed.</summary>
    public string? RenderDetail { get; init; }

    /// <summary>Whether the type's <c>Tests</c> layout area executed green.</summary>
    public CheckOutcome Tests { get; init; } = CheckOutcome.Skipped;

    /// <summary>The Tests verdict detail (the pass/fail summary, or the red rows).</summary>
    public string? TestsDetail { get; init; }

    /// <summary>
    /// WHICH node hosted the <c>Tests</c> run, and how the gate picked it — a shipped instance or
    /// the throwaway probe it created. A type's <c>Tests</c> area is served by INSTANCE hubs, never
    /// by the type node, so "Area not found" is only diagnosable together with the host: without
    /// this line the 2026-08-10 <c>Store/Catalog</c> RED was read as "the probe landed on a type
    /// path" when it had in fact landed on a correctly-typed shipped instance whose hub was serving
    /// the mesh default configuration (issue #1077).
    /// </summary>
    public string? TestsHost { get; init; }

    /// <summary>
    /// True when no gate check failed. 🚨 Reads <see cref="CheckOutcomes.Fails"/>, never
    /// <c>!= Failed</c>: a check that produced NO verdict
    /// (<see cref="CheckOutcome.Inconclusive"/> / <see cref="CheckOutcome.Unrecorded"/>) has not
    /// passed either, and an inequality test against one member silently admits every member
    /// added after it.
    /// </summary>
    public bool Success =>
        !Compile.Fails() && !Render.Fails() && !Tests.Fails();
}

/// <summary>The gate results for one installed package.</summary>
/// <param name="Id">The package id (its top-level folder).</param>
public sealed record PackageResult(string Id)
{
    /// <summary>Total nodes the package carried.</summary>
    public int NodeCount { get; init; }

    /// <summary>
    /// True when the package arrived from the bake seed's upstream publication: it is INSTALLED
    /// (a failure still fails the run - a gate that cannot install its dependencies proves
    /// nothing) but its types are not gated here; their verdicts belong to the repo that owns
    /// them. See <see cref="SeedPackages"/>.
    /// </summary>
    public bool Upstream { get; init; }

    /// <summary>
    /// True when this run is one SHARD of a fanned-out gate and the package is here only so the
    /// shard's own slice can install (<see cref="GateShardPlan"/>): installed — a failed install
    /// still fails the shard — but not gated, because the shard that OWNS it holds its verdict.
    /// Every package is gated exactly once across the fan-out, which is what lets the aggregate
    /// job fold the shards' summaries into one without double-counting a type.
    /// </summary>
    public bool Support { get; init; }

    /// <summary>
    /// Whether <see cref="NodeCount"/> / <see cref="NodeTypes"/> are a MEASUREMENT. False when the
    /// package pipeline threw before the install reported, in which case both are defaults and
    /// printing them asserts a count nobody took.
    ///
    /// <para>🚨 Why this exists (#1360). A wait inside the install timed out AFTER the nodes had
    /// been written — the identical snapshot wrote 34 nodes on the very next run — and the gate
    /// reported <c>[FAIL] Essentials (0 node(s), 0 type(s))</c>. "Its hub vanished mid-install" and
    /// "it legitimately had nothing to install" rendered as the same line, which is exactly why the
    /// signature was filed away as harness noise. A failure still FAILS; it just may not claim to
    /// have counted anything.</para>
    /// </summary>
    public bool CountsMeasured { get; init; } = true;

    /// <summary>Install failure detail; null when the install succeeded.</summary>
    public string? InstallError { get; init; }

    /// <summary>
    /// Idempotence failure detail: a SECOND install of the identical snapshot must write zero
    /// nodes (the unchanged-skip is what keeps a re-sync from churning versions, re-broadcasting
    /// nodes and recompiling untouched NodeTypes). Null when the re-install wrote nothing.
    /// </summary>
    public string? IdempotenceError { get; init; }

    /// <summary>Per-NodeType gate results.</summary>
    public IReadOnlyList<NodeTypeResult> NodeTypes { get; init; } = [];

    /// <summary>True when the install, the re-install idempotence pin and every NodeType gate passed.</summary>
    public bool Success => InstallError is null && IdempotenceError is null && NodeTypes.All(t => t.Success);
}

/// <summary>The whole run's outcome: per-package results and the process exit code.</summary>
/// <param name="Packages">Per-package results in install order.</param>
public sealed record GateReport(IReadOnlyList<PackageResult> Packages)
{
    /// <summary>A fatal error outside any single package (discovery, mesh boot).</summary>
    public string? FatalError { get; init; }

    /// <summary>True when every package passed and no fatal error occurred.</summary>
    public bool Success => FatalError is null && Packages.All(p => p.Success);

    /// <summary>Process exit code: 0 = all green.</summary>
    public int ExitCode => Success ? 0 : 1;

    /// <summary>
    /// Maps this report onto the structured wire contract <c>--report</c> writes
    /// (<see cref="GateRunReport"/>, owned by <c>MeshWeaver.PluginCatalog</c> next to the verdict
    /// types that consume it) — how the combo verifier, running OUTSIDE the candidate image, reads
    /// the verdict of the tester running inside it. Loss-free for everything the verdict folding
    /// needs; <c>GateRunReportContractTest</c> pins the round-trip.
    /// </summary>
    public GateRunReport ToRunReport() => new()
    {
        FatalError = FatalError,
        Packages = Packages
            .Select(package => new GateRunPackage
            {
                Id = package.Id,
                NodeCount = package.NodeCount,
                InstallError = package.InstallError,
                IdempotenceError = package.IdempotenceError,
                NodeTypes = package.NodeTypes
                    .Select(type => new GateRunNodeType
                    {
                        Path = type.Path,
                        CompilationStatus = type.CompilationStatus?.ToString(),
                        Compile = Map(type.Compile),
                        CompileDetail = type.CompileDetail,
                        Render = Map(type.Render),
                        RenderDetail = type.RenderDetail,
                        Tests = Map(type.Tests),
                        TestsDetail = type.TestsDetail,
                    })
                    .ToImmutableList(),
            })
            .ToImmutableList(),
    };

    /// <summary>
    /// Maps a local outcome onto the wire contract. 🚨 EXHAUSTIVE by construction — a new member
    /// must be given a wire spelling here, never fall into a catch-all. The previous
    /// <c>_ =&gt; Skipped</c> would have reported both non-verdicts to the combo verifier as
    /// "the check does not apply", which is the same collapse one process boundary further out.
    /// </summary>
    private static GateRunOutcome Map(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Passed => GateRunOutcome.Passed,
        CheckOutcome.Failed => GateRunOutcome.Failed,
        CheckOutcome.Inconclusive => GateRunOutcome.Inconclusive,
        CheckOutcome.Unrecorded => GateRunOutcome.Unrecorded,
        _ => GateRunOutcome.Skipped,
    };

    /// <summary>
    /// Writes the human-readable per-package summary table. With a <paramref name="verdict"/>
    /// (an allowlist was applied), a package or check whose only failures are known debt is
    /// labeled DEBT rather than FAIL, stale allow entries are listed by name (they fail the
    /// run — the list must shrink), and the final line states the verdict, never the raw
    /// pass/fail — two bottom lines disagreeing on "green" is how a gate gets ignored.
    /// </summary>
    public void WriteSummary(TextWriter output, GateVerdict? verdict = null)
    {
        output.WriteLine();
        output.WriteLine("=== mw-plugin-test summary ===");
        if (FatalError is not null)
            output.WriteLine($"FATAL: {FatalError}");
        foreach (var package in Packages)
        {
            output.WriteLine($"[{Label(package, verdict)}] {package.Id} " +
                             (package.CountsMeasured
                                 ? $"({package.NodeCount} node(s), {package.NodeTypes.Count} type(s))"
                                 : "(counts unavailable — the pipeline threw before the install reported)") +
                             (package.Upstream ? " [upstream: installed, not gated here]" : "") +
                             (package.Support ? " [support: installed, gated on another shard]" : ""));
            if (package.InstallError is not null)
                output.WriteLine($"    install{Debt(verdict, package.Id, "install")}: {package.InstallError}");
            if (package.IdempotenceError is not null)
                output.WriteLine($"    idempotence{Debt(verdict, package.Id, "idempotence")}: {package.IdempotenceError}");
            foreach (var type in package.NodeTypes)
            {
                output.WriteLine(
                    $"    {(type.Success ? "ok " : "RED")} {type.Path}: " +
                    $"compile={Describe(type.Compile, type.CompilationStatus?.ToString())}{Debt(verdict, type.Path, "compile")} " +
                    $"render={Describe(type.Render)}{Debt(verdict, type.Path, "render")} " +
                    $"tests={Describe(type.Tests)}{Debt(verdict, type.Path, "tests")}");
                if (type.CompileDetail is not null)
                    output.WriteLine(Indent(type.CompileDetail));
                if (type.RenderDetail is not null)
                    output.WriteLine(Indent(type.RenderDetail));
                if (type.TestsHost is not null)
                    output.WriteLine(Indent($"Tests host: {type.TestsHost}"));
                if (type.TestsDetail is not null)
                    output.WriteLine(Indent(type.TestsDetail));
            }
        }
        if (verdict is null)
        {
            output.WriteLine(Success ? "ALL GREEN." : $"{FailedPrefix} — {GateVerdict.Headline(this)}");
            return;
        }
        foreach (var entry in verdict.Stale)
            output.WriteLine($"STALE allow entry (now passing — remove it): {entry}");
        foreach (var entry in verdict.Unverifiable)
            output.WriteLine($"unverifiable allow entry (check skipped or scope absent this run): {entry}");
        var green = FatalError is null && verdict.Success;
        output.WriteLine(green
            ? verdict.KnownDebt.Count == 0
                ? "ALL GREEN."
                : $"GREEN — {verdict.KnownDebt.Count} known-debt failure(s) allowed (shrinking list)."
            : $"{FailedPrefix} — {GateVerdict.Headline(this, verdict)} — " +
              $"{verdict.NewFailures.Count} new failure(s), {verdict.Stale.Count} stale allow entr(ies).");
    }

    /// <summary>
    /// The stable prefix of the ONE line CI lifts verbatim into its failure annotation. The whole
    /// line is the message — no parsing, so the annotation cannot drift out of step with the
    /// verdict. Deliberately ASCII: <c>.github/workflows/dotnet-test.yml</c> greps
    /// <c>^GATE FAILED</c>, so re-punctuating the line cannot silently unhook the annotation and
    /// leave CI back on a guessed cause. Changing this literal changes that grep — keep them in
    /// step.
    /// </summary>
    public const string FailedPrefix = "GATE FAILED";

    private static string Label(PackageResult package, GateVerdict? verdict)
    {
        if (package.Success)
            return "PASS";
        if (verdict is null)
            return "FAIL";
        var allKnown =
            (package.InstallError is null || verdict.IsKnownDebt(package.Id, "install"))
            && (package.IdempotenceError is null || verdict.IsKnownDebt(package.Id, "idempotence"))
            && package.NodeTypes.All(t =>
                (!t.Compile.Fails() || verdict.IsKnownDebt(t.Path, "compile"))
                && (!t.Render.Fails() || verdict.IsKnownDebt(t.Path, "render"))
                && (!t.Tests.Fails() || verdict.IsKnownDebt(t.Path, "tests")));
        return allKnown ? "DEBT" : "FAIL";
    }

    private static string Debt(GateVerdict? verdict, string scope, string check) =>
        verdict is not null && verdict.IsKnownDebt(scope, check) ? " [known-debt]" : "";

    /// <summary>
    /// The per-type summary token. 🚨 The three not-passing states print DIFFERENTLY — this line
    /// is what a human scans, and <c>compile=FAILED</c> for a compile that succeeded is the whole
    /// defect (#2454/#2463).
    /// </summary>
    private static string Describe(CheckOutcome outcome, string? detail = null) =>
        outcome switch
        {
            CheckOutcome.Passed => detail is null ? "ok" : detail,
            CheckOutcome.Failed => detail is null ? "FAILED" : $"FAILED({detail})",
            CheckOutcome.Inconclusive =>
                detail is null ? "NO-VERDICT" : $"NO-VERDICT({detail})",
            CheckOutcome.Unrecorded =>
                detail is null ? "UNRECORDED" : $"UNRECORDED({detail})",
            _ => "skipped",
        };

    private static string Indent(string text) =>
        "        " + text.ReplaceLineEndings("\n").Replace("\n", "\n        ");
}
