
namespace MeshWeaver.Layout;

/// <summary>
/// Represents a markdown control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the markdown control.</param>
public record MarkdownControl(object Data)
    : UiControl<MarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public const string Extension = ".md";
}
