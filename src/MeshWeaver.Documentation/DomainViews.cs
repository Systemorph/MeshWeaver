using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Domain.Layout;

public static class DomainViews
{
    public static MessageHubConfiguration AddDomainViews(this MessageHubConfiguration config, Func<DomainViewConfiguration, DomainViewConfiguration> configuration = null)
        => config
            .AddDocumentation()
            .AddLayout(layout => layout
            .WithView(nameof(Catalog), Catalog)
            .WithView(nameof(Details), Details)
            )
            .WithServices(services => services.AddSingleton<IDomainLayoutService>(sp => new DomainLayoutService((configuration ?? (x => x)).Invoke(new(sp.GetRequiredService<IMessageHub>())))));


    public const string Type = nameof(Type);

    public static object Details(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Stream.Reference.Id is not string typeAndId)
            return Error("Url has to be in form of Details/Type/Id");

        // Extract type and Id from typeAndId
        var parts = typeAndId.Split('/');
        if (parts.Length != 2)
            return Error("Url has to be in form of Details/Type/Id");
        var type = parts[0];
        var typeSource = area.Workspace.DataContext.TypeSources.GetValueOrDefault(type);
        if (typeSource == null)
            return Error($"Unknown type: {type}");

        try
        {
            var typeDefinition = typeSource.TypeDefinition;
            var idString = parts[1];
            var keyType = typeDefinition.GetKeyType();
            var id = keyType == typeof(string) ? idString : JsonSerializer.Deserialize(idString, keyType);
            return area.Hub.ServiceProvider.GetRequiredService<IDomainLayoutService>().Render(new(area, typeDefinition, idString, id, ctx));
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

        var context = new EntityRenderingContext(area, typeDefinition, null, null, ctx);
        return area.Hub.ServiceProvider.GetRequiredService<IDomainLayoutService>().GetCatalog(context);

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

    public static string GetDetailsUri(this IMessageHub hub, Type type, object id) =>
        GetDetailsReference(hub, type, id)?.ToAppHref(hub.Address);

    public static LayoutAreaReference GetDetailsReference(this IMessageHub hub, Type type, object id)
    {
        var collection = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetCollectionName(type);
        if (collection == null)
            return null;
        return hub.GetDetailsReference(collection, id);
    }

    public static LayoutAreaReference GetDetailsReference(this IMessageHub hub, string collection, object id) =>
        new(nameof(Details)) { Id = $"{collection}/{JsonSerializer.Serialize(id, hub.JsonSerializerOptions)}" };
    public static LayoutAreaReference GetCatalogReference(this IMessageHub hub, string collection) =>
        new(nameof(Catalog)) { Id = $"{collection}" };
}


public static class FileSource
{
    public const string EmbeddedResource = nameof(EmbeddedResource);
}
