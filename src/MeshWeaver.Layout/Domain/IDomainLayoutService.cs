using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.Domain;

public interface IDomainLayoutService
{
    object Render(EntityRenderingContext context);

}

public class DomainLayoutService(DomainViewConfiguration configuration) : IDomainLayoutService
{
    public object Render(EntityRenderingContext context) =>
        configuration.ViewBuilders
            .Select(x => x.Invoke(context))
            .FirstOrDefault(x => x != null);
}
public record DomainViewConfiguration
{
    public DomainViewConfiguration()
    {
        ViewBuilders = [DefaultViewBuilder];
        PropertyViewBuilders = [MapToControl];
    }
    internal ImmutableList<Func<EntityRenderingContext, object>> ViewBuilders { get; init; }

    private object DefaultViewBuilder(EntityRenderingContext context)
    {
        return new LayoutStackControl()
            .WithView(Controls.Title(context.TypeDefinition.DisplayName, 1))
            .WithView(Controls.Html(context.TypeDefinition.Description))
            .WithView((host, _) =>
                host.Workspace
                    .GetStreamFor(new EntityReference(context.TypeDefinition.CollectionName, context.Id), host.Stream.Subscriber)
                    .Select(changeItem => DetailsLayout(context with {Instance = changeItem.Value})));

    }


    internal ImmutableList<Func<EditFormControl, PropertyRenderingContext, EditFormControl>> PropertyViewBuilders { get; init; } = [];

    public DomainViewConfiguration WithView(Func<EntityRenderingContext, object> viewBuilder)
        => this with { ViewBuilders = ViewBuilders.Add(viewBuilder) };
    public DomainViewConfiguration WithPropertyView(Func<EditFormControl, PropertyRenderingContext, EditFormControl> viewBuilder)
        => this with { PropertyViewBuilders = PropertyViewBuilders.Add(viewBuilder) };


    public object DetailsLayout(EntityRenderingContext context)
    {
        return Template.Bind(context.Instance,
            context.IdString,
            oo =>
                context.TypeDefinition.Type.GetProperties()
                    .Aggregate(Controls.EditForm, (grid, property) => 
                        PropertyViewBuilders
                        .Select(b => b.Invoke(grid, new PropertyRenderingContext(context, property)))
                        .FirstOrDefault(x => x != null)
                        )
        );
    }

    private static EditFormControl MapToControl(EditFormControl grid, PropertyRenderingContext context)
    {
        var propertyInfo = context.Property;
        var host = context.EntityContext.Host;
        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();

        if (dimensionAttribute != null)
        {
            var dimension = host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type);
            return grid.WithView(host.Workspace
                .GetStreamFor(new CollectionReference(host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)), host.Stream.Subscriber)
                .Select(changeItem =>
                    Controls.Select(jsonPointerReference)
                        .WithOptions(ConvertToOptions(changeItem.Value, dimension))))
                        .WithLabel(label);

        }

        if (propertyInfo.PropertyType.IsNumber())
            return grid.WithView(Controls.Number(jsonPointerReference, host.Workspace.DataContext.TypeRegistry.GetOrAddType(propertyInfo.PropertyType)).WithLabel(label));
        if (propertyInfo.PropertyType == typeof(string))
            return grid.WithView(Controls.TextBox(jsonPointerReference).WithLabel(label));

        return grid.WithView(Controls.Html($"<p>{label}: {propertyInfo.GetValue(host)}</p>"));
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
            .Select(kvp => (Option)Activator.CreateInstance(optionType, [kvp.Key, kvp.Value])).ToArray();
    }

}

public record EntityRenderingContext(LayoutAreaHost Host, ITypeDefinition TypeDefinition, string IdString, object Id)
{
    public object Instance { get; init; }
}

public record PropertyRenderingContext(EntityRenderingContext EntityContext, PropertyInfo Property);


