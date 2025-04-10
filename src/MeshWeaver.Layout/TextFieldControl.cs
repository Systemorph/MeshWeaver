namespace MeshWeaver.Layout;

/// <summary>
/// Represents a text field control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the text field control.</param>
public record 
    TextFieldControl(object Data)
    : InputFormControlBase<TextFieldControl>(Data)
{

    /// <summary>
    /// Gets or initializes the autocomplete attribute of the text field control.
    /// </summary>
    public object Autocomplete { get; init; }


    /// <summary>
    /// Sets the autocomplete attribute of the text field control.
    /// </summary>
    /// <param name="autocomplete">The autocomplete attribute to set.</param>
    /// <returns>A new <see cref="TextFieldControl"/> instance with the specified autocomplete attribute.</returns>
    public TextFieldControl WithAutocomplete(object autocomplete) => this with { Autocomplete = autocomplete };
}
