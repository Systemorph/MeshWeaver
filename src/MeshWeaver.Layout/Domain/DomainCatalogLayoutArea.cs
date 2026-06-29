using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ShortGuid;
using Namotion.Reflection;

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

        // Title with right-aligned navigation icon
        var navigationIcon = $"<a href=\"/{host.Hub.Address}/DataModel/{typeDefinition.Type.Name}\" title=\"Data Model\" style=\"text-decoration: none; font-size: 2em; line-height: 1;\">⧉</a>";
        var titleWithNav = $"<div style=\"display: flex !important; justify-content: space-between !important; align-items: center !important; margin-bottom: 1rem; width: 100%;\"><h2 style=\"margin: 0; flex-grow: 1;\">{typeDefinition.DisplayName}</h2><div style=\"flex-shrink: 0;\">{navigationIcon}</div></div>";
        var ret = Controls.Stack
            .WithView(Controls.Html(titleWithNav));
        
        var description = typeDefinition.Type.GetXmlDocsSummary();
        if (!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));
        
        return ret.WithView((areaHost, _) => RenderCatalog(areaHost, typeDefinition));
    }

    private static UiControl RenderCatalog(LayoutAreaHost host, ITypeDefinition typeDefinition)
    {
        var stream = host.Workspace
            .GetStream(new CollectionReference(typeDefinition.CollectionName));

        var id = Guid.NewGuid().AsString();
        host.RegisterForDisposal(stream!
            .Select(i => i.Value?.Instances.Values.Select(o => typeDefinition.SerializeEntityAndId(o,
                host.Hub.JsonSerializerOptions)) ?? [])
            .Subscribe(x => host.UpdateData(id, x))
        );

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
