using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public static class DocumentationExtensions
{
    public static MessageHubConfiguration AddDocumentation(
        this MessageHubConfiguration hubConf)
        => hubConf.AddDocumentation(x => x);
    public static MessageHubConfiguration AddDocumentation(
        this MessageHubConfiguration hubConf,
        Func<DocumentationContext, DocumentationContext> configuration) => hubConf
        .Set(AddLambda(hubConf.Get<ImmutableList<Func<DocumentationContext, DocumentationContext>>>()
                       ?? ImmutableList<Func<DocumentationContext, DocumentationContext>>.Empty, configuration))
        .WithServices(services => services.AddScoped<IDocumentationService, DocumentationService>())
        .AddLayout(layout => layout.AddDocumentation());

    private static ImmutableList<Func<DocumentationContext, DocumentationContext>> AddLambda(
        ImmutableList<Func<DocumentationContext, DocumentationContext>> existing,
        Func<DocumentationContext, DocumentationContext> configuration)
        => existing.Add(configuration);


    public static IDocumentationService GetDocumentationService(this IMessageHub hub)
        => hub.ServiceProvider.GetRequiredService<IDocumentationService>();

    internal static DocumentationContext CreateDocumentationContext(this IMessageHub hub)
        => hub.Configuration
            .Get<ImmutableList<Func<DocumentationContext, DocumentationContext>>>()
            ?.Aggregate(new DocumentationContext(hub), (context, config) => config(context))
        ?? new(hub);

    public static string DocumentationPath(this LayoutDefinition layout, Assembly assembly, string name)
        => $"{layout.Hub.Address}/Doc/{EmbeddedDocumentationSource.Embedded}/{assembly.GetName().Name}/{name}";
    public static LayoutDefinition AddDocumentationMenu(this LayoutDefinition layout, Assembly assembly)
        => layout.WithNavMenu((menu, _) =>
            layout.Hub.GetDocumentationService().Context.GetSource(EmbeddedDocumentationSource.Embedded, assembly.GetName().Name)
                ?.DocumentPaths
                .Aggregate(menu, (m, i) => m.WithNavLink(i.Key, layout.DocumentationPath(assembly, i.Key))));

}
