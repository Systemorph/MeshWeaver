using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

/// <summary>
/// Registers and implements the standard domain layout areas: Catalog, Details, and DataModel.
/// </summary>
public static class DomainLayoutAreas
{
    /// <summary>
    /// Registers the Catalog, Details, and DataModel standard views into the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to extend.</param>
    /// <returns>The layout definition with the domain views registered.</returns>
    public static LayoutDefinition AddDomainLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithView(nameof(Catalog), Catalog)
            .WithView(nameof(Details), Details)
            .WithView(nameof(DataModelLayoutArea.DataModel), DataModelLayoutArea.DataModel, area => area.WithDescription($"The data model for the domain behind {layout.Hub.Address}."));



    /// <summary>URL segment key used to carry the type name in detail-area references.</summary>
    public const string Type = nameof(Type);

    /// <summary>
    /// Renders a detail view for a single entity identified by "Type/Id" in the area reference Id.
    /// Returns an error control when the URL is malformed or the type is not registered.
    /// </summary>
    /// <param name="area">The layout area host providing workspace and type-registry access.</param>
    /// <param name="ctx">The rendering context for this area.</param>
    /// <returns>A UI control showing the entity's edit form, or an error markdown control.</returns>
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
            return EditLayoutArea.Edit(area, typeDefinition, id, ctx);
        }
        catch (Exception e)
        {
            return Error($"Exception while displaying details for Type {type} and id {parts[1]}: \n{e}");
        }
    }




    private static MarkdownControl Error(string message) => new($"[!CAUTION]\n{message}\n");

    /// <summary>
    /// Renders a catalog view listing all entities of the type named in the area reference Id.
    /// Throws when no type is specified or the type is not registered in the data context.
    /// </summary>
    /// <param name="area">The layout area host providing workspace and type-registry access.</param>
    /// <param name="ctx">The rendering context for this area.</param>
    /// <returns>A catalog UI control for the requested type.</returns>
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

    

    /// <summary>
    /// Builds navigation controls for all registered types in the host's data context,
    /// grouping them under <see cref="NavGroupControl"/> when a group name is present.
    /// </summary>
    /// <param name="host">The layout area host whose data context types are enumerated.</param>
    /// <returns>An enumerable of nav-link and nav-group controls, one per registered type.</returns>
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


    /// <summary>
    /// Returns a <see cref="LayoutAreaReference"/> for the Details view of the entity with the given type and id,
    /// or null when the type is not registered in the type registry.
    /// </summary>
    /// <param name="hub">The message hub providing the type registry.</param>
    /// <param name="type">The CLR type of the entity.</param>
    /// <param name="id">The entity's key value.</param>
    /// <returns>A reference to the Details area for the entity, or null if the type has no registered collection name.</returns>
    public static LayoutAreaReference? GetDetailsReference(this IMessageHub hub, Type type, object id)
    {
        var collection = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetCollectionName(type);
        if (collection == null)
            return null;
        return GetDetailsReference(collection, id);
    }

    /// <summary>
    /// Returns a <see cref="LayoutAreaReference"/> for the Details view of the entity identified by <paramref name="collection"/> and <paramref name="id"/>.
    /// </summary>
    /// <param name="collection">The collection name (type identifier) of the entity.</param>
    /// <param name="id">The entity's key value.</param>
    /// <returns>A reference to the Details area with Id set to "collection/id".</returns>
    public static LayoutAreaReference GetDetailsReference(string collection, object id) =>
        new(nameof(Details)) { Id = $"{collection}/{id}" };
    /// <summary>
    /// Returns a <see cref="LayoutAreaReference"/> for the Catalog view of the given collection.
    /// </summary>
    /// <param name="collection">The collection name (type identifier) to display in the catalog.</param>
    /// <returns>A reference to the Catalog area for the specified collection.</returns>
    public static LayoutAreaReference GetCatalogReference(string collection) =>
        new(nameof(Catalog)) { Id = $"{collection}" };
}


/// <summary>
/// Constants identifying the source mechanism for file-based data loading in the domain.
/// </summary>
public static class FileSource
{
    /// <summary>Identifies files that are loaded from assembly embedded resources.</summary>
    public const string EmbeddedResource = nameof(EmbeddedResource);
}
