using MeshWeaver.Domain;

using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Layout;

public static class NavMenuExtensions
{
    public const string NavMenu = "$" + nameof(NavMenu);

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
        Func<NavMenuControl, LayoutAreaHost, RenderingContext, Task<NavMenuControl>> config)
        => layout.WithRenderer(a => a.Area == NavMenu,
            async (h, c, store) =>
                await h.ConfigBasedRenderer(
                    c,
                    store,
                    NavMenu,
                    () => new(),
                    config)
        );
    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        object title, string href, Icon icon = null)
        => layout.WithNavMenu((menu, _, _) => menu.WithNavLink(title, href, icon));

    public static TContainer WithViews<TContainer>(this TContainer container, params IEnumerable<UiControl> views)
    where TContainer : ContainerControl<TContainer> =>
        views.Aggregate(container, (c, v) => c.WithView(v));



}
