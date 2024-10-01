namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a text field control with customizable properties.
    /// </summary>
    /// <param name="Data">The data associated with the text field control.</param>
    public record TextFieldControl(object Data)
        : InputBaseControl<TextFieldControl>(Data), IInputControl
    {
        /// <summary>
        /// Gets or initializes the start icon of the text field control.
        /// </summary>
        public object IconStart { get; init; }

        /// <summary>
        /// Gets or initializes the end icon of the text field control.
        /// </summary>
        public object IconEnd { get; init; }

        /// <summary>
        /// Gets or initializes the autocomplete attribute of the text field control.
        /// </summary>
        public object Autocomplete { get; init; }

        /// <summary>
        /// Sets the start icon of the text field control.
        /// </summary>
        /// <param name="icon">The start icon to set.</param>
        /// <returns>A new <see cref="TextFieldControl"/> instance with the specified start icon.</returns>
        public TextFieldControl WithIconStart(object icon) => this with { IconStart = icon };

        /// <summary>
        /// Sets the end icon of the text field control.
        /// </summary>
        /// <param name="icon">The end icon to set.</param>
        /// <returns>A new <see cref="TextFieldControl"/> instance with the specified end icon.</returns>
        public TextFieldControl WithIconEnd(object icon) => this with { IconEnd = icon };

        /// <summary>
        /// Sets the autocomplete attribute of the text field control.
        /// </summary>
        /// <param name="autocomplete">The autocomplete attribute to set.</param>
        /// <returns>A new <see cref="TextFieldControl"/> instance with the specified autocomplete attribute.</returns>
        public TextFieldControl WithAutocomplete(object autocomplete) => this with { Autocomplete = autocomplete };
    }
}