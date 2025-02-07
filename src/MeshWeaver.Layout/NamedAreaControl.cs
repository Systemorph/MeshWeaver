namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a named area control with customizable properties.
    /// </summary>
    /// <param name="Area">The area associated with the named area control.</param>
    public record NamedAreaControl(object Area)
        : UiControl<NamedAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        /// <summary>
        /// Gets or initializes the progress display state of the named area control.
        /// </summary>
        public object ShowProgress { get; init; }

        /// <summary>
        /// Message to be shown while waiting for the area.
        /// </summary>
        public object ProgressMessage { get; init; }

        /// <summary>
        /// Sets the area of the named area control.
        /// </summary>
        /// <param name="area">The area to set.</param>
        /// <returns>A new <see cref="NamedAreaControl"/> instance with the specified area.</returns>
        public NamedAreaControl WithArea(object area)
            => this with { Area = area };

        /// <summary>
        /// Sets the display area of the named area control.
        /// </summary>
        /// <param name="progressMessage">The progress message to be shown while waiting for the area.</param>
        /// <returns>A new <see cref="NamedAreaControl"/> instance with the specified display area.</returns>
        public NamedAreaControl WithProgressMessage(object progressMessage)
            => this with { ProgressMessage = progressMessage };
    }
}
