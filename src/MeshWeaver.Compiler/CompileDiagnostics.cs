using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Lsp = MeshWeaver.Mesh.Services.LanguageServer;

namespace MeshWeaver.Compiler;

/// <summary>
/// Diagnostics shaping for dynamic NodeType compiles: the flattened failure summary, the
/// structured per-source-file re-derivation, and the source-discovery report. Pure over
/// <see cref="CompilationInputs"/> / Roslyn diagnostics; the scheduling and the write-backs stay
/// with the caller in MeshWeaver.Graph.
/// </summary>
public static class CompileDiagnostics
{
    /// <summary>
    /// Formats a failed Roslyn <c>Emit</c>'s diagnostics into a complete, never-empty error
    /// message — each line carries the diagnostic <c>CS####</c> id, severity, source line and
    /// message. Falls back to Warning-severity diagnostics when there are no Errors, and to an
    /// explanatory sentence when Emit failed with NO diagnostics at all (typically a missing
    /// source file or a configuration lambda referencing a type that was never compiled). The
    /// previous <c>Where(Severity == Error).Select(GetMessage)</c> produced a bare
    /// "Compilation failed for 'X':" whenever the failure carried no Error-severity diagnostic.
    /// </summary>
    internal static string FormatCompileFailure(string nodePath, IEnumerable<Diagnostic> diagnostics)
    {
        var joined = string.Join('\n', diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity)
            .Select(d =>
            {
                var loc = d.Location.IsInSource
                    ? $" (line {d.Location.GetLineSpan().StartLinePosition.Line + 1})"
                    : "";
                return $"{d.Id} {d.Severity}{loc}: {d.GetMessage()}";
            }));
        return !string.IsNullOrEmpty(joined)
            ? $"Compilation failed for '{nodePath}':\n{joined}"
            : $"Compilation failed for '{nodePath}': Roslyn emit failed but produced no error/warning "
              + "diagnostics — this usually means a source file was not found, or the configuration "
              + "lambda references a type that was never compiled (see the source-discovery report below).";
    }

    /// <summary>
    /// Sentinel FilePath for the generated skeleton tree — must match the one the LSP uses
    /// so skeleton-internal diagnostics (framework noise the user can't act on) are filtered out.
    /// </summary>
    internal const string SkeletonDiagnosticsPath = "__skeleton__.cs";

    /// <summary>
    /// Sentinel FilePath for the generated <c>global using</c> import scope (#1802) — filtered out
    /// of results for the same reason as the skeleton: generated, and not editable by the user.
    /// </summary>
    internal const string GlobalUsingsDiagnosticsPath = "__global_usings__.cs";

    /// <summary>
    /// Re-derives a failed compile's diagnostics in their structured, per-source-file form by
    /// assembling ONE LSP-style compilation (skeleton tree + one tree per src/test Code node,
    /// each carrying the MeshNode path as its <c>FilePath</c>) — exactly the model
    /// <see cref="SpeculativeCompilation"/> / the language service use, so a diagnostic's
    /// <see cref="Lsp.SourceLocation.SourcePath"/> is the Code node path. This is what lets the
    /// GUI mark each error at its exact line/column in a Monaco editor and link to the source.
    /// Synchronous, CPU-bound — the caller schedules it off the hub.
    /// </summary>
    internal static IReadOnlyList<Lsp.DiagnosticInfo> DiagnoseInputs(CompilationInputs inputs)
    {
        var trees = new List<SyntaxTree>(inputs.Sources.Length + 2)
        {
            CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(inputs.SkeletonSource),
                inputs.ParseOptions, path: SkeletonDiagnosticsPath),
            // 🚨 The import scope, as `global using`. The per-file trees below give each diagnostic
            // the Code node's path (the whole point here), but a C# `using` is FILE-SCOPED, so
            // without this document the skeleton's preamble covers none of them and this reports
            // errors the emit path does not have — #1802, which put 12 phantom CS0246/CS0308 on a
            // NodeType whose assembly had built and loaded.
            CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(inputs.GlobalUsingsSource),
                inputs.ParseOptions, path: GlobalUsingsDiagnosticsPath),
        };
        foreach (var (path, code) in inputs.Sources)
            trees.Add(CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(code), inputs.ParseOptions, path: path));

        // Structured failure diagnostics are best-effort: pass no generator candidates so
        // RunSourceGenerators is a no-op here (the authoritative flat summary from the production
        // compile already reflects generation). Avoids loading any generator on every failed compile.
        var compilation = GeneratorPipeline.RunSourceGenerators(
            CSharpCompilation.Create(inputs.AssemblyName, trees, inputs.References, inputs.CompilationOptions),
            Array.Empty<string>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        var diags = compilation.GetDiagnostics();
        if (diags.IsDefaultOrEmpty) return Array.Empty<Lsp.DiagnosticInfo>();

        var result = new List<Lsp.DiagnosticInfo>(diags.Length);
        foreach (var d in diags)
        {
            if (d.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) continue;
            // Generated-tree diagnostics are framework noise the user can't act on.
            var treePath = d.Location.SourceTree?.FilePath;
            if (treePath == SkeletonDiagnosticsPath || treePath == GlobalUsingsDiagnosticsPath) continue;
            result.Add(ToDiagnosticInfo(d));
        }
        // Errors first, then by file then position — stable order for the GUI.
        return result
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location?.SourcePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.Range.Start.Line ?? 0)
            .ToList();
    }

    internal static Lsp.DiagnosticInfo ToDiagnosticInfo(Diagnostic d)
    {
        Lsp.SourceLocation? location = null;
        if (d.Location.IsInSource && d.Location.SourceTree?.FilePath is { Length: > 0 } path)
        {
            var span = d.Location.GetLineSpan();
            location = new Lsp.SourceLocation(
                path,
                new Lsp.SourceRange(
                    new Lsp.SourcePosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new Lsp.SourcePosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
        }
        return new Lsp.DiagnosticInfo(d.Id, MapDiagnosticSeverity(d.Severity), d.GetMessage(), location);
    }

    internal static Lsp.DiagnosticSeverity MapDiagnosticSeverity(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Hidden => Lsp.DiagnosticSeverity.Hidden,
        DiagnosticSeverity.Info => Lsp.DiagnosticSeverity.Info,
        DiagnosticSeverity.Warning => Lsp.DiagnosticSeverity.Warning,
        DiagnosticSeverity.Error => Lsp.DiagnosticSeverity.Error,
        _ => Lsp.DiagnosticSeverity.Info,
    };

    /// <summary>Formats the source-discovery report appended to every compile failure: every
    /// executed query and every matched Code path.</summary>
    internal static string BuildSourceDiscoveryReport(
        IReadOnlyList<string> executedQueries, IReadOnlyList<string> matchedCodePaths)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Executed source queries ({executedQueries.Count}):");
        foreach (var q in executedQueries)
            sb.AppendLine($"  - {q}");
        sb.AppendLine($"Matched Code nodes ({matchedCodePaths.Count}):");
        if (matchedCodePaths.Count == 0)
            sb.AppendLine("  (none) — the configuration lambda cannot reference types because no source files were included. Check that your Source Code nodes exist and that the NodeType's `sources` list points at them.");
        else
            foreach (var p in matchedCodePaths)
                sb.AppendLine($"  - {p}");
        return sb.ToString();
    }

    /// <summary>
    /// How many diagnostic lines of <c>compileError</c> the LOGGED report carries. The complete
    /// set still travels on the exception (printed after the message by the console formatter)
    /// and is written to <c>NodeTypeDefinition.CompilationDiagnostics</c>; this bounds only the
    /// copy that has to survive a fixed-size evidence capture.
    /// </summary>
    internal const int MaxLoggedDiagnosticLines = 12;

    /// <summary>
    /// How many matched Code paths the LOGGED report lists. The full list is in the compile's own
    /// ActivityLog (<c>get @{Type}/_Activity/compile-…</c>), which has no size cap.
    /// </summary>
    internal const int MaxLoggedMatchedPaths = 8;

    /// <summary>
    /// 🚨 THE failure report the compile pipeline's single reporting funnel LOGS — and the order of
    /// its sections is the whole point (issue #1840).
    ///
    /// <para><b>What went wrong.</b> The funnel logged
    /// <c>LogError(ex, "Failed to compile assembly for node {NodePath}. {Diagnostics}", path,
    /// sourceDiscoveryReport)</c>. The compiler diagnostics were never discarded — they ride on
    /// <c>CompilationException.Message</c> (pinned by
    /// <c>CompileFailureReportedOnceTest.Failed_emit_throws_the_full_diagnostics_and_logs_nothing</c>)
    /// and the console formatter prints the exception AFTER the message. But the message it printed
    /// first was the source-discovery report, whose length scales with the number of matched Code
    /// nodes — 26 of them for <c>…/Northwind/AnalyticsCatalog</c>, about 2.4 kB. The red-log watcher
    /// keeps <c>LogWatcherOptions.MaxSampleLength</c> (2000) characters of a burst
    /// (<c>BurstAggregator.Truncate</c>), so the evidence attached to the incident ended
    /// <c>…[truncated]</c> partway down the node listing and the exception — the only actionable
    /// part — never made it into the ticket. An operator reading the incident could see 26 file
    /// names and not one compiler error.</para>
    ///
    /// <para><b>The rule this encodes.</b> A failure report is ordered by ACTIONABILITY, because
    /// everything downstream of the logger truncates from the END: the node, then the compiler's
    /// own verdict, then the source-discovery context. The listing that scales with the input is
    /// bounded here and kept in full where nothing truncates it (the ActivityLog and
    /// <c>CompilationDiagnostics</c>), so a big source set can never crowd out the diagnostics
    /// again.</para>
    ///
    /// <para>Pure — no hub, no logger, no I/O — so the ordering invariant is asserted directly.</para>
    /// </summary>
    /// <param name="nodePath">The NodeType whose compile failed.</param>
    /// <param name="compileError">The compiler's verdict — <c>CompilationException.Message</c>,
    /// i.e. the output of <see cref="FormatCompileFailure"/> for a Roslyn failure.</param>
    /// <param name="executedQueries">Every source query the compile ran.</param>
    /// <param name="matchedCodePaths">Every Code node those queries matched.</param>
    internal static string FormatCompileFailureReport(
        string nodePath,
        string? compileError,
        IReadOnlyList<string> executedQueries,
        IReadOnlyList<string> matchedCodePaths)
    {
        var sb = new System.Text.StringBuilder();

        // 1. WHAT failed. One short line, so the two facts below always start inside any budget.
        sb.Append("Failed to compile assembly for node '").Append(nodePath).AppendLine("'.");

        // 2. WHY — the compiler's own verdict, FIRST, because it is the only part that says what to
        //    change. Bounded by line count, never by a character cut that could slice a CS id in half.
        sb.AppendLine("--- Compiler diagnostics ---");
        if (string.IsNullOrWhiteSpace(compileError))
            sb.AppendLine("  (the failure carried no message — see the exception below)");
        else
        {
            var lines = compileError.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length && i < MaxLoggedDiagnosticLines; i++)
                sb.AppendLine(lines[i]);
            if (lines.Length > MaxLoggedDiagnosticLines)
                sb.AppendLine(
                    $"  … and {lines.Length - MaxLoggedDiagnosticLines} more diagnostic line(s) — "
                    + "the complete set is on the exception below and in the NodeType's "
                    + "CompilationDiagnostics.");
        }

        // 3. WHERE the compile looked. Queries first (there are a handful and they explain an empty
        //    or surprising source set), then a BOUNDED sample of what they matched.
        sb.AppendLine("--- Source discovery ---");
        sb.AppendLine($"Executed source queries ({executedQueries.Count}):");
        foreach (var q in executedQueries)
            sb.AppendLine($"  - {q}");

        if (matchedCodePaths.Count == 0)
        {
            sb.AppendLine("Matched Code nodes (0):");
            sb.AppendLine(
                "  (none) — the configuration lambda cannot reference types because no source files "
                + "were included. Check that your Source Code nodes exist and that the NodeType's "
                + "`sources` list points at them.");
            return sb.ToString();
        }

        var listed = Math.Min(matchedCodePaths.Count, MaxLoggedMatchedPaths);
        sb.AppendLine(matchedCodePaths.Count > listed
            ? $"Matched Code nodes ({matchedCodePaths.Count}, first {listed} listed):"
            : $"Matched Code nodes ({matchedCodePaths.Count}):");
        for (var i = 0; i < listed; i++)
            sb.AppendLine($"  - {matchedCodePaths[i]}");
        if (matchedCodePaths.Count > listed)
            sb.AppendLine(
                $"  … and {matchedCodePaths.Count - listed} more — the full list is in the compile's "
                + "ActivityLog, which is not size-capped.");
        return sb.ToString();
    }
}
