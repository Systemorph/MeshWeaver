using MeshWeaver.Data;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Layout.Composition;
using static MeshWeaver.Layout.Controls;

namespace MeshWeaver.Layout.Domain;

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

    public static LayoutDefinition WithPageLayout(this LayoutDefinition builder)
        => builder
            .WithRenderer(IsPage, RenderPage)
            .WithRenderer(IsPage, RenderHeader)
            .WithRenderer(IsPage, RenderFooter)
            .WithRenderer(IsPage, RenderMainContent);

    private static EntityStoreAndUpdates RenderMainContent(LayoutAreaHost host,
        RenderingContext context, EntityStore store)
        => store.UpdateControl(MainContent, NamedArea(context.Area));


    private static EntityStoreAndUpdates
        RenderPage(
            LayoutAreaHost host,
            RenderingContext context,
            EntityStore store
        )
        =>
            host.RenderArea(
                new(Page) { Parent = context },
                Controls.Layout
                    .WithView(NamedArea(Header).AddSkin(Skins.Header))
                    .WithView(NamedArea(Toolbar))
                    .WithView(
                        Stack
                            .WithView(NamedArea(NavMenu))
                            .WithView(
                                Splitter
                                    .WithView(
                                        Stack
                                            .WithView(NamedArea(ContentHeading))
                                            .WithView(NamedArea(MainContent).AddSkin(Skins.BodyContent))
                                            .WithSkin(skin => skin.WithClass("main-content-stack"))
                                    )
                                    .WithView(NamedArea(ContextMenu), skin => skin.WithCollapsed(true))
                            )
                            .WithSkin(skin => skin
                                .WithOrientation(Orientation.Horizontal)
                            )
                            .AddSkin(new MainSkin())
                    )
                    .WithView(NamedArea(Footer).AddSkin(Skins.Footer))
                , store
            );

    public static EntityStoreAndUpdates
        RenderHeader(
            LayoutAreaHost host,
            RenderingContext context,
            EntityStore store
        ) =>
        host.RenderArea(
            new(Header) { Parent = context },
            Stack
                .WithOrientation(Orientation.Horizontal)
                .WithView(NavLink("Mesh Weaver", null, "/"))
            , store
        );

    public static EntityStoreAndUpdates RenderFooter(
        LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        host.RenderArea(
            new(Footer),
            Stack
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
            , store
        );
    //public static IEnumerable<EntityStoreUpdate> RenderNavMenu(LayoutAreaHost host, RenderingContext context, EntityStore store) => NavLink("Put link to documentation", "/");

    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        Func<NavMenuControl, LayoutAreaHost, RenderingContext, NavMenuControl> config)
        => layout.WithRenderer(IsPage,
            (h, c, store) =>
               h.ConfigBasedRenderer(
                    c,
                    store,
                    NavMenu,
                    () => new(),
                    config)
            );
    public static LayoutDefinition WithContentHeading(this LayoutDefinition layout,
        Func<LayoutStackControl, LayoutAreaHost, RenderingContext, LayoutStackControl> config)
        => layout.WithRenderer(IsPage,
            (h, c, store) =>
                h.ConfigBasedRenderer(
                    c,
                    store,
                    ContentHeading,
                    () => new(),
                    config)
            );



    public static void SetMainContent(this LayoutAreaHost host, object view)
        => host.UpdateArea(new(MainContent), view);

    public static LayoutDefinition WithToolbar(this LayoutDefinition layout,
        Func<ToolbarControl, LayoutAreaHost, RenderingContext, ToolbarControl> config)
        => layout.WithRenderer(IsPage,
            (h, c, store) =>
                h.ConfigBasedRenderer(
                    c,
                    store,
                    Toolbar,
                    () => new(),
                    config)
            );


}
