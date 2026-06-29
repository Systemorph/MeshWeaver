using System.Collections.Immutable;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnosticSeverity = MeshWeaver.Mesh.Services.LanguageServer.DiagnosticSeverity;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Builds a <see cref="CSharpCompilation"/> from a NodeType's <see cref="CompilationInputs"/>
/// with one source file substituted by a proposed body, and returns the resulting diagnostics.
/// No caching — every call rebuilds.
/// <para>
/// Strips <c>#r "nuget:..."</c> directives from the proposed source (Roslyn's regular parse
/// mode rejects them with CS7011 "#r is only allowed in scripts") and resolves any new
/// packages via <see cref="INuGetAssemblyResolver"/>, augmenting the cached
/// <see cref="CompilationInputs.References"/> set. Re-uses the resolver's cache so
/// already-seen packages are essentially free on subsequent checks.
/// </para>
/// <para>
/// Used by <c>MeshNodeLanguageService.CheckSpeculative</c> to back the Coder agent's
/// <c>LspCheckNode</c> pre-flight tool. Full substitution (not single-file isolation)
/// catches the dominant Coder failure mode: editing one source breaks a sibling.
/// </para>
/// </summary>
internal sealed class SpeculativeCompilation(INuGetAssemblyResolver nugetResolver)
{
    private const string SkeletonDocumentPath = "__skeleton__.cs";

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        CompilationInputs inputs,
        string sourcePath,
        string proposedCode,
        CancellationToken ct)
    {
        // Strip #r from the proposed source so Roslyn doesn't reject it with CS7011.
        // Resolve any new package refs and append them to the cached reference set —
        // mirrors the production compile path's NuGet handling.
        var (cleanedProposed, proposedNugetRefs) = NuGetDirectiveParser.Extract(proposedCode ?? string.Empty);

        ImmutableArray<MetadataReference> effectiveReferences = inputs.References;
        if (proposedNugetRefs.Length > 0)
        {
            var resolved = await nugetResolver.ResolveAsync(proposedNugetRefs, targetFramework: null, ct);
            if (resolved.AssemblyPaths.Length > 0)
            {
                effectiveReferences = inputs.References
                    .Concat(resolved.AssemblyPaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)))
                    .ToImmutableArray();
            }
        }

        var trees = new List<SyntaxTree>(inputs.Sources.Length + 1)
        {
            CSharpSyntaxTree.ParseText(
                SourceText.From(inputs.SkeletonSource),
                inputs.ParseOptions,
                path: SkeletonDocumentPath),
        };

        var substituted = false;
        foreach (var (path, code) in inputs.Sources)
        {
            string treeCode;
            if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                treeCode = cleanedProposed;
                substituted = true;
            }
            else
            {
                treeCode = code;
            }

            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(treeCode), inputs.ParseOptions, path: path));
        }

        if (!substituted)
        {
            // Proposed source path doesn't match any existing source — treat as a new file.
            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(cleanedProposed), inputs.ParseOptions, path: sourcePath));
        }

        var compilation = CSharpCompilation.Create(
            inputs.AssemblyName,
            syntaxTrees: trees,
            references: effectiveReferences,
            options: inputs.CompilationOptions);

        var diags = compilation.GetDiagnostics(ct);
        if (diags.IsDefaultOrEmpty) return Array.Empty<DiagnosticInfo>();

        var result = new List<DiagnosticInfo>(diags.Length);
        foreach (var d in diags)
        {
            // Skeleton-tree diagnostics are framework noise — the user can't act on them.
            if (d.Location.SourceTree?.FilePath == SkeletonDocumentPath) continue;
            result.Add(ToDiagnosticInfo(d));
        }
        return result;
    }

    private static DiagnosticInfo ToDiagnosticInfo(Diagnostic d)
    {
        SourceLocation? location = null;
        if (d.Location.IsInSource && d.Location.SourceTree?.FilePath is { Length: > 0 } path)
        {
            var span = d.Location.GetLineSpan();
            location = new SourceLocation(
                path,
                new SourceRange(
                    new SourcePosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new SourcePosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
        }
        return new DiagnosticInfo(
            Id: d.Id,
            Severity: MapSeverity(d.Severity),
            Message: d.GetMessage(),
            Location: location);
    }

    private static LspDiagnosticSeverity MapSeverity(RoslynDiagnosticSeverity s) => s switch
    {
        RoslynDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hidden,
        RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Info,
        RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
        RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
        _ => LspDiagnosticSeverity.Info,
    };
}
