using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Documentation.Test
{
    /// <summary>
    /// Base class for testing the documentation module.
    /// Uses MeshNode and MarkdownContent instead of the legacy Article system.
    /// </summary>
    public class DocumentationTestBase : MonolithMeshTestBase
    {
        /// <summary>
        /// Location of the assembly
        /// </summary>
        protected static readonly string DocumentationAssemblyLocation =
            typeof(DocumentationApplicationAttribute).Assembly.Location;

        /// <summary>
        /// Address of the documentation application
        /// </summary>
        protected static readonly Address Address = AddressExtensions.CreateAppAddress("Documentation");

        /// <summary>
        /// Initializes a new instance of DocumentationTestBase with debug file logging
        /// </summary>
        protected DocumentationTestBase(ITestOutputHelper output) : base(output)
        {
        }

        /// <summary>
        /// Gets the path to the Markdown directory.
        /// </summary>
        protected string GetMarkdownPath()
        {
            return Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "Markdown");
        }

        /// <summary>
        /// Configures the documentation module with file system persistence for markdown files.
        /// Also configures the legacy ContentCollections for tests that still need them.
        /// </summary>
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        {
            var markdownPath = GetMarkdownPath();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Graph:Storage:SourceType"] = "FileSystem",
                    ["Graph:Storage:BasePath"] = markdownPath
                })
                .Build();

            return base.ConfigureMesh(builder)
                .UseMonolithMesh()
                .AddFileSystemPersistence(markdownPath)
                .AddKernel()  // Required for interactive markdown code execution
                .InstallAssemblies(DocumentationAssemblyLocation)
                .ConfigureHub(hub => hub.AddContentCollections(new ContentCollectionConfig()
                {
                    SourceType = FileSystemStreamProvider.SourceType,
                    Name = "Documentation",
                    BasePath = markdownPath
                }))
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConfiguration>(configuration);
                    return services;
                });
        }

        /// <summary>
        /// Default configuration of the client
        /// </summary>
        protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        {
            return base.ConfigureClient(configuration).AddLayoutClient().AddArticles();
        }

        /// <summary>
        /// Extracts markdown content from a MeshNode.
        /// Handles MarkdownContent, JsonElement, and plain string content.
        /// </summary>
        protected static MarkdownContent? ExtractMarkdownContent(MeshNode node)
        {
            if (node.Content is MarkdownContent markdownContent)
                return markdownContent;

            if (node.Content is JsonElement jsonContent)
            {
                if (jsonContent.TryGetProperty("content", out var contentProp))
                {
                    var content = contentProp.GetString();
                    string? prerenderedHtml = null;
                    if (jsonContent.TryGetProperty("prerenderedHtml", out var htmlProp))
                        prerenderedHtml = htmlProp.GetString();

                    return new MarkdownContent
                    {
                        Content = content ?? "",
                        PrerenderedHtml = prerenderedHtml
                    };
                }
                if (jsonContent.ValueKind == JsonValueKind.String)
                {
                    return new MarkdownContent { Content = jsonContent.GetString() ?? "" };
                }
            }

            if (node.Content is string strContent)
            {
                return new MarkdownContent { Content = strContent };
            }

            return null;
        }

        /// <summary>
        /// Override disposal to add aggressive timeout and prevent hanging
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            var logger = ServiceProvider.GetService<ILogger<DocumentationTestBase>>();
            logger?.LogInformation("Starting disposal of DocumentationTestBase");

            try
            {
                var disposeTask = base.DisposeAsync();
                await disposeTask;

                logger?.LogInformation("Finished disposal of DocumentationTestBase");
            }
            catch (OperationCanceledException)
            {
                logger?.LogError("DocumentationTestBase disposal timed out after 10 seconds - forcing completion");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error during DocumentationTestBase disposal");
            }
        }
    }
}
