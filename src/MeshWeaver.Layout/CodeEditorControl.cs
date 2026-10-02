using MeshWeaver.Data;

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

    /// <summary>
    /// Opt-in AUTO-SAVE: the path of the Code MeshNode this editor edits IN PLACE. When set,
    /// the renderer persists the (debounced) editor text into that node's
    /// <c>CodeConfiguration.Code</c> through the process-wide <c>IMeshNodeStreamCache</c> —
    /// the same circuit-side seam the markdown editor's auto-save uses
    /// (<see cref="MarkdownEditorControl.AutoSaveAddress"/>), so the write carries the
    /// viewer's own identity and every reader on the path observes the patch in order.
    /// Null = no auto-save (default; the classic bind-and-Save flow).
    ///
    /// <para>🚨 An editor has ONE write target. Auto-save and a node-bound <see cref="Value"/>
    /// (<see cref="BindToNode"/>) are mutually exclusive: the first writes
    /// <c>CodeConfiguration.Code</c>, the second writes the bound field, and a control carrying
    /// both would write the same keystroke twice. The builders keep the combination out —
    /// <see cref="BindToNode"/> clears this address, <see cref="WithAutoSave"/> refuses a
    /// node-bound editor — so never set both through an initializer or a <c>with</c>.</para>
    /// </summary>
    public string? AutoSaveAddress { get; init; }

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
    /// <summary>Returns a copy that AUTO-SAVES the (debounced) editor text into the Code node at
    /// <paramref name="nodePath"/> — see <see cref="AutoSaveAddress"/>.</summary>
    /// <param name="nodePath">Path of the Code MeshNode this editor edits in place.</param>
    /// <returns>A new instance with the updated AutoSaveAddress.</returns>
    /// <exception cref="InvalidOperationException">The editor is already bound to a node field
    /// (<see cref="BindToNode"/>): that binding IS its write path, and an auto-save address would be a
    /// second, competing one. Dropping the binding instead would leave the editor with no text to
    /// show, so the combination is refused rather than resolved.</exception>
    public CodeEditorControl WithAutoSave(string nodePath) =>
        IsBoundToNodeField
            ? throw new InvalidOperationException(
                $"This {nameof(CodeEditorControl)} is bound to a node field ({nameof(BindToNode)}), which already "
                + $"writes every edit to that field. {nameof(WithAutoSave)} would add a second write target "
                + $"(CodeConfiguration.Code of '{nodePath}'). Use one or the other.")
            : this with { AutoSaveAddress = nodePath };

    /// <summary>True when <see cref="Value"/> is a relative pointer under a node-bound DataContext —
    /// the shape <see cref="BindToNode"/> produces and the renderers write back through.</summary>
    private bool IsBoundToNodeField =>
        Value is JsonPointerReference pointer
        && !pointer.Pointer.StartsWith('/')
        && LayoutAreaReference.TryParseMeshNodeDataContext(DataContext) is not null;

    /// <summary>
    /// Returns a copy whose text is BOUND to one field of the node at <paramref name="nodePath"/> —
    /// the code-editor counterpart of the node-bound form controls (Doc/GUI/DataBinding → "Binding a
    /// rich control to a node field"). <see cref="Value"/> becomes the relative pointer
    /// <paramref name="field"/> and the control's DataContext the node-bound context
    /// (<see cref="LayoutAreaReference.GetMeshNodeDataContext"/>): the renderer reads the field live
    /// off the node stream and writes every edit straight back to that ONE field. The producing area
    /// renders the editor at once and never loads the node — no <c>/data</c> copy, no Save button,
    /// no save subscription.
    ///
    /// <para>The binding is the editor's ONE write target: an <see cref="AutoSaveAddress"/> set
    /// earlier is cleared (it would write <c>CodeConfiguration.Code</c> on top of the bound field).</para>
    /// </summary>
    /// <param name="nodePath">Path of the node whose field the editor edits.</param>
    /// <param name="field">A RELATIVE JSON pointer to the field, resolved against the node's
    /// <c>Content</c> (or against the whole node when <paramref name="bindContent"/> is <c>false</c>):
    /// a property name such as <c>"instructions"</c>, or a <c>/</c>-separated path to a nested one
    /// (<c>"review/notes"</c>). Because it is pointer syntax, a property name that itself contains
    /// <c>/</c> or <c>~</c> is written escaped (<c>~1</c>, <c>~0</c>, RFC 6901). Each segment is
    /// resolved case-insensitively. It must not start with <c>/</c> — an absolute pointer reads the
    /// layout area's data, not the node.</param>
    /// <param name="bindContent"><c>true</c> (default) resolves <paramref name="field"/> against the
    /// node's <c>Content</c>; <c>false</c> against the node's top-level fields
    /// (<c>Description</c>, <c>Name</c>, …).</param>
    /// <returns>A new instance bound to the node field.</returns>
    /// <exception cref="ArgumentException"><paramref name="field"/> is empty or absolute.</exception>
    public CodeEditorControl BindToNode(string nodePath, string field, bool bindContent = true) =>
        this with
        {
            Value = NodeFieldPointer.Relative(field, nameof(field)),
            DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent),
            AutoSaveAddress = null,
        };
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
