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




}
