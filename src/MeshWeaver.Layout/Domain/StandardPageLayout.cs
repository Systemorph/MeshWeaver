using MeshWeaver.Data;
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

    private static IEnumerable<Func<EntityStore, EntityStore>> RenderMainContent(LayoutAreaHost host,
        RenderingContext context)
        => [s => s.UpdateControl(MainContent, NamedArea(context.Area))];


    private static IEnumerable<Func<EntityStore, EntityStore>>
        RenderPage(
            LayoutAreaHost host,
            RenderingContext context
        )
        =>
            host.RenderArea(
                new(Page) { Parent = context },
                Stack
                    .WithSkin(Skins.Layout)
                    .WithWidth("100%")
                    .WithView(NamedArea(Header).WithSkin(Skins.Header))
                    .WithView(NamedArea(Toolbar))
                    .WithView(
                        Stack
                            .WithClass("main")
                            .WithOrientation(Orientation.Horizontal)
                            .WithWidth("100%")
                            .WithView(NamedArea(NavMenu))
                            .WithView(Stack
                                .WithSkin(StackSkins.Splitter)
                                .WithView(
                                    Stack
                                        .WithSkin(Skins.BodyContent)
                                        .WithSkin(Skins.SplitterPane)
                                        .WithView(NamedArea(ContentHeading))
                                        .WithView(NamedArea(MainContent))
                                )
                                .WithView(NamedArea(ContextMenu))
                            )

                    )
                    .WithView(NamedArea(Footer).WithSkin(Footer))
            );

    public static IEnumerable<Func<EntityStore, EntityStore>>
        RenderHeader(
            LayoutAreaHost host,
            RenderingContext context) =>
        host.RenderArea(
            new(Header) { Parent = context },
            Stack.WithSkin(Skins.Header)
                .WithOrientation(Orientation.Horizontal)
                .WithView(NavLink("Mesh Weaver", null, "/"))
        );

    public static IEnumerable<Func<EntityStore, EntityStore>> RenderFooter(
        LayoutAreaHost host, RenderingContext context) =>
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
    //public static IEnumerable<Func<EntityStore,EntityStore>> RenderNavMenu(LayoutAreaHost host, RenderingContext context, EntityStore store) => NavLink("Put link to documentation", "/");

    public static LayoutDefinition WithNavMenu(this LayoutDefinition layout,
        Func<NavMenuControl, RenderingContext, NavMenuControl> config)
        => layout.WithRenderer(IsPage,
            (h, c) =>
            [
                store => h.ConfigBasedRenderer(
                    c,
                    store,
                    StandardPageLayout.NavMenu,
                    () => new(),
                    config)
            ]);
    public static LayoutDefinition WithContentHeading(this LayoutDefinition layout,
        Func<LayoutStackControl, RenderingContext, LayoutStackControl> config)
        => layout.WithRenderer(IsPage,
            (h, c) =>
            [
                store => h.ConfigBasedRenderer<LayoutStackControl>(
                    c,
                    store,
                    StandardPageLayout.ContentHeading,
                    () => new(),
                    config)
            ]);



    public static void SetMainContent(this LayoutAreaHost host, object view)
        => host.UpdateArea(new(MainContent), view);

    public static void SetContextMenu(this LayoutAreaHost host, UiControl view)
        => host.UpdateArea(new(ContextMenu), view.WithSkin(Skins.SplitterPane));
    public static LayoutDefinition WithToolbar(this LayoutDefinition layout,
        Func<ToolbarControl, RenderingContext, ToolbarControl> config)
        => layout.WithRenderer(IsPage,
            (h, c) =>
            [
                store => h.ConfigBasedRenderer(
                    c,
                    store,
                    StandardPageLayout.Toolbar,
                    () => new(),
                    config)
            ]);


}
