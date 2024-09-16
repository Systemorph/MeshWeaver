namespace MeshWeaver.Layout
{
    /// <summary>
    /// Control representing a progress bar
    /// </summary>
    /// <param name="Message">String message</param>
    /// <param name="Progress">Between 0 and 100</param>
    public record ProgressControl(object Message, object Progress)
        : UiControl<ProgressControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
}
