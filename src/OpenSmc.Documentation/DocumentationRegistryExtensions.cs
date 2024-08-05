using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public static class DocumentationRegistryExtensions
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
        .AddLayout(layout => layout.AddDocumentation()
            //.AddSources()
        );

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

    //public static object Source(LayoutAreaHost area, RenderingContext context)
    //{
    //    var id = area.Stream.Reference.Id as SourceItem;
    //    if (id == null)
    //        return Controls.Markdown($":warning: {area.Stream.Reference.Id} is not a valid SourceItem.");

    //    var sources = area.Hub.GetDocumentationService().GetSources(id.Assembly)?.GetValueOrDefault(id.Type);
    //    if(sources == null)
    //        return Controls.Markdown($":warning: {id.Type} does not have any sources.");

    //    return Controls.Markdown($"```C#\n{sources}\n```");
    //}

}
