using System.Collections.Immutable;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents the properties of a layout area.
    /// </summary>
    public record LayoutAreaProperties
    {
        /// <summary>
        /// The name of the properties.
        /// </summary>
        public const string Properties = nameof(Properties);

        /// <summary>
        /// Gets or initializes the page title of the layout area.
        /// </summary>
        public string PageTitle { get; init; }

        /// <summary>
        /// Gets or initializes the heading of the layout area.
        /// </summary>
        public string Heading { get; init; }

        /// <summary>
        /// Gets or initializes the heading menu items of the layout area.
        /// </summary>
        public ImmutableList<object> HeadingMenu { get; init; } = ImmutableList<object>.Empty;

        /// <summary>
        /// Gets or initializes the menu controls of the layout area.
        /// </summary>
        internal ImmutableList<NavLinkControl> MenuControls { get; init; } = ImmutableList<NavLinkControl>.Empty;

        /// <summary>
        /// Gets or initializes the menu order of the layout area.
        /// </summary>
        internal int MenuOrder { get; init; }

        /// <summary>
        /// Sets the menu order of the layout area.
        /// </summary>
        /// <param name="order">The menu order to set.</param>
        /// <returns>A new <see cref="LayoutAreaProperties"/> instance with the specified menu order.</returns>
        public LayoutAreaProperties WithMenuOrder(int order) => this with { MenuOrder = order };

        /// <summary>
        /// Sets the menu controls of the layout area.
        /// </summary>
        /// <param name="items">The menu controls to set.</param>
        /// <returns>A new <see cref="LayoutAreaProperties"/> instance with the specified menu controls.</returns>
        public LayoutAreaProperties WithMenu(params NavLinkControl[] items)
            => this with { MenuControls = MenuControls.AddRange(items) };
    }
}
