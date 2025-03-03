﻿using System.Reflection;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

public static class DomainViews
{
    public static MessageHubConfiguration AddDomainViews(this MessageHubConfiguration config, Func<DomainViewConfiguration, DomainViewConfiguration> configuration = null)
        => config
            .AddLayout(layout => layout
            .WithView(nameof(Catalog), Catalog)
            .WithView(nameof(Details), Details)
            .WithView(nameof(DataModel), DataModel)
            )
            .WithServices(services => services.AddSingleton<IDomainLayoutService>(sp => new DomainLayoutService((configuration ?? (x => x)).Invoke(new(sp.GetRequiredService<IMessageHub>())))));

    private static object DataModel(LayoutAreaHost host, RenderingContext arg)
    {
        return new MarkdownControl(host.GetMermaidDiagram()); 
    }


    public const string Type = nameof(Type);

    public static object Details(LayoutAreaHost area, RenderingContext ctx)
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
            var id = keyType == typeof(string)  ? idString : JsonSerializer.Deserialize(idString, keyType);
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
        if (area.Reference.Id is not string collection)
            throw new InvalidOperationException("No type specified for catalog.");
        var typeDefinition = area.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetTypeDefinition(collection);
        if (typeDefinition == null)
            throw new DataSourceConfigurationException(
                $"Collection {collection} is not mapped in Address {area.Hub.Address}.");

        var context = new EntityRenderingContext(area, typeDefinition, null, null, ctx);
        return area.Hub.ServiceProvider.GetRequiredService<IDomainLayoutService>().GetCatalog(context);

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

    private static readonly HashSet<string> ExcludedMethods =
    [
        "<Clone>$",
        "Deconstruct",
        "ToString",
        "Equals",
        "GetHashCode"
    ];
    private static string GetMermaidDiagram(this LayoutAreaHost host)
    {
        var types = host.GetTypes().ToArray();
        var sb = new StringBuilder();

        // Group types into subgraphs based on some criteria, e.g., namespace or group name
        var groupedTypes = types.GroupBy(t => t.GroupName ?? "Default");

        foreach (var group in groupedTypes)
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine("```mermaid");
            sb.AppendLine("classDiagram");

            foreach (var type in group)
            {
                var typeName = type.Type.Name;
                var collectionName = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                    .GetCollectionName(type.Type);
                var link = $"{host.Hub.Address}/type/{collectionName}";

                sb.AppendLine($"class {typeName} {{");

                // Add properties
                foreach (var prop in type.Type.GetProperties())
                {
                    sb.AppendLine($"  {prop.PropertyType.Name} {prop.Name}");
                }

                // Add methods
                foreach (var method in type.Type
                             .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                             .Where(m => !m.IsSpecialName && !ExcludedMethods.Contains(m.Name)))
                    sb.AppendLine($"  {method.ReturnType.Name} {method.Name}({GetParameters(method)})");
                sb.AppendLine("}");
            }


            // TODO V10: Need to create relationships, but they will be across the diagram.
            //  Need to think of different diagram type. (29.01.2025, Roland Bürgi)
            // Add relationships within the group
            //foreach (var type in group)
            //{
            //    var typeName = type.Type.Name;
            //    foreach (var prop in type.Type.GetProperties())
            //    {
            //        if (group.Any(t => t.Type == prop.PropertyType))
            //        {
            //            sb.AppendLine($"{typeName} --> {prop.PropertyType.Name}");
            //        }
            //    }
            //}

            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static string GetParameters(MethodInfo method)
    {
        return string.Join(", ",
            method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")
        );
    }

    public static string GetDetailsUri(this IMessageHub hub, Type type, object id) =>
        GetDetailsReference(hub, type, id)?.ToHref(hub.Address);

    public static LayoutAreaReference GetDetailsReference(this IMessageHub hub, Type type, object id)
    {
        var collection = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetCollectionName(type);
        if (collection == null)
            return null;
        return GetDetailsReference(collection, id);
    }

    public static LayoutAreaReference GetDetailsReference(string collection, object id) =>
        new(nameof(Details)) { Id = $"{collection}/{id}" };
    public static LayoutAreaReference GetCatalogReference(this IMessageHub hub, string collection) =>
        new(nameof(Catalog)) { Id = $"{collection}" };
}


public static class FileSource
{
    public const string EmbeddedResource = nameof(EmbeddedResource);
}
