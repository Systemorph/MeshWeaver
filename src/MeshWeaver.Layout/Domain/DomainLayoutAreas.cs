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
    /// Returns an actionable, localized caution when no type is specified and when the named type is
    /// not a collection of this host's workspace.
    /// </summary>
    /// <param name="area">The layout area host providing workspace and type-registry access.</param>
    /// <param name="ctx">The rendering context for this area.</param>
    /// <returns>A catalog UI control for the requested type.</returns>
    // 🚨 THE GUARD MUST INTERROGATE THE REGISTRY THE RENDER ACTUALLY READS (#5065). This resolved the
    // type from the hub's ITypeRegistry and then handed it to a render that streams
    // `Workspace.GetStream(new CollectionReference(...))` — i.e. it checked one registry and read
    // another. The two are NOT the same set and are not meant to be: ITypeRegistry is the hub's
    // SERIALIZATION registry, which every content type a NodeType declares lands in (DataContext
    // registers each type source into it, and PolymorphicTypeInfoResolver adds any type the hub
    // merely serialises), while `DataContext.DataSourcesByCollection` holds only the collections a
    // data source MAPS. A type in the first and not the second walked straight past the guard and
    // died three frames deeper in `WorkspaceStreams.CreateWorkspaceStream`, as a bare
    // `ArgumentException: Collections Harness are not mapped to any source` — an unhandled throw out
    // of the render, so `LayoutAreaHost` logged it at ERROR ("Rendering failed for area Catalog",
    // which auto-files an incident) and put that framework sentence in the viewer's face for what is
    // an AUTHORING mistake: `@@Catalog/Harness` naming a type that is not a workspace collection.
    //
    // `DataContext.GetTypeSource(collection)` is the right question because it is the SAME map, keyed
    // the same way: `DataContext.Initialize` builds `TypeSources` from `typeSource.CollectionName`
    // and `DataSourcesByCollection` from `TypeRegistry.GetCollectionName(mappedType)` in the same
    // pass, and it is also the set `AddTypesCatalogs` enumerates to OFFER catalog links. So after
    // this the accept set and the offer set are one set, which is the invariant that stops the guard
    // drifting from the read again.
    [Browsable(false)]
    public static UiControl Catalog(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Reference.Id is not string collection)
            // "Catalog" lists all entities of a registered data TYPE, so it needs one. Written bare
            // (@@Catalog) it has no Id — that used to throw a dead-end "No type specified" error.
            // Guide the author to the right syntax instead: a type catalog needs the type, and a
            // Space's own contents are the separate node-children "Search" area.
            return Error(area.Localize("catalog.needsType"));

        var typeDefinition = area.Workspace.DataContext.GetTypeSource(collection)?.TypeDefinition;
        if (typeDefinition == null)
            // An author naming a type this workspace does not hold as a collection is an authoring
            // fault, so it renders as the same actionable caution as the missing-Id case above and
            // is NOT thrown: a throw here is reported as a rendering DEFECT and files an incident.
            // The type's own name is all an author needs; the framework's map is not their business.
            return Error(area.Localize("catalog.notAWorkspaceCollection", collection));

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
