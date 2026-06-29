using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Everything language services (hover, completion, diagnostics, speculative compile) need
/// to assemble a Roslyn <see cref="CSharpCompilation"/> or an <c>AdhocWorkspace</c> for a
/// NodeType — already source-discovery-resolved, @@-include-resolved, and NuGet-resolved.
/// <para>
/// <see cref="Sources"/> is per-user-source-file (each becomes its own
/// <see cref="SyntaxTree"/> with the MeshNode <c>Path</c> as <c>FilePath</c>) so positions
/// in language-service queries map back to what the user edits in Monaco. Distinct from
/// the existing emit path which concatenates all sources into one tree for assembly output.
/// </para>
/// <para>
/// <see cref="SourceVersions"/> is the per-source <c>{path → MeshNode.LastModified.Ticks}</c>
/// snapshot — the cache key callers (e.g. <c>MeshNodeLanguageService</c>) use to decide
/// whether their cached workspace is still valid.
/// </para>
/// </summary>
internal sealed record CompilationInputs(
    string AssemblyName,
    ImmutableArray<(string Path, string Code)> Sources,
    string SkeletonSource,
    ImmutableArray<MetadataReference> References,
    CSharpParseOptions ParseOptions,
    CSharpCompilationOptions CompilationOptions,
    ImmutableDictionary<string, long> SourceVersions);
