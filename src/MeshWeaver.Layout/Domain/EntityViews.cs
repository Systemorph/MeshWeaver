using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
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
                        .Select(o => DetailsLayout(host, o, idString, typeDefinition)));
        }
        catch (Exception e)
        {
            return Error($"Exception while displaying details for Type {type} and id {parts[1]}: \n{e}");
        }
    }

    public static object DetailsLayout(this LayoutAreaHost host, object o, string idString, ITypeDefinition typeDefinition)
    {
        return Template.Bind(o,
            typeDefinition.GetKey(o).ToString(),
            oo => new PropertyViewBuilder(typeDefinition).AutoMapProperties().Properties.Aggregate(Controls.LayoutGrid,
                (g, p) => g
                    .WithView(
                        Controls.Stack
                        .WithOrientation(Orientation.Horizontal).WithView(Controls.Label(p.Title))
                        .WithView(GetDisplay(idString,p))
                    )
            )
        );
    }

    private static object GetDisplay(string id, PropertyControl propertyControl)
    {
        return LayoutAreaReference.GetDataPointer(id, propertyControl.Property.ToString());
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
                        typeDefinition.ToDataGrid(changeItem.Value.Instances.Values)
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
