namespace MeshWeaver.Layout;

/// <summary>
/// Represents a number field control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the number field control.</param>
/// <param name="Type">The type of the number field control.</param>
public record NumberFieldControl(object Data, object Type)
    : FormControlBase<NumberFieldControl>(Data), IFormControl
{
    /// <summary>
    /// Gets or initializes the state to hide the step value for the number field control.
    /// </summary>
    public object HideStep { get; init; }

    /// <summary>
    /// Gets or initializes the data list for the number field control.
    /// </summary>
    public object DataList { get; init; }

    /// <summary>
    /// Gets or initializes the maximum length for the number field control.
    /// </summary>
    public object MaxLength { get; init; }

    /// <summary>
    /// Gets or initializes the minimum length for the number field control.
    /// </summary>
    public object MinLength { get; init; }

    /// <summary>
    /// Gets or initializes the size of the number field control.
    /// </summary>
    public object Size { get; init; }

    /// <summary>
    /// Gets or initializes the step value for the number field control.
    /// </summary>
    public object Step { get; init; }

    /// <summary>
    /// Gets or initializes the minimum value for the number field control.
    /// </summary>
    public object Min { get; init; }

    /// <summary>
    /// Gets or initializes the maximum value for the number field control.
    /// </summary>
    public object Max { get; init; }

    /// <summary>
    /// Gets or initializes the appearance of the number field control.
    /// </summary>
    public object Appearance { get; init; }

    /// <summary>
    /// Gets or initializes the parsing error message for the number field control.
    /// </summary>
    public object ParsingErrorMessage { get; init; }
}
