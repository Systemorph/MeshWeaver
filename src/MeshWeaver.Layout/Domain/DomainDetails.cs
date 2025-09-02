using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Reflection;

namespace MeshWeaver.Layout.Domain;

public static class DomainDetails
{
    public static UiControl GetDetails(LayoutAreaHost host, ITypeDefinition typeDefinition, object id, RenderingContext ctx)
    {
        var ret = new StackControl()
            .WithView(Controls.Title(typeDefinition.DisplayName, 1));
        
        var description = typeDefinition.Type.GetXmlDocsSummary();
        if (!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));
        
        return ret.WithView((areaHost, _) => DetailsLayout(areaHost, typeDefinition, id));
    }

    private static UiControl DetailsLayout(LayoutAreaHost host, ITypeDefinition typeDefinition, object id)
    {
        var dataId = Guid.NewGuid().AsString();
        var stream = host.Workspace
            .GetStream(new EntityReference(typeDefinition.CollectionName, id));

        host.RegisterForDisposal(stream!
            .Select(e => typeDefinition.SerializeEntityAndId(e?.Value ?? throw new InvalidOperationException("Entity value is null"), host.Hub.JsonSerializerOptions))
            .Subscribe(e => host.UpdateData(dataId, e))
        );

        return typeDefinition.Type.GetProperties()
            .Aggregate(new EditFormControl { DataContext = LayoutAreaReference.GetDataPointer(dataId) }, (form, property) =>
                MapToEditFormControl(form, property, host));
    }

    private static EditFormControl MapToEditFormControl(EditFormControl form, PropertyInfo property, LayoutAreaHost host)
    {
        return host.Hub.ServiceProvider.MapToControl<EditFormControl, EditFormSkin>(form, property);
    }
}