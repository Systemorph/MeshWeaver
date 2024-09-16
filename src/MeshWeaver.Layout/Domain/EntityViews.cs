using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

public static class EntityViews
{
    public static MessageHubConfiguration WithDomainViews(this MessageHubConfiguration config, Func<DomainViewConfiguration, DomainViewConfiguration> configuration = null)
        => config.AddLayout(layout => layout
            .WithView(nameof(Catalog), Catalog)
            .WithView(nameof(Details), Details)
            )
            .WithServices(services => services.AddSingleton<IDomainLayoutService>(_ => new DomainLayoutService((configuration ?? (x => x)).Invoke(new()))));


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
            return area.Hub.ServiceProvider.GetRequiredService<IDomainLayoutService>().Render(new(area, typeDefinition, idString, id));
        }
        catch (Exception e)
        {
            return Error($"Exception while displaying details for Type {type} and id {parts[1]}: \n{e}");
        }
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
