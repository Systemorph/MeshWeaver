using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

public interface IDomainLayoutService
{
    object Render(EntityRenderingContext context);

    object GetCatalog(EntityRenderingContext context);
}

public class DomainLayoutService(DomainViewConfiguration configuration) : IDomainLayoutService
{
    public object Render(EntityRenderingContext context) =>
        configuration.ViewBuilders
            .Reverse()
            .Select(x => x.Invoke(context))
            .FirstOrDefault(x => x != null);

    public object GetCatalog(EntityRenderingContext context)
    {
        return configuration.GetCatalog(context);
    }
}
public record DomainViewConfiguration
{
    public readonly IMessageHub Hub;
    public readonly IDocumentationService DocumentationService;

    public DomainViewConfiguration(IMessageHub hub)
    {
        this.Hub = hub;
        DocumentationService = hub.ServiceProvider.GetRequiredService<IDocumentationService>();
        ViewBuilders = [DefaultViewBuilder];
        PropertyViewBuilders = [(editor, ctx)=> 
            hub.ServiceProvider.MapToControl<EditFormControl,EditFormSkin>(editor,ctx.Property)];
        CatalogBuilders = [DefaultCatalog];
    }

    public const string HrefProperty = "$href";

    private object DefaultCatalog(EntityRenderingContext context)
    {
        var typeDefinition = context.TypeDefinition;
        var ret = Controls.Stack
            .WithView(Controls.Title(typeDefinition.DisplayName, 2));
        var description = DocumentationService.GetDocumentation(typeDefinition.Type)?.Summary?.Text;
        if(!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));
        return ret
                .WithView((host,_) => RenderCatalog(host, context, typeDefinition))
            ;

    }

    private object RenderCatalog(LayoutAreaHost host, EntityRenderingContext context, ITypeDefinition typeDefinition)
    {
        var stream = host.Workspace
            .GetStream(new CollectionReference(typeDefinition.CollectionName));

        var id = Guid.NewGuid().AsString();
        host.RegisterForDisposal(stream
            .Select(i => i.Value.Instances.Values.Select(o =>
            {
                var serialized = typeDefinition.SerializeEntityAndId(o,
                    context.Host.Hub.JsonSerializerOptions);
                serialized[HrefProperty] = new LayoutAreaReference("Details")
                {
                    Id =
                        $"{typeDefinition.CollectionName}/{serialized[EntitySerializationExtensions.IdProperty]}"
                }.ToHref(host.Hub.Address);
                return serialized;
            }))

            .Subscribe(x => host.UpdateData(id, x))
        );

        return typeDefinition.ToDataGrid(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new TemplateColumnControl(
                    new ButtonControl("View")
                        .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                        .WithLabel("View")
                        .WithNavigateToHref(new ContextProperty(HrefProperty))
                )
            );
    }

    internal ImmutableList<Func<EntityRenderingContext, object>> ViewBuilders { get; init; }
    internal ImmutableList<Func<EditFormControl, PropertyRenderingContext, EditFormControl>> PropertyViewBuilders { get; init; } = [];
    internal ImmutableList<Func<EntityRenderingContext, object>> CatalogBuilders { get; init; } = [];

    private object DefaultViewBuilder(EntityRenderingContext context)
    {
        var ret = new StackControl()
            .WithView(Controls.Title(context.TypeDefinition.DisplayName, 1));
        var description = DocumentationService.GetDocumentation(context.TypeDefinition.Type)?.Summary?.Text;
        if(!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));
        return ret
            .WithView((h, ctx) => DetailsLayout(h,ctx,context));

    }



    public DomainViewConfiguration WithView(Func<EntityRenderingContext, object> viewBuilder)
        => this with { ViewBuilders = ViewBuilders.Add(viewBuilder) };
    public DomainViewConfiguration WithPropertyView(Func<EditFormControl, PropertyRenderingContext, EditFormControl> viewBuilder)
        => this with { PropertyViewBuilders = PropertyViewBuilders.Add(viewBuilder) };


    public object DetailsLayout(LayoutAreaHost host, RenderingContext _, EntityRenderingContext context)
    {
        var id = Guid.NewGuid().AsString();
        var stream = host.Workspace
            .GetStream(new EntityReference(context.TypeDefinition.CollectionName, context.Id));

        var typeDefinition = context.TypeDefinition;
        host.RegisterForDisposal(stream
            .Select(e => typeDefinition.SerializeEntityAndId(e.Value, context.Host.Hub.JsonSerializerOptions))
            .Subscribe(e => host.UpdateData(id,e))
        );

        return  context.TypeDefinition.Type.GetProperties()
                    .Aggregate(new EditFormControl {DataContext = LayoutAreaReference.GetDataPointer(id)}, (grid, property) =>
                        PropertyViewBuilders
                            .Reverse()
                            .Select(b =>
                                b.Invoke(grid, new PropertyRenderingContext(context, property))
                            )
                            .FirstOrDefault(x => x != null)
                    );
    }




    public object GetCatalog(EntityRenderingContext context) =>
        CatalogBuilders
            .Reverse()
            .Select(x => x.Invoke(context))
            .FirstOrDefault(x => x != null);
}

public record EntityRenderingContext(
    LayoutAreaHost Host,
    ITypeDefinition TypeDefinition,
    string IdString,
    object Id,
    RenderingContext RenderingContext);

public record PropertyRenderingContext(EntityRenderingContext EntityContext, PropertyInfo Property);
