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

    public CodeEditorControl WithValue(string value) => this with { Value = value };
    public CodeEditorControl WithLanguage(string language) => this with { Language = language };
    public CodeEditorControl WithTheme(string theme) => this with { Theme = theme };
    public CodeEditorControl WithReadonly(bool @readonly) => this with { Readonly = @readonly };
    public CodeEditorControl WithHeight(string height) => this with { Height = height };
    public CodeEditorControl WithLineNumbers(bool show) => this with { LineNumbers = show };
    public CodeEditorControl WithMinimap(bool enabled) => this with { Minimap = enabled };
    public CodeEditorControl WithWordWrap(bool enabled) => this with { WordWrap = enabled };
    public CodeEditorControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    public CodeEditorControl WithExtraTypeDefinitions(string definitions) => this with { ExtraTypeDefinitions = definitions };
    public CodeEditorControl WithLanguageServer(string nodeTypePath, string sourcePath) =>
        this with { LanguageServer = new CodeEditorLanguageServerConfig(nodeTypePath, sourcePath) };
}

/// <summary>
/// Opt-in configuration for Roslyn-backed live diagnostics in <see cref="CodeEditorControl"/>.
/// </summary>
/// <param name="NodeTypePath">Path of the NodeType whose <c>CSharpCompilation</c> hosts the source (e.g. <c>type/MyType</c>).</param>
/// <param name="SourcePath">Path of the Code MeshNode being edited (e.g. <c>type/MyType/Source/MyType.cs</c>).</param>
public sealed record CodeEditorLanguageServerConfig(string NodeTypePath, string SourcePath);
