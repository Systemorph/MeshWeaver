namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents the skin for a card control with customizable properties.
    /// </summary>
    /// <remarks>
    /// For more information, visit the 
    /// <a href="https://www.fluentui-blazor.net/card">Fluent UI Blazor Card documentation</a>.
    /// </remarks>
    public record CardSkin : Skin<CardSkin>
    {
        /// <summary>
        /// Gets or initializes the area restriction state of the card.
        /// </summary>
        public object AreaRestricted { get; init; }

        /// <summary>
        /// Gets or initializes the height of the card.
        /// </summary>
        public object Height { get; init; }

        /// <summary>
        /// Gets or initializes the width of the card.
        /// </summary>
        public object Width { get; init; }

        /// <summary>
        /// Gets or initializes the minimal style state of the card.
        /// </summary>
        public object MinimalStyle { get; init; }

        /// <summary>
        /// Sets the area restriction state of the card.
        /// </summary>
        /// <param name="areaRestricted">The area restriction state to set.</param>
        /// <returns>A new <see cref="CardSkin"/> instance with the specified area restriction state.</returns>
        public CardSkin WithAreaRestricted(object areaRestricted) => This with { AreaRestricted = areaRestricted };

        /// <summary>
        /// Sets the height of the card.
        /// </summary>
        /// <param name="height">The height to set.</param>
        /// <returns>A new <see cref="CardSkin"/> instance with the specified height.</returns>
        public CardSkin WithHeight(object height) => This with { Height = height };

        /// <summary>
        /// Sets the width of the card.
        /// </summary>
        /// <param name="width">The width to set.</param>
        /// <returns>A new <see cref="CardSkin"/> instance with the specified width.</returns>
        public CardSkin WithWidth(object width) => This with { Width = width };

        /// <summary>
        /// Sets the minimal style state of the card.
        /// </summary>
        /// <returns>A new <see cref="CardSkin"/> instance with the minimal style state set to true.</returns>
        public CardSkin WithMinimalStyle() => This with { MinimalStyle = true };
    }
}
