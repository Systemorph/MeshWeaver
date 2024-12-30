using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Data.Documentation;

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
        ;

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

}
