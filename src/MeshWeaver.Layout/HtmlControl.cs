namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents an HTML control with customizable properties.
    /// </summary>
    /// <remarks>
    /// For more information, visit the 
    /// <a href="https://www.fluentui-blazor.net/html">Fluent UI Blazor HTML documentation</a>.
    /// </remarks>
    /// <param name="Data">The data associated with the HTML control.</param>
    public record HtmlControl(object Data)
        : UiControl<HtmlControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
}
