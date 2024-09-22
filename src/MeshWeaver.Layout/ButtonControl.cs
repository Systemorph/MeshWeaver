namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a button control with customizable properties.
    /// </summary>
    /// <remarks>
    /// For more information, visit the 
    /// <a href="https://www.fluentui-blazor.net/button">Fluent UI Blazor Button documentation</a>.
    /// </remarks>
    /// <param name="Data">The data associated with the button control.</param>
    public record ButtonControl(object Data)
        : UiControl<ButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        /// <summary>
        /// Gets or initializes the icon displayed at the start of the button.
        /// </summary>
        public object IconStart { get; init; }

        /// <summary>
        /// Gets or initializes the icon displayed at the end of the button.
        /// </summary>
        public object IconEnd { get; init; }

        /// <summary>
        /// Gets or initializes the disabled state of the button.
        /// </summary>
        public object Disabled { get; init; }

        /// <summary>
        /// Gets or sets the appearance of the button.
        /// </summary>
        public object Appearance { get; set; }

        /// <summary>
        /// Sets the icon displayed at the start of the button.
        /// </summary>
        /// <param name="icon">The icon to display at the start of the button.</param>
        /// <returns>A new <see cref="ButtonControl"/> instance with the specified start icon.</returns>
        public ButtonControl WithIconStart(object icon) => this with { IconStart = icon };

        /// <summary>
        /// Sets the icon displayed at the end of the button.
        /// </summary>
        /// <param name="icon">The icon to display at the end of the button.</param>
        /// <returns>A new <see cref="ButtonControl"/> instance with the specified end icon.</returns>
        public ButtonControl WithIconEnd(object icon) => this with { IconEnd = icon };

        /// <summary>
        /// Sets the disabled state of the button.
        /// </summary>
        /// <param name="disabled">The disabled state to set.</param>
        /// <returns>A new <see cref="ButtonControl"/> instance with the specified disabled state.</returns>
        public ButtonControl WithDisabled(object disabled) => this with { Disabled = disabled };

        /// <summary>
        /// Sets the appearance of the button.
        /// </summary>
        /// <param name="appearance">The appearance to set.</param>
        /// <returns>A new <see cref="ButtonControl"/> instance with the specified appearance.</returns>
        public ButtonControl WithAppearance(object appearance) => this with { Appearance = appearance };
    }
}