namespace MeshWeaver.Layout;

/// <summary>
/// Represents a form component with customizable properties.
/// </summary>
public interface IFormControl : IUiControl
{
    /// <summary>
    /// Gets or initializes the data associated with the form component.
    /// </summary>
    object Data { get; init; }

    /// <summary>
    /// Label of the form component.
    /// </summary>
    object? Label { get; init; }

    /// <summary>
    /// Sets the label of the form component.
    /// </summary>
    /// <param name="label">The label to set.</param>
    /// <returns>A new instance of the form control with the specified label.</returns>
    IFormControl WithLabel(object label);

    /// <summary>
    /// Whether the form control is disabled.
    /// </summary>
    object? Disabled { get; init; }

    /// <summary>
    /// Whether the form control is required.
    /// </summary>
    object? Required { get; init; }

    /// <summary>
    /// Whether the form control should auto-focus.
    /// </summary>
    object? AutoFocus { get; init; }

    /// <summary>
    /// Gets or initializes the immediate update state of the input control.
    /// </summary>
    object? Immediate { get; init; }

    /// <summary>
    /// Gets or initializes the delay for immediate updates of the input control.
    /// </summary>
    object? ImmediateDelay { get; init; }

    /// <summary>
    /// Gets or initializes the start icon of the text field control.
    /// </summary>
    object? IconStart { get; init; }

    /// <summary>
    /// Gets or initializes the end icon of the text field control.
    /// </summary>
    object? IconEnd { get; init; }

    /// <summary>
    /// Placeholder to be put in the control.
    /// </summary>
    object? Placeholder { get; init; }
}
