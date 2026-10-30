using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Layout.Domain;

/// <summary>
/// Provides the standard catalog layout area for a domain type: a title header with data-model link,
/// an optional XML-doc description, and a data grid of all collection instances with click-through to the detail view.
/// </summary>
public static class DomainCatalogLayoutArea
{

    /// <summary>
    /// Builds the catalog view for <paramref name="typeDefinition"/>: a title bar with a data-model navigation link,
    /// an optional type description, and a data grid of all instances with row-click navigation to the detail page.
    /// </summary>
    /// <param name="host">The layout area host providing workspace and hub access.</param>
    /// <param name="typeDefinition">The type whose collection instances are displayed.</param>
    /// <param name="ctx">The rendering context (currently unused).</param>
    /// <returns>A stack control containing the catalog header and data grid.</returns>
    public static UiControl GetCatalog(LayoutAreaHost host, ITypeDefinition typeDefinition, RenderingContext ctx)
    {
        var _= ctx;

        // Title with right-aligned navigation glyph
        var ret = Controls.Stack
            .WithView(DomainViews.TitleRow(
                Controls.H2(typeDefinition.DisplayName).WithStyle("margin: 0;"),
                DomainViews.NavGlyph("⧉", host.Localize("ui.dataModel"), $"/{host.Hub.Address}/DataModel/{typeDefinition.Type.Name}")));

        // Data-bound: the documentation file is read on the I/O pool, not while this view is built.
        ret = ret.WithView(DomainViews.DescriptionOf(host.Hub, typeDefinition.Type));

        return ret.WithView((areaHost, ctx) => RenderCatalog(areaHost, ctx, typeDefinition));
    }

    private static UiControl RenderCatalog(LayoutAreaHost host, RenderingContext context, ITypeDefinition typeDefinition)
    {
        var stream = host.Workspace
            .GetStream(new CollectionReference(typeDefinition.CollectionName));

        var id = Guid.NewGuid().AsString();
        // FeedData, never a bare Subscribe: a faulting collection stream must show on this area,
        // not rethrow on the pool (Template.BindFeed).
        host.RegisterForDisposal(context.Area, host.FeedData(context.Area, id, stream!
            .Select(i => i.Value?.Instances.Values.Select(o => typeDefinition.SerializeEntityAndId(o,
                host.Hub.JsonSerializerOptions)) ?? [])));

        return typeDefinition.ToDataGrid(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithClickAction(HandleClick);
    }

    private static void HandleClick(UiActionContext context)
    {
        var click = context.Payload as DataGridCellClick;
        if (click?.Item is null)
            return;

        var typeDefinition = context.Hub.TypeRegistry.GetTypeDefinition(click.Item.GetType());
        if (typeDefinition is null)
            return;
        var key = typeDefinition.GetKey(click.Item);
        context.Host.UpdateArea(context.Area, new RedirectControl($"/{context.Hub.Address}/Details/{typeDefinition.CollectionName}/{key}"));

    }
}
