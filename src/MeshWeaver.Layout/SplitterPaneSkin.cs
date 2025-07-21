namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents the skin for a splitter pane with customizable properties.
    /// </summary>
    public record SplitterPaneSkin : Skin<SplitterPaneSkin>
    {
        /// <summary>
        /// Gets or initializes a value indicating whether the pane is collapsible.
        /// </summary>
        public object? Collapsible { get; init; }

        /// <summary>
        /// Gets or initializes a value indicating whether the pane is collapsed.
        /// </summary>
        public object? Collapsed { get; init; }

        /// <summary>
        /// Gets or initializes the maximum size of the pane.
        /// </summary>
        public object? Max { get; init; }

        /// <summary>
        /// Gets or initializes the minimum size of the pane.
        /// </summary>
        public object? Min { get; init; }

        /// <summary>
        /// Gets or initializes a value indicating whether the pane is resizable.
        /// </summary>
        public object? Resizable { get; init; } = true;

        /// <summary>
        /// Gets or initializes the size of the pane.
        /// </summary>
        public object? Size { get; init; }

        /// <summary>
        /// Sets the collapsible state of the pane.
        /// </summary>
        /// <param name="collapsible">The collapsible state to set.</param>
        /// <returns>A new <see cref="SplitterPaneSkin"/> instance with the specified collapsible state.</returns>
        public SplitterPaneSkin WithCollapsible(object? collapsible) => this with { Collapsible = collapsible };

        /// <summary>
        /// Sets the collapsed state of the pane.
        /// </summary>
        /// <param name="collapsed">The collapsed state to set.</param>
        /// <returns>A new <see cref="SplitterPaneSkin"/> instance with the specified collapsed state.</returns>
        public SplitterPaneSkin WithCollapsed(object? collapsed) => this with { Collapsed = collapsed };

        /// <summary>
        /// Sets the maximum size of the pane.
        /// </summary>
        /// <param name="max">The maximum size to set.</param>
        /// <returns>A new <see cref="SplitterPaneSkin"/> instance with the specified maximum size.</returns>
        public SplitterPaneSkin WithMax(object? max) => this with { Max = max };

        /// <summary>
        /// Sets the minimum size of the pane.
        /// </summary>
        /// <param name="min">The minimum size to set.</param>
        /// <returns>A new <see cref="SplitterPaneSkin"/> instance with the specified minimum size.</returns>
        public SplitterPaneSkin WithMin(object? min) => this with { Min = min };

        /// <summary>
        /// Sets the resizable state of the pane.
        /// </summary>
        /// <param name="resizable">The resizable state to set.</param>
        /// <returns>A new <see cref="SplitterPaneSkin"/> instance with the specified resizable state.</returns>
        public SplitterPaneSkin WithResizable(object? resizable) => this with { Resizable = resizable };

        /// <summary>
        /// Sets the size of the pane.
        /// </summary>
        /// <param name="size">The size to set.</param>
        /// <returns>A new <see cref="SplitterPaneSkin"/> instance with the specified size.</returns>
        public SplitterPaneSkin WithSize(object? size) => this with { Size = size };
    }
}
