using System.Collections.Immutable;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnosticSeverity = MeshWeaver.Mesh.Services.LanguageServer.DiagnosticSeverity;

namespace MeshWeaver.Compiler;

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
/// Used by <c>MeshNodeLanguageService.CheckSpeculative</c> to back the <c>LspCheckNode</c>
/// pre-flight tool (the /code skill's edit loop). Full substitution (not single-file
/// isolation) catches the dominant code-edit failure mode: editing one source breaks a sibling.
/// </para>
/// </summary>
internal sealed class SpeculativeCompilation(INuGetAssemblyResolver nugetResolver)
{
    private const string SkeletonDocumentPath = Compiler.CompileDiagnostics.SkeletonDiagnosticsPath;

    /// <summary>
    /// Document path of the generated <c>global using</c> scope. Like the skeleton it is framework
    /// output the user cannot edit, so its own diagnostics are filtered out of the result.
    /// </summary>
    internal const string GlobalUsingsDocumentPath = Compiler.CompileDiagnostics.GlobalUsingsDiagnosticsPath;

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

        // Resolve the EFFECTIVE source set first (proposed body substituted), because the import
        // scope below is derived from it: a `using` the user just typed in Monaco has to count.
        var effectiveSources = new List<(string Path, string Code)>(inputs.Sources.Length + 1);
        var substituted = false;
        foreach (var (path, code) in inputs.Sources)
        {
            if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                effectiveSources.Add((path, cleanedProposed));
                substituted = true;
            }
            else
            {
                effectiveSources.Add((path, code));
            }
        }

        if (!substituted)
        {
            // Proposed source path doesn't match any existing source — treat as a new file.
            effectiveSources.Add((sourcePath, cleanedProposed));
        }

        var trees = new List<SyntaxTree>(effectiveSources.Count + 2)
        {
            CSharpSyntaxTree.ParseText(
                SourceText.From(inputs.SkeletonSource),
                inputs.ParseOptions,
                path: SkeletonDocumentPath),
            // 🚨 Not optional. Each source below is its OWN tree (so a diagnostic carries the
            // MeshNode path the user edits), and a C# `using` is FILE-SCOPED — so without this
            // document the skeleton's imports cover nothing and every source that relies on them
            // reports phantom CS0246/CS0308 while the emit path compiles it cleanly (#1802).
            // Recomputed here, not taken from `inputs`, so the proposed body's own usings apply.
            CSharpSyntaxTree.ParseText(
                SourceText.From(new DynamicMeshNodeAttributeGenerator()
                    .GenerateGlobalUsingsSource(effectiveSources.Select(s => (string?)s.Code))),
                inputs.ParseOptions,
                path: GlobalUsingsDocumentPath),
        };

        foreach (var (path, code) in effectiveSources)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(code), inputs.ParseOptions, path: path));
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
            // Skeleton- and global-usings-tree diagnostics are framework noise — the user can't
            // act on them. (A CS8019 "unnecessary using" on the generated scope is expected: it
            // imports for the whole compilation, so most files use only part of it.)
            var treePath = d.Location.SourceTree?.FilePath;
            if (treePath == SkeletonDocumentPath || treePath == GlobalUsingsDocumentPath) continue;
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
