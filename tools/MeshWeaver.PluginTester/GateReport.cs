using MeshWeaver.Mesh.Services;

namespace MeshWeaver.PluginTester;

/// <summary>Outcome of one gate check (compile / render / Tests area) on one NodeType.</summary>
public enum CheckOutcome
{
    /// <summary>The check passed.</summary>
    Passed,

    /// <summary>The check failed — the gate exits non-zero.</summary>
    Failed,

    /// <summary>The check does not apply (e.g. the type declares no Tests area).</summary>
    Skipped,
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

    /// <summary>True when no gate check failed.</summary>
    public bool Success =>
        Compile != CheckOutcome.Failed
        && Render != CheckOutcome.Failed
        && Tests != CheckOutcome.Failed;
}

/// <summary>The gate results for one installed package.</summary>
/// <param name="Id">The package id (its top-level folder).</param>
public sealed record PackageResult(string Id)
{
    /// <summary>Total nodes the package carried.</summary>
    public int NodeCount { get; init; }

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
                             $"({package.NodeCount} node(s), {package.NodeTypes.Count} type(s))");
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
                (t.Compile != CheckOutcome.Failed || verdict.IsKnownDebt(t.Path, "compile"))
                && (t.Render != CheckOutcome.Failed || verdict.IsKnownDebt(t.Path, "render"))
                && (t.Tests != CheckOutcome.Failed || verdict.IsKnownDebt(t.Path, "tests")));
        return allKnown ? "DEBT" : "FAIL";
    }

    private static string Debt(GateVerdict? verdict, string scope, string check) =>
        verdict is not null && verdict.IsKnownDebt(scope, check) ? " [known-debt]" : "";

    private static string Describe(CheckOutcome outcome, string? detail = null) =>
        outcome switch
        {
            CheckOutcome.Passed => detail is null ? "ok" : detail,
            CheckOutcome.Failed => detail is null ? "FAILED" : $"FAILED({detail})",
            _ => "skipped",
        };

    private static string Indent(string text) =>
        "        " + text.ReplaceLineEndings("\n").Replace("\n", "\n        ");
}
