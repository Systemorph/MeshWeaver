using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout
{
    public static class LayoutAreaCatalogArea
    {
        public const string LayoutAreas = "$" + nameof(LayoutAreas);

        internal static LayoutDefinition AddLayoutAreaCatalog(this LayoutDefinition layout)
            => layout.WithView(LayoutAreas, LayoutAreaCatalog);

        private static object LayoutAreaCatalog(LayoutAreaHost host, RenderingContext ctx)
        {
            var layouts = host.GetLayoutAreaDefinitions();
            return layouts.Aggregate(Controls.Stack,
                (s, l) => s.WithView(CreateAreaLayout(l)));
        }

        private static StackControl CreateAreaLayout(LayoutAreaDefinition area)
        {
            return Controls.Stack.WithView(Controls.NavLink(area.Area.Wordify(), area.Url));
        }
    }
}
