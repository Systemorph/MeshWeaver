using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

public static class EntityViews
{
    public static LayoutDefinition WithDomainViews(this LayoutDefinition layout)
        => layout.WithView(nameof(Catalog), Catalog)
            .WithView(nameof(Details), Details);


    public const string Type = nameof(Type);

    public static object Details(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Stream.Reference.Id is not string typeAndId)
            return Error("Url has to be in form of Details/Type/Id");

        // Extract type and Id from typeAndId
        var parts = typeAndId.Split('/');
        if(parts.Length != 2)
            return Error("Url has to be in form of Details/Type/Id");
        var type = parts[0];
        var typeSource = area.Workspace.DataContext.TypeSources.GetValueOrDefault(type);
        if(typeSource == null)
            return Error($"Unknown type: {type}");

        try
        {
            var typeDefinition = typeSource.TypeDefinition;
            var idString = parts[1];
            var id = JsonSerializer.Deserialize(idString, typeDefinition.GetKeyType());
            return new LayoutStackControl()
                .WithView(Controls.Title(typeDefinition.DisplayName, 1))
                .WithView(Controls.Html(typeDefinition.Description))
                .WithView((host, _) =>
                    host.Workspace
                        .GetStreamFor(new EntityReference(type, id), area.Stream.Subscriber)
                        .Select(changeItem => host.DetailsLayout(typeDefinition, changeItem.Value, idString)));
        }
        catch (Exception e)
        {
            return Error($"Exception while displaying details for Type {type} and id {parts[1]}: \n{e}");
        }
    }

    public static object DetailsLayout(this LayoutAreaHost host, ITypeDefinition typeDefinition, object o, string idString)
    {
        return Template.Bind(o,
            typeDefinition.GetKey(o).ToString(),
            oo =>
                typeDefinition.Type.GetProperties()
                    .Aggregate(Controls.LayoutGrid, host.MapToControl)
        );
    }

    private static LayoutGridControl MapToControl(this LayoutAreaHost host, LayoutGridControl grid, PropertyInfo propertyInfo)
    {
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
            return grid.WithView(Controls.Number(jsonPointerReference).WithLabel(label));
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



    private static MarkdownControl Error(string message) => new($"[!CAUTION]\n{message}\n");

    public static object Catalog(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Stream.Reference.Id is not string collection)
            throw new InvalidOperationException("No type specified for catalog.");
        var typeDefinition = area.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetTypeDefinition(collection);
        if (typeDefinition == null)
            throw new DataSourceConfigurationException(
                $"Collection {collection} is not mapped in Address {area.Hub.Address}.");
        return Controls.Stack
                .WithView(Controls.Title(typeDefinition.DisplayName, 1))
                .WithView(Controls.Html(typeDefinition.Description))
                .WithView((a, _) => a
                    .Workspace
                    .Stream
                    .Reduce(new CollectionReference(collection), area.Stream.Subscriber)
                    .Select(changeItem =>
                        typeDefinition.ToDataGrid(changeItem.Value.Instances.Values.Select(o => typeDefinition.SerializeEntityAndId(o, area.Hub.JsonSerializerOptions)))
                            .WithColumn(new TemplateColumnControl(new InfoButtonControl(typeDefinition.CollectionName, new JsonPointerReference(EntitySerializationExtensions.IdProperty))))
                    )
                )
            ;
    }

    public static NavMenuControl AddTypesCatalogs(this LayoutAreaHost host, NavMenuControl menu)
        => host
            .Workspace
            .DataContext
            .TypeSources
            .Values

            .OrderBy(x => x.TypeDefinition.Order ?? int.MaxValue)
            .GroupBy(x => x.TypeDefinition.GroupName)

            .Aggregate(
                menu,
                (m, types) => m.WithNavGroup(
                    types.Aggregate(
                        Controls.NavGroup(types.Key ?? "Types")
                            .WithSkin(skin => skin.Expand()),
                        (ng, t) => ng.WithLink(t.TypeDefinition.DisplayName,
                            new LayoutAreaReference(nameof(Catalog)) { Id = t.CollectionName }
                                .ToAppHref(host.Hub.Address))
                    )
                )
            );
}


public static class FileSource
{
    public const string EmbeddedResource = nameof(EmbeddedResource);
}
