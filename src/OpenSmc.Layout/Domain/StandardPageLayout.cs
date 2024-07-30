using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Layout.Domain;

public static class StandardPageLayout
{
    public const string Page = "$" + nameof(Page);
    public const string NavMenu = "$" + nameof(NavMenu);
    public const string ContextMenu = "$" + nameof(ContextMenu);
    public const string Footer = "$" + nameof(Footer);
    public const string Header = "$" + nameof(Header);
    public const string Toolbar = "$" + nameof(Toolbar);
    public const string MainContent = "$" + nameof(MainContent);
    public const string ContentHeading = "$" + nameof(ContentHeading);


    private static Func<RenderingContext, bool> IsPage => ctx => ctx.Layout == Page;
    public static LayoutDefinition WithStandardPageLayout(this LayoutDefinition builder)
        => builder
            .WithRenderer(IsPage, RenderPage)
            .WithRenderer(IsPage, RenderHeader)
            .WithRenderer(IsPage, RenderFooter)
            .WithRenderer(IsPage, RenderMainContent);

    private static IEnumerable<(string Area, UiControl Control)> RenderMainContent(LayoutAreaHost host, RenderingContext context, EntityStore store)
    => [(MainContent,  NamedArea(context.Area))];


    private static IEnumerable<(string Area, UiControl Control)>
        RenderPage(
            LayoutAreaHost host,
            RenderingContext context,
            EntityStore store
        )
        =>
            host.RenderArea(
                new(Page) { Parent = context },


                Stack
                    .WithOrientation(Orientation.Vertical)
                    .WithWidth("100%")
                    .WithView(NamedArea(Header).WithSkin(Skins.Header))
                    .WithView(NamedArea(Toolbar))
                    .WithView(
                        Stack
                            .WithClass("main")
                            .WithOrientation(Orientation.Horizontal)
                            .WithWidth("100%")
                            .WithView(NamedArea(NavMenu))
                            .WithView(Splitter
                                .WithView(
                                    Stack
                                        .WithSkin(Skins.SplitterPane)
                                        .WithSkin(Skins.BodyContent)
                                        .WithOrientation(Orientation.Vertical)
                                        .WithView(NamedArea(ContentHeading))
                                        .WithView(NamedArea(MainContent))
                                )
                                .WithView(NamedArea(ContextMenu)
                                    .WithSkin(Skins.SplitterPane))
                            )

                    )
                    .WithView(NamedArea(Footer).WithSkin(Footer))
            );

    public static IEnumerable<(string Area, UiControl Control)>
        RenderHeader(
            LayoutAreaHost host,
            RenderingContext context,
            EntityStore store) =>
        host.RenderArea(
            new(Header) { Parent = context },
            Stack.WithSkin(Skins.Header)
                .WithOrientation(Orientation.Horizontal)
                .WithView(NavLink("Mesh Weaver", null, "/"))
        );

    public static IEnumerable<(string Area, UiControl Control)> RenderFooter(
        LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        host.RenderArea(
            new(Footer),
            Stack
                .WithSkin(Skins.Footer)
                .WithOrientation(Orientation.Horizontal)
                .WithView(
                    Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithView(
                            Html("© 2024 Systemorph")
                        )
                        .WithView(
                            Html("Privacy Policy")
                        )
                        .WithView(
                            Html("Terms of Service")
                        )
                )
        );
    //public static IEnumerable<(string Area, UiControl Control)> RenderNavMenu(LayoutAreaHost host, RenderingContext context, EntityStore store) => NavLink("Put link to documentation", "/");

    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        Func<NavMenuControl, RenderingContext, NavMenuControl> config)
        => layout.WithRenderer(IsPage,
            (host, context, store) =>
                host.RenderArea(new(NavMenu), config.Invoke(store.GetControl<NavMenuControl>(NavMenu) ?? new(), context)));
    public static LayoutDefinition WithToolbar(this LayoutDefinition layout,
        Func<ToolbarControl, RenderingContext, ToolbarControl> config)
        => layout.WithRenderer(IsPage,
            (host, context, store) =>
                host.RenderArea(new(Toolbar), config.Invoke(store.GetControl<ToolbarControl>(Toolbar) ?? new(), context)));

    public static LayoutDefinition WithContentHeading(this LayoutDefinition layout,
        Func<LayoutStackControl, RenderingContext, LayoutStackControl> heading)
    => layout.WithRenderer(IsPage,
            (host, context, store) =>
                host.RenderArea(new(ContentHeading), heading.Invoke(store.GetControl<LayoutStackControl>(ContentHeading) ?? new(), context)));


    public static void SetMainContent(this LayoutAreaHost host, object view)
        => host.UpdateArea(new(MainContent),view);
    public static void SetContextMenu(this LayoutAreaHost host, object view)
        => host.UpdateArea(new(ContextMenu), view);

    private static object ApplyConfig<TControl>(this LayoutAreaHost host, RenderingContext context, string id, Func<TControl, RenderingContext, TControl> config)
        where TControl : class, new()
    {
        return host.SetVariable(id,
            config.Invoke(host.GetVariable<TControl>(id) ?? new(), context));
    }



}
