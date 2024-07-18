using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public static class DocumentationRegistryExtensions
{
    public static MessageHubConfiguration AddDocumentation(
        this MessageHubConfiguration hubConf,
        Func<DocumentationContext, DocumentationContext> configuration) => hubConf
        .Set(AddLambda(hubConf.Get<ImmutableList<Func<DocumentationContext, DocumentationContext>>>()
                       ?? ImmutableList<Func<DocumentationContext, DocumentationContext>>.Empty, configuration))
        .WithServices(services => services.AddScoped<IDocumentationService, DocumentationService>())
        .AddLayout(layout => layout.AddDocuments());

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

    private static LayoutDefinition AddDocuments(this LayoutDefinition builder, string area = nameof(Doc)) =>
        builder.WithView(area, Doc);

    private static async Task<object> Doc(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken)
    {
        if (area.Stream.Reference.Id is not string name)
            throw new InvalidOperationException("No file name specified.");


        var documentationService = area.Hub.GetDocumentationService();


        var text = "";
        using var stream = documentationService.GetStream(name);
        if (stream == null)
            // Resource not found, return a warning control/message instead
            text = $":error: **File not found**: {name}";

        else
        {
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(cancellationToken);
        }

        return new MarkdownControl(text);
    }

}
