namespace MeshWeaver.Layout;

/// <summary>
/// Represents a markdown control with customizable properties.
/// </summary>
/// <param name="Markdown">The data associated with the markdown control.</param>
public record MarkdownControl(object Markdown)
    : UiControl<MarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>File extension used for markdown content nodes (<c>.md</c>).</summary>
    public const string Extension = ".md";

    /// <summary>
    /// Pre-rendered HTML content. If set, will be used instead of parsing Markdown.
    /// </summary>
    public object? Html { get; init; }

    /// <summary>
    /// Node path used to resolve RELATIVE <c>@@</c> area/content embeds in the markdown
    /// (e.g. <c>@@("area:Search")</c> → <c>{NodePath}/area:Search</c>). When unset,
    /// <c>MarkdownView</c> falls back to the bound stream's owner. Set it when the markdown
    /// is a child control whose stream owner may not be the authoring node — e.g. a Space's
    /// body markdown rendered inside the Overview area, where relying on the stream owner is
    /// unreliable and the relative embed would otherwise fail to resolve.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>
    /// Code submissions to execute in the kernel for interactive markdown.
    /// These are extracted from code blocks with --render or --execute flags.
    /// Actual type is IReadOnlyCollection&lt;MeshWeaver.Kernel.SubmitCodeRequest&gt;.
    /// </summary>
    public object? CodeSubmissions { get; init; }

    /// <summary>
    /// Whether to show the References section below the rendered markdown.
    /// Defaults to true.
    /// </summary>
    public object ShowReferences { get; init; } = true;
}
