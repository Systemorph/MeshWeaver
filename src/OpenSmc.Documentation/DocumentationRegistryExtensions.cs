using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
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
        .AddLayout(layout => layout.AddFiles());

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

    private static LayoutDefinition AddFiles(this LayoutDefinition builder, string area = nameof(File)) =>
        builder.WithView(area, File);

    private static object File(LayoutAreaHost area, RenderingContext _)
    {
        if (area.Stream.Reference.Id is not string fileName)
            throw new InvalidOperationException("No file name specified.");


        var documentationService = area.Hub.GetDocumentationService();


        var text = "";
        using var stream = documentationService.GetStream(fileName);
        if (stream == null)
            // Resource not found, return a warning control/message instead
            text = $":error: **File not found**: {fileName}";

        else
        {
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }

        return new MarkdownControl(text);
    }

}
