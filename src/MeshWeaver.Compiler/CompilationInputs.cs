using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Compiler;

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
/// <param name="GlobalUsingsSource">The compile's import scope rendered as <c>global using</c>
/// directives (<c>DynamicMeshNodeAttributeGenerator.GenerateGlobalUsingsSource</c>).
/// <para>🚨 REQUIRED for correctness, not a convenience. <see cref="Sources"/> is one tree per file
/// and a C# <c>using</c> is FILE-SCOPED, so without this document the skeleton's imports reach none
/// of the user trees and the language service reports phantom CS0246/CS0308 on source that compiles
/// and ships — a false FAIL on a cleanly loaded assembly (Systemorph/MeshWeaver#1802). Any consumer
/// that assembles a compilation or workspace from these inputs MUST include it as a document.</para></param>
internal sealed record CompilationInputs(
    string AssemblyName,
    ImmutableArray<(string Path, string Code)> Sources,
    string SkeletonSource,
    string GlobalUsingsSource,
    ImmutableArray<MetadataReference> References,
    CSharpParseOptions ParseOptions,
    CSharpCompilationOptions CompilationOptions,
    ImmutableDictionary<string, long> SourceVersions);
