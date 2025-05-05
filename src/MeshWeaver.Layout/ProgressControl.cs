namespace MeshWeaver.Layout;

/// <summary>
/// Control representing a progress bar
/// </summary>
/// <param name="Message">String message</param>
/// <param name="Progress">Between 0 and 100</param>
public record ProgressControl(object Message, object Progress)
    : UiControl<ProgressControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object HideNumber { get; init; }
    public object Paused { get; init; }
    public object Min { get; init; }
    public object Max { get; init; }
    public object Stroke { get; init; }
    public object Width { get; init; }
    public object Color { get; init; }
    public object MessagePosition { get; init; }
}

public enum ProgressStroke
{
    Small,
    Normal,
    Large,
}

public enum MessagePosition
{
    Top,
    Bottom,
    Right
}
