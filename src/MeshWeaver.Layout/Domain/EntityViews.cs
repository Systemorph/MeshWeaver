using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

public static class EntityViews
{
    public static LayoutDefinition WithDomainViews(this LayoutDefinition layout)
        => layout.WithView(nameof(Catalog), Catalog);


    public const string Type = nameof(Type);

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
                .WithView((a,_) => a
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
