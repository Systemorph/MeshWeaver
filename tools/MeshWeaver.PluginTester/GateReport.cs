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

    /// <summary>Writes the human-readable per-package summary table.</summary>
    public void WriteSummary(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("=== mw-plugin-test summary ===");
        if (FatalError is not null)
            output.WriteLine($"FATAL: {FatalError}");
        foreach (var package in Packages)
        {
            output.WriteLine($"[{(package.Success ? "PASS" : "FAIL")}] {package.Id} " +
                             $"({package.NodeCount} node(s), {package.NodeTypes.Count} type(s))");
            if (package.InstallError is not null)
                output.WriteLine($"    install: {package.InstallError}");
            if (package.IdempotenceError is not null)
                output.WriteLine($"    idempotence: {package.IdempotenceError}");
            foreach (var type in package.NodeTypes)
            {
                output.WriteLine(
                    $"    {(type.Success ? "ok " : "RED")} {type.Path}: " +
                    $"compile={Describe(type.Compile, type.CompilationStatus?.ToString())} " +
                    $"render={Describe(type.Render)} tests={Describe(type.Tests)}");
                if (type.CompileDetail is not null)
                    output.WriteLine(Indent(type.CompileDetail));
                if (type.RenderDetail is not null)
                    output.WriteLine(Indent(type.RenderDetail));
                if (type.TestsDetail is not null)
                    output.WriteLine(Indent(type.TestsDetail));
            }
        }
        output.WriteLine(Success ? "ALL GREEN." : "GATE FAILED.");
    }

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
