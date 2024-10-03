namespace MeshWeaver.Layout;

/// <summary>
/// Represents a date-time control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the date-time control.</param>
public record DateTimeControl(object Data) : UiControl<DateTimeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IFormComponent
{
    /// <summary>
    /// Gets or initializes the minimum value for the date-time control.
    /// </summary>
    public object Min { get; init; }

    /// <summary>
    /// Gets or initializes the maximum value for the date-time control.
    /// </summary>
    public object Max { get; init; }

    /// <summary>
    /// Gets or initializes the step value for the date-time control.
    /// </summary>
    public object Step { get; init; }

    /// <summary>
    /// Gets or initializes the state to hide the step value for the date-time control.
    /// </summary>
    public object HideStep { get; init; }

    /// <summary>
    /// Gets or initializes the data list for the date-time control.
    /// </summary>
    public object DataList { get; init; }

    /// <summary>
    /// Gets or initializes the maximum length for the date-time control.
    /// </summary>
    public object MaxLength { get; init; }

    /// <summary>
    /// Gets or initializes the minimum length for the date-time control.
    /// </summary>
    public object MinLength { get; init; }

    /// <summary>
    /// Gets or initializes the size of the date-time control.
    /// </summary>
    public object Size { get; init; }

    /// <summary>
    /// Gets or initializes the appearance of the date-time control.
    /// </summary>
    public object Appearance { get; init; }

    /// <summary>
    /// Gets or initializes the parsing error message for the date-time control.
    /// </summary>
    public object ParsingErrorMessage { get; init; }
}
