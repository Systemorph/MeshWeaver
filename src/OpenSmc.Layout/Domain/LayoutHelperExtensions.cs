using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout.Domain
{
    public static class LayoutHelperExtensions
    { 
        public static EntityStore ConfigBasedRenderer<TControl>(this LayoutAreaHost host,
            RenderingContext context,
            EntityStore store,
            string area,
            Func<TControl> factory,
            Func<TControl, RenderingContext, TControl> config)
            where TControl : UiControl
        {
            var menu = store.GetControl<TControl>(area) ?? factory();
            menu = config(menu, context);
            return host.RenderArea(context with { Area = area }, menu).Aggregate(store, (x, y) => y.Invoke(x));
        }
    }
}
