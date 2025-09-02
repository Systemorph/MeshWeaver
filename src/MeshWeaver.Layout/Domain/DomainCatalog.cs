using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ShortGuid;
using Namotion.Reflection;

namespace MeshWeaver.Layout.Domain;

public static class DomainCatalog
{

    public static UiControl GetCatalog(LayoutAreaHost host, ITypeDefinition typeDefinition, RenderingContext ctx)
    {
        var ret = Controls.Stack
            .WithView(Controls.Title(typeDefinition.DisplayName, 2));
        
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
