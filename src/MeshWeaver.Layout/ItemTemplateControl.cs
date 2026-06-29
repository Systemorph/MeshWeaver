using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Layout
{
    /// <summary>
    /// Renders a repeated template by combining a view control with a bound data collection.
    /// Each item in <paramref name="Data"/> is rendered using the <paramref name="View"/> as its template.
    /// </summary>
    /// <param name="View">The UI control used as the item template for each element in <paramref name="Data"/>.</param>
    /// <param name="Data">The data collection or data-bound reference whose items are rendered by <paramref name="View"/>.</param>
    public record ItemTemplateControl(UiControl View, object Data) :
        UiControl<ItemTemplateControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        /// <summary>The area name under which the item view is registered in the entity store during rendering.</summary>
        public static string ViewArea = nameof(View);


        /// <summary>Controls whether items are arranged horizontally or vertically.</summary>
        public Orientation? Orientation { get; init; }

        /// <summary>Controls wrapping behaviour when items overflow the primary axis (data-bound or bool value).</summary>
        public object? Wrap { get; init; }

        /// <summary>Returns a copy with <paramref name="orientation"/> as its Orientation.</summary>
        /// <param name="orientation">The layout orientation for the item list.</param>
        /// <returns>A new instance with the updated Orientation.</returns>
        public ItemTemplateControl WithOrientation(Orientation orientation) => this with { Orientation = orientation };

        /// <summary>Returns a copy with <paramref name="wrap"/> as its Wrap.</summary>
        /// <param name="wrap">A boolean or data-bound value controlling item wrapping.</param>
        /// <returns>A new instance with the updated Wrap.</returns>
        public ItemTemplateControl WithWrap(object? wrap) => this with { Wrap = wrap };
        /// <summary>
        /// Renders the view template into the entity store, then renders the item view into a child area.
        /// </summary>
        /// <param name="host">The layout area host providing rendering services.</param>
        /// <param name="context">The rendering context for this control.</param>
        /// <param name="store">The entity store accumulating rendered control state.</param>
        /// <returns>The updated store and list of pending stream updates.</returns>
        protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
        {
            var ret = base.Render(host, context, store);
            var renderedView = host.RenderArea(GetContextForArea(context, ItemTemplateControl.ViewArea), View, ret.Store);
            return renderedView with { Updates = ret.Updates.Concat(renderedView.Updates) };
        }

        /// <summary>
        /// Returns true when <paramref name="other"/> has equal View, Wrap, Orientation, and Data (using structural data equality).
        /// </summary>
        /// <param name="other">The instance to compare with.</param>
        /// <returns>True if all relevant properties are equal; otherwise false.</returns>
        public virtual bool Equals(ItemTemplateControl? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (!base.Equals(other))
                return false;
            return Equals(View, other.View)
                   && Equals(Wrap, other.Wrap)
                   && Equals(Orientation, other.Orientation)
                   && LayoutHelperExtensions.DataEquality(Data, other.Data);
        }

        /// <summary>Returns a hash code combining the base hash with View, Wrap, Orientation, and Data.</summary>
        /// <returns>A composite hash code.</returns>
        public override int GetHashCode() =>
            HashCode.Combine(base.GetHashCode(),
                Wrap,
                View,
                Orientation,
                LayoutHelperExtensions.DataHashCode(Data)
            );
    }
}
