using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Catalog.Domain;
using MeshWeaver.Catalog.Layout;
using MeshWeaver.Mesh.Contract;
using MeshNode = MeshWeaver.Catalog.Domain.MeshNode;

namespace MeshWeaver.Catalog.ViewModel;

/// <summary>
/// Catalog area definition.
/// </summary>
public static class CatalogArea
{
    /// <summary>
    /// Adds the MeshBrowser Catalog view to the layout.
    /// </summary>
    /// <param name="layout">The layout to add Catalog to.</param>
    /// <returns>The updated layout including the MeshBrowser Catalog view.</returns>
    /// <remarks>
    /// This method registers the MeshBrowser Catalog view to the provided layout.
    /// </remarks>
    public static LayoutDefinition AddCatalog(this LayoutDefinition layout)
        => layout.WithView(nameof(Cards), Cards);

    /// <summary>
    /// Catalog view definition.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the view will be displayed.</param>
    /// <param name="context">The rendering context for generating the view.</param>
    /// <returns>The view representing a catalog of discovered mesh nodes.</returns>
    /// <remarks>
    /// This method constructs the main view of the MeshBrowser - the Catalog.
    /// </remarks>
    private static object Cards(this LayoutAreaHost layoutArea, RenderingContext context) =>
        Controls.Stack
            .WithView((area, context) => area.CatalogItems(context))
            .WithSkin(skin => skin.WithClass("catalog-items"))
        ;

    private static IObservable<object> CatalogItems(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.MeshDocuments()
            .Select(documents =>
                documents.Aggregate(Controls.Stack.WithSkin(skin => skin.WithVerticalGap(24)),
                    (stack, data) =>
                        stack
                            .WithView(CatalogControls.CatalogItem(data))
                ));
    }

    private static IObservable<IEnumerable<CatalogItemData>> MeshDocuments(this LayoutAreaHost layoutArea) =>
        layoutArea.GetOrAddVariable(nameof(MeshDocuments), () => layoutArea.Workspace
            .ReduceToTypes(typeof(MeshDocument), typeof(MeshNode), typeof(User))
            .DistinctUntilChanged()
            .Select(x =>
                x.Value.GetData<MeshDocument>()
                    .Join(
                        x.Value.GetData<MeshNode>(),
                        o => o.MeshNodeId,
                        d => d.Id,
                        (document, node) => (document, node)
                    )
                    .Join(
                        x.Value.GetData<User>(),
                        d => d.document.Author,
                        u => u.Id,
                        (d, user) => (d.document, d.node, user)
                    )
                    .Select(x => new CatalogItemData(x.document, x.node, x.user))
            ));
}
