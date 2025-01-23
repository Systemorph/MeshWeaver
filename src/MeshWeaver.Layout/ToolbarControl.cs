namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a toolbar control with customizable properties.
    /// </summary>
    public record ToolbarControl()
        : ContainerControl<ToolbarControl, ToolbarSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
    {
        /// <summary>
        /// Sets the orientation of the toolbar.
        /// </summary>
        /// <param name="orientation">The orientation to set.</param>
        /// <returns>A new <see cref="ToolbarSkin"/> instance with the specified orientation.</returns>
        public ToolbarControl WithOrientation(object orientation) => this.WithSkin(skin => skin.WithOrientation(orientation));
    }

    /// <summary>
    /// Represents the skin for a toolbar control with customizable properties.
    /// </summary>
    public record ToolbarSkin : Skin<ToolbarSkin>
    {
        /// <summary>
        /// Gets or sets the orientation of the toolbar.
        /// </summary>
        public object Orientation { get; set; } = Layout.Orientation.Horizontal;

        /// <summary>
        /// Sets the orientation of the toolbar.
        /// </summary>
        /// <param name="orientation">The orientation to set.</param>
        /// <returns>A new <see cref="ToolbarSkin"/> instance with the specified orientation.</returns>
        public ToolbarSkin WithOrientation(object orientation) => this with { Orientation = orientation };
    }
}
