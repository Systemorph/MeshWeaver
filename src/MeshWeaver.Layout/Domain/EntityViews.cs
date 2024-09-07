using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

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
        var typeSource = area.Workspace.DataContext.GetTypeSource(collection);
        if (typeSource == null)
            throw new DataSourceConfigurationException(
                $"Collection {collection} is not mapped in Address {area.Hub.Address}.");
        return
            Controls.Stack
                .WithView(Controls.Title(typeSource.DisplayName, 1))
                .WithView(Controls.Html(typeSource.Description))
                .WithView((a,_) => a
                    .Workspace
                    .Stream
                    .Reduce(new CollectionReference(collection), area.Stream.Subscriber)
                    .Select(changeItem =>
                        area.ToDataGrid(
                            changeItem
                                .Value
                                .Instances
                                .Values,
                            typeSource.ElementType,
                            x => x.AutoMapColumns()
                        )
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

            .OrderBy(x => x.Order ?? int.MaxValue)
            .GroupBy(x => x.GroupName)

            .Aggregate(
                menu,
                (m, types) => m.WithNavGroup(
                    types.Aggregate(
                        Controls.NavGroup(types.Key ?? "Types")
                            .WithSkin(skin => skin.Expand()),
                        (ng, t) => ng.WithLink(t.DisplayName,
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
