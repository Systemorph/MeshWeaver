using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
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


    private object DefaultCatalog(EntityRenderingContext context)
    {
        var typeDefinition = context.TypeDefinition;
        var ret = Controls.Stack
            .WithView(Controls.Title(typeDefinition.DisplayName, 2));
        var description = DocumentationService.GetDocumentation(typeDefinition.Type)?.Summary?.Text;
        if(!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));
        return ret
                .WithView((a, _) => a
                    .Workspace
                    .GetStream(new CollectionsReference(typeDefinition.CollectionName))
                    .Select(changeItem =>
                        typeDefinition.ToDataGrid(changeItem.Value.Collections.Values.First().Instances.Values.Select(o => typeDefinition.SerializeEntityAndId(o, context.Host.Hub.JsonSerializerOptions)))
                            .WithColumn(new TemplateColumnControl(new InfoButtonControl(typeDefinition.CollectionName, new JsonPointerReference(EntitySerializationExtensions.IdProperty))))
                    )
                )
            ;

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


    public object DetailsLayout(LayoutAreaHost host, RenderingContext ctx, EntityRenderingContext context)
    {
        var stream = host.Workspace
            .GetStream(new EntityReference(context.TypeDefinition.CollectionName, context.Id));
        var ret = stream
            .Select(x => x.Value)
            .Bind(_ =>
                context.TypeDefinition.Type.GetProperties()
                    .Aggregate(new EditFormControl(), (grid, property) =>
                        PropertyViewBuilders
                            .Select(b =>
                                b.Invoke(grid, new PropertyRenderingContext(context, property))
                            )
                            .FirstOrDefault(x => x != null)
                    ), ctx.Area);
        return ret;
    }




    public object GetCatalog(EntityRenderingContext context) =>
        CatalogBuilders
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
