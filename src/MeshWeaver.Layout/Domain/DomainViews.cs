using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

public static class DomainViews
{
    public static MessageHubConfiguration AddDomainViews(this MessageHubConfiguration config)
        => config
            .AddLayout(layout => layout
            .WithView(nameof(Catalog), Catalog)
            .WithView(nameof(Details), Details)
            .WithView(nameof(DataModelLayoutArea.DataModel), DataModelLayoutArea.DataModel)
            );



    public const string Type = nameof(Type);

    [Browsable(false)]
    public static UiControl Details(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Reference.Id is not string typeAndId)
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
            var id = keyType == typeof(string)  ? idString : JsonSerializer.Deserialize(idString, keyType)!;
            return DomainDetails.GetDetails(area, typeDefinition, id, ctx);
        }
        catch (Exception e)
        {
            return Error($"Exception while displaying details for Type {type} and id {parts[1]}: \n{e}");
        }
    }




    private static MarkdownControl Error(string message) => new($"[!CAUTION]\n{message}\n");

    [Browsable(false)]
    public static UiControl Catalog(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Reference.Id is not string collection)
            throw new InvalidOperationException("No type specified for catalog.");
        var typeDefinition = area.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetTypeDefinition(collection);
        if (typeDefinition == null)
            throw new DataSourceConfigurationException(
                $"Collection {collection} is not mapped in Address {area.Hub.Address}.");

        return DomainCatalogLayoutArea.GetCatalog(area, typeDefinition, ctx);
    }

    

    public static IEnumerable<UiControl> AddTypesCatalogs(this LayoutAreaHost host)
        => GetTypes(host)
            .GroupBy(x => x.GroupName)
            .Select(types => (types.Key, types))
            .SelectMany(m => m.Key is null ? m.types.Select(t => 
                (UiControl)new NavLinkControl(t.DisplayName, t.Icon,
                new LayoutAreaReference(nameof(Catalog)) { Id = t.CollectionName }
                    .ToHref(host.Hub.Address)) )
                : [ m.types.Select(t =>
                    new NavLinkControl(t.DisplayName, t.Icon,
                    new LayoutAreaReference(nameof(Catalog)) { Id = t.CollectionName }
                        .ToHref(host.Hub.Address)) ).Aggregate(new NavGroupControl(m.Key), (g,l) => g.WithView(l))]
                );

    private static IOrderedEnumerable<ITypeDefinition> GetTypes(this LayoutAreaHost host)
    {
        return host
            .Workspace
            .DataContext
            .TypeSources
            .Values
            .Select(x => x.TypeDefinition)
            .OrderBy(x => x.Order ?? int.MaxValue).ThenBy(x => x.DisplayName);
    }


    public static LayoutAreaReference? GetDetailsReference(this IMessageHub hub, Type type, object id)
    {
        var collection = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetCollectionName(type);
        if (collection == null)
            return null;
        return GetDetailsReference(collection, id);
    }

    public static LayoutAreaReference GetDetailsReference(string collection, object id) =>
        new(nameof(Details)) { Id = $"{collection}/{id}" };
    public static LayoutAreaReference GetCatalogReference(string collection) =>
        new(nameof(Catalog)) { Id = $"{collection}" };
}


public static class FileSource
{
    public const string EmbeddedResource = nameof(EmbeddedResource);
}
