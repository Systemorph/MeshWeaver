namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a splitter control with customizable properties.
    /// </summary>
    public record SplitterControl() :
        ContainerControlWithItemSkin<SplitterControl, SplitterSkin, SplitterPaneSkin>
        (ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new SplitterSkin())
    {
        /// <summary>
        /// Creates a new instance of <see cref="SplitterPaneSkin"/> for the specified named area.
        /// </summary>
        /// <param name="namedArea">The named area control.</param>
        /// <returns>A new instance of <see cref="SplitterPaneSkin"/>.</returns>
        protected override SplitterPaneSkin CreateItemSkin(NamedAreaControl namedArea)
            => new();
    }

    /// <summary>
    /// Represents the skin for a splitter control with customizable properties.
    /// </summary>
    public record SplitterSkin : Skin<SplitterSkin>
    {
        /// <summary>
        /// Gets or sets the width of the splitter.
        /// </summary>
        public object? Width { get; set; }

        /// <summary>
        /// Gets or sets the height of the splitter.
        /// </summary>
        public object? Height { get; set; }

        /// <summary>
        /// Gets or sets the orientation of the splitter.
        /// </summary>
        public object? Orientation { get; set; }

        /// <summary>
        /// Sets the width of the splitter.
        /// </summary>
        /// <param name="width">The width to set.</param>
        /// <returns>A new <see cref="SplitterSkin"/> instance with the specified width.</returns>
        public SplitterSkin WithWidth(object width) => this with { Width = width };

        /// <summary>
        /// Sets the height of the splitter.
        /// </summary>
        /// <param name="height">The height to set.</param>
        /// <returns>A new <see cref="SplitterSkin"/> instance with the specified height.</returns>
        public SplitterSkin WithHeight(object height) => this with { Height = height };

        /// <summary>
        /// Sets the orientation of the splitter.
        /// </summary>
        /// <param name="orientation">The orientation to set.</param>
        /// <returns>A new <see cref="SplitterSkin"/> instance with the specified orientation.</returns>
        public SplitterSkin WithOrientation(object orientation) => this with { Orientation = orientation };
    }
}
