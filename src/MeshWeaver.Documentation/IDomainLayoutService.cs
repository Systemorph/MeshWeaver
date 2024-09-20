using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Domain.Layout;

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
        PropertyViewBuilders = [MapToControl];
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
                    .Stream
                    .Reduce(new CollectionReference(typeDefinition.CollectionName), context.Host.Stream.Subscriber)
                    .Select(changeItem =>
                        typeDefinition.ToDataGrid(changeItem.Value.Instances.Values.Select(o => typeDefinition.SerializeEntityAndId(o, context.Host.Hub.JsonSerializerOptions)))
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
        var ret = new LayoutStackControl()
            .WithView(Controls.Title(context.TypeDefinition.DisplayName, 1));
        var description = DocumentationService.GetDocumentation(context.TypeDefinition.Type)?.Summary?.Text;
        if(!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));
        return ret
            .WithView((host, _) =>
                host.Workspace
                    .GetStreamFor(new EntityReference(context.TypeDefinition.CollectionName, context.Id), host.Stream.Subscriber)
                    .Select(changeItem => DetailsLayout(context with { Instance = changeItem.Value })));

    }



    public DomainViewConfiguration WithView(Func<EntityRenderingContext, object> viewBuilder)
        => this with { ViewBuilders = ViewBuilders.Add(viewBuilder) };
    public DomainViewConfiguration WithPropertyView(Func<EditFormControl, PropertyRenderingContext, EditFormControl> viewBuilder)
        => this with { PropertyViewBuilders = PropertyViewBuilders.Add(viewBuilder) };


    public object DetailsLayout(EntityRenderingContext context)
    {
        var ret = Template.Bind(context.Instance,
            context.IdString,
            oo =>
                context.TypeDefinition.Type.GetProperties()
                    .Aggregate(Controls.EditForm, (grid, property) =>
                        PropertyViewBuilders
                        .Select(b => b.Invoke(grid, new PropertyRenderingContext(context, property)))
                        .FirstOrDefault(x => x != null)
                        )
        );

        var host = context.Host;
        var subscription = host.Stream.Subscribe(changeItem =>
        {
            if(changeItem.Patch?.Value is null)
                return;
            if (changeItem.ChangedBy.Equals(host.Stream.Subscriber) &&changeItem.Patch.Value.Operations.Any(x => x.Path.ToString().StartsWith(ret.DataContext)))
            {
                var instance = changeItem.Value.Collections.GetValueOrDefault(LayoutAreaReference.Data)?.Instances.GetValueOrDefault(context.IdString);
                if(instance != null)
                    host.Workspace.Update(instance);
                else
                {
                    // TODO V10: Should we delete here? How would we end up here? (20.09.2024, Roland Bürgi)
                }
            }
        });

        host.AddDisposable(context.RenderingContext.Area, subscription);

        return ret;
    }

    private EditFormControl MapToControl(EditFormControl grid, PropertyRenderingContext context)
    {
        var propertyInfo = context.Property;
        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();
        var description = DocumentationService.GetDocumentation(context.Property)?.Summary?.Text;

        Func<EditFormItemSkin, EditFormItemSkin> skinConfiguration = skin => skin with{Name = propertyInfo.Name.ToCamelCase(), Description = description, Label = label};
        if (dimensionAttribute != null)
        {
            return grid.WithView((host,_) => host.Workspace
                        .GetStreamFor(
                            new CollectionReference(
                                host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)),
                            host.Stream.Subscriber)
                        .Select(changeItem =>
                            Controls.Select(jsonPointerReference)
                                .WithOptions(ConvertToOptions(changeItem.Value, host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type)))), skinConfiguration)
                ;

        }

        if (propertyInfo.PropertyType.IsNumber())
            return grid.WithView(RenderNumber(jsonPointerReference, context.EntityContext.Host, propertyInfo), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(string))
            return grid.WithView(RenderText(jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return grid.WithView(RenderDateTime(jsonPointerReference), skinConfiguration);
        return grid.WithView(propertyInfo.GetValue(context.EntityContext.Instance),skinConfiguration);
    }

    private DateTimeControl RenderDateTime(JsonPointerReference jsonPointerReference)
    {
        return Controls.DateTime(jsonPointerReference);
    }

    private static TextFieldControl RenderText(JsonPointerReference jsonPointerReference)
    {
        // TODO V10: Add validations. (17.09.2024, Roland Bürgi)
        return Controls.Text(jsonPointerReference);
    }

    private static NumberFieldControl RenderNumber(JsonPointerReference jsonPointerReference, LayoutAreaHost host, PropertyInfo propertyInfo)
    {
        // TODO V10: Add range validation, etc. (17.09.2024, Roland Bürgi)
        return Controls.Number(jsonPointerReference, host.Workspace.DataContext.TypeRegistry.GetOrAddType(propertyInfo.PropertyType));
    }

    private static JsonPointerReference GetJsonPointerReference(PropertyInfo propertyInfo)
    {
        return new JsonPointerReference($"/{propertyInfo.Name.ToCamelCase()}");
    }

    private static IReadOnlyCollection<Option> ConvertToOptions(InstanceCollection instances, ITypeDefinition dimensionType)
    {
        var displayNameSelector =
            typeof(INamed).IsAssignableFrom(dimensionType.Type)
                ? (Func<object, string>)(x => ((INamed)x).DisplayName)
                : o => o.ToString();

        var keyType = dimensionType.GetKeyType();
        var optionType = typeof(Option<>).MakeGenericType(keyType);
        return instances.Instances
            .Select(kvp => (Option)Activator.CreateInstance(optionType, [kvp.Key, displayNameSelector(kvp.Value)])).ToArray();
    }


    public object GetCatalog(EntityRenderingContext context) =>
        CatalogBuilders
            .Select(x => x.Invoke(context))
            .FirstOrDefault(x => x != null);
}

public record EntityRenderingContext(LayoutAreaHost Host, ITypeDefinition TypeDefinition, string IdString, object Id, RenderingContext RenderingContext)
{
    public object Instance { get; init; }
}

public record PropertyRenderingContext(EntityRenderingContext EntityContext, PropertyInfo Property);


