namespace MeshWeaver.Layout;

/// <summary>
/// Control representing a progress bar
/// </summary>
/// <param name="Message">String message</param>
/// <param name="Progress">Between 0 and 100</param>
public record ProgressControl(object Message, object Progress)
    : UiControl<ProgressControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>When set, hides the numeric percentage label inside the progress bar.</summary>
    public object? HideNumber { get; init; }
    /// <summary>When set, renders the progress bar in a paused/inactive visual state.</summary>
    public object? Paused { get; init; }
    /// <summary>Minimum value of the progress range (e.g., 0). Null uses the renderer default.</summary>
    public object? Min { get; init; }
    /// <summary>Maximum value of the progress range (e.g., 100). Null uses the renderer default.</summary>
    public object? Max { get; init; }
    /// <summary>Thickness of the progress bar track. Accepts a <see cref="ProgressStroke"/> value or a CSS string.</summary>
    public object? Stroke { get; init; }
    /// <summary>Width of the progress bar control. Accepts a CSS size string (e.g., "200px").</summary>
    public object? Width { get; init; }
    /// <summary>Foreground color of the progress indicator. Accepts a CSS color value or token.</summary>
    public object? Color { get; init; }
    /// <summary>Position of the message text relative to the progress bar. Accepts a <see cref="Layout.MessagePosition"/> value.</summary>
    public object? MessagePosition { get; init; }

    /// <summary>Sets the width of the progress bar (CSS size string, e.g. "100%").</summary>
    public ProgressControl WithWidth(object width) => this with { Width = width };

    /// <summary>Hides (or shows) the numeric percentage label rendered next to the bar.</summary>
    public ProgressControl WithHideNumber(object hideNumber) => this with { HideNumber = hideNumber };

    /// <summary>Sets where the message text is rendered relative to the bar.</summary>
    public ProgressControl WithMessagePosition(object messagePosition) => this with { MessagePosition = messagePosition };
}

/// <summary>
/// Specifies the stroke (track thickness) of the progress bar.
/// </summary>
public enum ProgressStroke
{
    /// <summary>Thin progress bar track.</summary>
    Small,
    /// <summary>Standard progress bar track thickness.</summary>
    Normal,
    /// <summary>Thick progress bar track.</summary>
    Large,
}

/// <summary>
/// Specifies where the message text is rendered relative to the progress bar.
/// </summary>
public enum MessagePosition
{
    /// <summary>Message is rendered above the progress bar.</summary>
    Top,
    /// <summary>Message is rendered below the progress bar.</summary>
    Bottom,
    /// <summary>Message is rendered to the right of the progress bar.</summary>
    Right
}
