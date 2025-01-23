using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Domain;

public static class StandardPageLayout
{
    public const string NavMenu = "$" + nameof(NavMenu);
    public const string ContextMenu = "$" + nameof(ContextMenu);
    public const string Toolbar = "$" + nameof(Toolbar);

    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        Func<NavMenuControl, LayoutAreaHost, RenderingContext, NavMenuControl> config)
        => layout.WithRenderer(a => a.Area == NavMenu,
            (h, c, store) =>
                h.ConfigBasedRenderer(
                    c,
                    store,
                    NavMenu,
                    () => new(),
                    config)
        );
    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        object title, string href, Icon icon = null)
        => layout.WithNavMenu((menu, _, _) => menu.WithNavLink(title, href, icon));



    public static LayoutDefinition WithToolbar(this LayoutDefinition layout,
        Func<ToolbarControl, LayoutAreaHost, RenderingContext, ToolbarControl> config)
        => layout.WithRenderer(a => a.Area == Toolbar,
            (h, c, store) =>
                h.ConfigBasedRenderer(
                    c,
                    store,
                    Toolbar,
                    () => new(),
                    config)
            );


}
