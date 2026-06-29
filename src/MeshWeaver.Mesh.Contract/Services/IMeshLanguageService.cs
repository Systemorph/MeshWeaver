namespace MeshWeaver.Mesh.Services.LanguageServer;

/// <summary>
/// Language services over the live <c>CSharpCompilation</c> of a NodeType — diagnostics,
/// hover (QuickInfo), code completion, and speculative pre-flight checks for a proposed
/// source-file substitution.
///
/// <para>
/// Stage 1 of LSP integration (see <c>Doc/Architecture/LanguageServices.md</c>). Backed
/// in-process by Roslyn against the cached compilation held by
/// <c>IMeshNodeCompilationService</c>. A future Stage 2 will add a sibling interface
/// (or extend this one) for the on-disk repo workspace via the Roslyn LSP child-process.
/// </para>
///
/// <para>
/// 100% reactive — every method returns <see cref="IObservable{T}"/>. Compose with
/// <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>. Never bridge to <c>Task</c>
/// or <c>await</c> from hub-reachable code — that deadlocks the hub action block. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c>. Tests bridge at their own edge with
/// <c>.FirstAsync().ToTask(ct)</c>.
/// </para>
/// </summary>
public interface IMeshLanguageService
{
    /// <summary>
    /// Emits the current cached compilation's diagnostics for the NodeType. Distinct from
    /// <c>IMeshNodeCompilationService.CompileAndGetConfigurations</c> which only surfaces
    /// the compile <i>status</i> — this enumerates every <see cref="DiagnosticInfo"/>
    /// (errors + warnings + info) with source location, so a consumer can show squiggles.
    /// </summary>
    IObservable<IReadOnlyList<DiagnosticInfo>> GetDiagnostics(string nodeTypePath);

    /// <summary>
    /// Emits the QuickInfo (signature + XML doc summary) at the given position, or
    /// <c>null</c> if no symbol resolves there.
    /// </summary>
    /// <param name="nodeTypePath">Path of the NodeType whose compilation hosts the source.</param>
    /// <param name="sourcePath">Path of the Code MeshNode within the NodeType's sources.</param>
    /// <param name="position">0-based line/character — LSP convention.</param>
    IObservable<HoverInfo?> GetHover(string nodeTypePath, string sourcePath, SourcePosition position);

    /// <summary>
    /// Emits up to <paramref name="maxResults"/> completion entries at the given position.
    /// </summary>
    IObservable<IReadOnlyList<CompletionEntry>> GetCompletions(
        string nodeTypePath,
        string sourcePath,
        SourcePosition position,
        int maxResults = 20);

    /// <summary>
    /// Speculative pre-flight check: rebuild the NodeType's compilation with one source
    /// file replaced by <paramref name="proposedCode"/>, return all diagnostics. The
    /// substitute file is identified by <paramref name="sourcePath"/> — if no source at
    /// that path exists today, the proposed code is added as a new file.
    /// <para>
    /// Full substitution (not single-file isolation) — catches the dominant Coder
    /// failure mode where editing one source file breaks a sibling. Reuses the cached
    /// <c>MetadataReference</c> set so per-check cost is just parse + bind + diagnose
    /// (~200–500ms for typical NodeTypes; no NuGet resolution, no emit).
    /// </para>
    /// </summary>
    IObservable<IReadOnlyList<DiagnosticInfo>> CheckSpeculative(
        string nodeTypePath,
        string sourcePath,
        string proposedCode);
}

/// <summary>0-based line/character position. LSP convention (Monaco is 1-based for lines — converted at the boundary).</summary>
public readonly record struct SourcePosition(int Line, int Character);

/// <summary>Half-open range — Start inclusive, End exclusive.</summary>
public readonly record struct SourceRange(SourcePosition Start, SourcePosition End);

/// <summary>A pointer to a span of source text inside a Code MeshNode.</summary>
public sealed record SourceLocation(string SourcePath, SourceRange Range);

/// <summary>One Roslyn diagnostic, flattened to plain data — no Roslyn types leak through this interface.</summary>
public sealed record DiagnosticInfo(
    string Id,
    DiagnosticSeverity Severity,
    string Message,
    SourceLocation? Location);

/// <summary>Diagnostic severity. Values match Roslyn's <c>Microsoft.CodeAnalysis.DiagnosticSeverity</c> for direct mapping.</summary>
#pragma warning disable CS1591 // standard Roslyn-spec names; self-documenting
public enum DiagnosticSeverity
{
    Hidden = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}
#pragma warning restore CS1591

/// <summary>QuickInfo / hover content. <see cref="ContentMarkdown"/> is the formatted markdown body Monaco renders.</summary>
public sealed record HoverInfo(string ContentMarkdown, SourceRange? Range);

/// <summary>One code-completion suggestion.</summary>
public sealed record CompletionEntry(
    string Label,
    CompletionKind Kind,
    string InsertText,
    string? Detail = null,
    string? Documentation = null,
    string? SortText = null);

/// <summary>
/// Completion-item kind. Values match the LSP wire protocol so they can be passed through to
/// Monaco / LSP clients unchanged. Names also match Roslyn's <c>WellKnownTags</c> categories
/// closely enough to map without a translation table.
/// </summary>
#pragma warning disable CS1591 // standard LSP-spec names; self-documenting
public enum CompletionKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}
#pragma warning restore CS1591
