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
        var trees = new List<SyntaxTree>(inputs.Sources.Length + 1)
        {
            CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(inputs.SkeletonSource),
                inputs.ParseOptions, path: SkeletonDiagnosticsPath),
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
            // Skeleton-internal diagnostics are framework noise the user can't act on.
            if (d.Location.SourceTree?.FilePath == SkeletonDiagnosticsPath) continue;
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
}
