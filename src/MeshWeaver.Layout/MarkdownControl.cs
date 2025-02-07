
namespace MeshWeaver.Layout;

/// <summary>
/// Represents a markdown control with customizable properties.
/// </summary>
/// <param name="Markdown">The data associated with the markdown control.</param>
public record MarkdownControl(object Markdown)
    : UiControl<MarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public const string Extension = ".md";
    public object Html { get; init; }
}
