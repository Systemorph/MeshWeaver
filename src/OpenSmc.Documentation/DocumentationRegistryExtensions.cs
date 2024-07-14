using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Documentation.Markdown;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public static class DocumentationRegistryExtensions
{
    public static MessageHubConfiguration AddDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration; //.WithServices(services => services.AddSingleton<MarkdownService>());
}
