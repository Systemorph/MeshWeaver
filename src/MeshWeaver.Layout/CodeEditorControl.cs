namespace MeshWeaver.Layout;

/// <summary>
/// A control that wraps the Monaco code editor.
/// </summary>
public record CodeEditorControl() : UiControl<CodeEditorControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The initial value/content of the editor.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The language for syntax highlighting (e.g., "javascript", "csharp", "markdown").
    /// </summary>
    public object? Language { get; init; }

    /// <summary>
    /// The theme of the editor (e.g., "vs", "vs-dark", "hc-black").
    /// </summary>
    public object? Theme { get; init; }

    /// <summary>
    /// Whether the editor is read-only.
    /// </summary>
    public new object? Readonly { get; init; }

    /// <summary>
    /// The height of the editor (e.g., "300px", "100%").
    /// </summary>
    public object? Height { get; init; }

    /// <summary>
    /// Whether to show line numbers.
    /// </summary>
    public object? LineNumbers { get; init; }

    /// <summary>
    /// Whether to enable minimap.
    /// </summary>
    public object? Minimap { get; init; }

    /// <summary>
    /// Whether to enable word wrap.
    /// </summary>
    public object? WordWrap { get; init; }

    /// <summary>
    /// Placeholder text when editor is empty.
    /// </summary>
    public object? Placeholder { get; init; }

    /// <summary>
    /// Extra type definitions to include for autocomplete.
    /// For C#, this is additional source code that provides type information
    /// from dependencies, enabling Monaco to suggest types from other modules.
    /// </summary>
    public object? ExtraTypeDefinitions { get; init; }

    /// <summary>
    /// Opt-in: enables Roslyn-backed live diagnostics in Monaco when set. The renderer
    /// resolves <c>IMeshLanguageService</c> from DI and pushes per-keystroke (debounced)
    /// diagnostics for the substituted source via <c>CheckSpeculative</c>. Both fields
    /// reference the NodeType + source MeshNode paths the editor is bound to so the
    /// service can locate the right cached compilation. Null = no LSP wiring (default).
    /// </summary>
    public CodeEditorLanguageServerConfig? LanguageServer { get; init; }

    /// <summary>
    /// Pre-computed, STATIC diagnostics to render as Monaco markers (the IDE-style red
    /// squiggle "error overlay") the moment the editor loads — no live language-server
    /// round-trip. Used by the compile-error page to mark the exact lines a failed Roslyn
    /// compile flagged, sourced from the captured <c>NodeTypeDefinition.CompilationDiagnostics</c>.
    /// Distinct from <see cref="LanguageServer"/> (which re-derives diagnostics live as the
    /// user types). When both are set, <see cref="LanguageServer"/> wins. Null = no markers.
    /// </summary>
    public IReadOnlyList<CodeEditorDiagnostic>? Diagnostics { get; init; }

    /// <summary>Returns a copy with <paramref name="value"/> as its initial editor content.</summary>
    /// <param name="value">The initial text content of the editor.</param>
    /// <returns>A new instance with the updated Value.</returns>
    public CodeEditorControl WithValue(string value) => this with { Value = value };
    /// <summary>Returns a copy with <paramref name="language"/> as the syntax-highlighting language.</summary>
    /// <param name="language">The Monaco language id, e.g. "csharp", "javascript", "markdown".</param>
    /// <returns>A new instance with the updated Language.</returns>
    public CodeEditorControl WithLanguage(string language) => this with { Language = language };
    /// <summary>Returns a copy with <paramref name="theme"/> as the Monaco editor theme.</summary>
    /// <param name="theme">The theme id, e.g. "vs", "vs-dark", "hc-black".</param>
    /// <returns>A new instance with the updated Theme.</returns>
    public CodeEditorControl WithTheme(string theme) => this with { Theme = theme };
    /// <summary>Returns a copy with the read-only flag set to <paramref name="readonly"/>.</summary>
    /// <param name="readonly">True to make the editor read-only; false to allow editing.</param>
    /// <returns>A new instance with the updated Readonly setting.</returns>
    public CodeEditorControl WithReadonly(bool @readonly) => this with { Readonly = @readonly };
    /// <summary>Returns a copy with <paramref name="height"/> as the editor height CSS value.</summary>
    /// <param name="height">A CSS height string, e.g. "300px" or "100%".</param>
    /// <returns>A new instance with the updated Height.</returns>
    public CodeEditorControl WithHeight(string height) => this with { Height = height };
    /// <summary>Returns a copy with line-number visibility set to <paramref name="show"/>.</summary>
    /// <param name="show">True to show line numbers; false to hide them.</param>
    /// <returns>A new instance with the updated LineNumbers setting.</returns>
    public CodeEditorControl WithLineNumbers(bool show) => this with { LineNumbers = show };
    /// <summary>Returns a copy with the minimap enabled or disabled per <paramref name="enabled"/>.</summary>
    /// <param name="enabled">True to show the minimap; false to hide it.</param>
    /// <returns>A new instance with the updated Minimap setting.</returns>
    public CodeEditorControl WithMinimap(bool enabled) => this with { Minimap = enabled };
    /// <summary>Returns a copy with word-wrap enabled or disabled per <paramref name="enabled"/>.</summary>
    /// <param name="enabled">True to enable word-wrap; false to disable it.</param>
    /// <returns>A new instance with the updated WordWrap setting.</returns>
    public CodeEditorControl WithWordWrap(bool enabled) => this with { WordWrap = enabled };
    /// <summary>Returns a copy with <paramref name="placeholder"/> as the empty-editor placeholder text.</summary>
    /// <param name="placeholder">The placeholder text shown when the editor is empty.</param>
    /// <returns>A new instance with the updated Placeholder.</returns>
    public CodeEditorControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    /// <summary>Returns a copy with <paramref name="definitions"/> as extra type definitions for Monaco autocomplete.</summary>
    /// <param name="definitions">Additional source code (e.g. type stubs) to include in the language service context.</param>
    /// <returns>A new instance with the updated ExtraTypeDefinitions.</returns>
    public CodeEditorControl WithExtraTypeDefinitions(string definitions) => this with { ExtraTypeDefinitions = definitions };
    /// <summary>
    /// Returns a copy with Roslyn-backed live diagnostics enabled, using the specified node type and source paths.
    /// </summary>
    /// <param name="nodeTypePath">Path of the NodeType whose compilation hosts the source.</param>
    /// <param name="sourcePath">Path of the Code MeshNode being edited.</param>
    /// <returns>A new instance with the updated LanguageServer configuration.</returns>
    public CodeEditorControl WithLanguageServer(string nodeTypePath, string sourcePath) =>
        this with { LanguageServer = new CodeEditorLanguageServerConfig(nodeTypePath, sourcePath) };
    /// <summary>Returns a copy with <paramref name="diagnostics"/> as the static diagnostic markers.</summary>
    /// <param name="diagnostics">Pre-computed diagnostic markers to render as Monaco squiggles on load.</param>
    /// <returns>A new instance with the updated Diagnostics.</returns>
    public CodeEditorControl WithDiagnostics(IReadOnlyList<CodeEditorDiagnostic> diagnostics) =>
        this with { Diagnostics = diagnostics };
}

/// <summary>
/// A single static diagnostic marker for <see cref="CodeEditorControl.Diagnostics"/> — a flat,
/// serializable shape (no dependency on the language-server contract, which lives in a
/// higher-level assembly than <c>MeshWeaver.Layout</c>). Line/character are 0-based (LSP
/// convention; the Monaco JS adds 1). <paramref name="Severity"/> matches the LSP
/// <c>DiagnosticSeverity</c> ordinal (0=Hidden, 1=Info, 2=Warning, 3=Error).
/// </summary>
public sealed record CodeEditorDiagnostic(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    int Severity,
    string Message,
    string? Code);

/// <summary>
/// Opt-in configuration for Roslyn-backed live diagnostics in <see cref="CodeEditorControl"/>.
/// </summary>
/// <param name="NodeTypePath">Path of the NodeType whose <c>CSharpCompilation</c> hosts the source (e.g. <c>type/MyType</c>).</param>
/// <param name="SourcePath">Path of the Code MeshNode being edited (e.g. <c>type/MyType/Source/MyType.cs</c>).</param>
public sealed record CodeEditorLanguageServerConfig(string NodeTypePath, string SourcePath);
