using System.Collections.Immutable;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout
{
    public record LayoutAreaProperties
    {
        public const string Properties = nameof(Properties);
        public string PageTitle { get; init; }
        public string Heading { get; init; }
        public ImmutableList<object> HeadingMenu { get; init; } = ImmutableList<object>.Empty;

        internal ImmutableList<NavLinkControl> MenuControls { get; init; } = ImmutableList<NavLinkControl>.Empty;

        internal int MenuOrder { get; init; }
        public LayoutAreaProperties WithMenuOrder(int order) => this with { MenuOrder = order };


        public LayoutAreaProperties WithMenu(params NavLinkControl[] items)
            => this with { MenuControls = MenuControls.AddRange(items) };


    }
}
