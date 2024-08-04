using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Reflection;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Layout.Domain;

public static class StandardDocsLayout
{
    public const string Docs = "$" + nameof(Docs);
    public const string MainContent = "$" + nameof(MainContent);

    private static Func<RenderingContext, bool> IsDocs => ctx => ctx.Layout == Docs;

    public static LayoutDefinition WithDocsLayout(this LayoutDefinition builder)
        => builder
            .WithRenderer(IsDocs, RenderDocs);



    private static IEnumerable<Func<EntityStore, EntityStore>>
        RenderDocs(
            LayoutAreaHost host,
            RenderingContext context
        )
        =>
        [
            store => store.UpdateControl(
                Docs,
                Tabs.WithTab(NamedArea(context.Area).WithSkin(Skins.Tab(context.DisplayName)))
            )

        ];


    public static LayoutDefinition WithSources(this LayoutDefinition layout,
        Func<RenderingContext, bool> contextFilter, params string[] sources)
        => layout.WithRenderer(ctx => IsDocs(ctx) && contextFilter(ctx),
            (h, config) =>
            [
                store => h.ConfigBasedRenderer(
                    config,
                    store,
                    Docs,
                    () => new TabsControl(),
                    (tabs, ctx) =>
                        sources.Select(s => new LayoutAreaControl(layout.Hub.Address, new(Docs) { Id = s }))
                            .Aggregate(tabs, (t,s) =>
                                t.WithTab(new LayoutAreaControl(layout.Hub.Address, new(Docs) { Id = s }))))
            ]);




}
