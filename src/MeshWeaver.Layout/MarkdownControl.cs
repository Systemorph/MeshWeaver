namespace MeshWeaver.Layout;

/// <summary>
/// Represents a markdown control with customizable properties.
/// </summary>
/// <param name="Markdown">The data associated with the markdown control.</param>
public record MarkdownControl(object Markdown)
    : UiControl<MarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public const string Extension = ".md";

    /// <summary>
    /// Pre-rendered HTML content. If set, will be used instead of parsing Markdown.
    /// </summary>
    public object? Html { get; init; }

    /// <summary>
    /// Code submissions to execute in the kernel for interactive markdown.
    /// These are extracted from code blocks with --render or --execute flags.
    /// Actual type is IReadOnlyCollection&lt;MeshWeaver.Kernel.SubmitCodeRequest&gt;.
    /// </summary>
    public object? CodeSubmissions { get; init; }
}
