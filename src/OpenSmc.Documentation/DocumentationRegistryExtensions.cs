using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public static class DocumentationRegistryExtensions
{
    public static MessageHubConfiguration AddDocumentation(
        this MessageHubConfiguration hubConf,
        Func<DocumentationContext, DocumentationContext> configuration) => hubConf
        .Set(AddLambda(hubConf.Get<ImmutableList<Func<DocumentationContext, DocumentationContext>>>() 
                       ?? ImmutableList<Func<DocumentationContext, DocumentationContext>>.Empty, configuration))
        .WithServices(services => services.AddScoped<IDocumentationService, DocumentationService>());

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

public record DocumentationContext(IMessageHub Hub)
{
    public ImmutableDictionary<string, DocumentationSource> Sources { get; init; } = ImmutableDictionary<string, DocumentationSource>.Empty;

    public DocumentationContext WithEmbeddedResourcesFrom(Assembly assembly,
        Func<EmbeddedResourceDocumentationSource, EmbeddedResourceDocumentationSource> configuration) =>
        this with
        {
            Sources = Sources.Add(assembly.GetName().Name,
                configuration.Invoke(new(assembly.GetName().Name, assembly)))
        };

}

public abstract record DocumentationSource(string Id)
{
    internal ImmutableList<string> XmlComments { get; set; } = ImmutableList<string>.Empty;
    internal ImmutableList<string> FilePaths { get; set; } = ImmutableList<string>.Empty;
    public abstract Stream GetStream(string name);
}

public abstract record DocumentationSource<TSource>(string Id) : DocumentationSource(Id)
    where TSource:DocumentationSource<TSource>
{
    public TSource This => (TSource)this;

    public TSource WithXmlComments(string xmlCommentPath = null)
        => This with { XmlComments = XmlComments.Add(xmlCommentPath ?? $"{Id}.xml") };

    public TSource WithFilePath(string filePath)
        => This with { FilePaths = FilePaths.Add(filePath) };

}
public record EmbeddedResourceDocumentationSource(string Id, Assembly Assembly) : 
    DocumentationSource<EmbeddedResourceDocumentationSource>(Id)
{
    public override Stream GetStream(string name)
        => Assembly.GetManifestResourceStream(name);
}
