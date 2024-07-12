using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Documentation.Markdown;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public static class DocumentationRegistryExtensions
{
    public static void AddDocumentationRegistry(this MessageHubConfiguration configuration) =>
        configuration.WithServices(services => services.AddSingleton<MarkdownComponentParser>());
}
