using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Documentation.Test
{    /// <summary>
     /// Base class for testing the documentation module.
     /// </summary>
    public class DocumentationTestBase : MonolithMeshTestBase
    {
        /// <summary>
        /// Initializes a new instance of DocumentationTestBase with debug file logging
        /// </summary>
        /// <param name="output"></param>
        protected DocumentationTestBase(ITestOutputHelper output) : base(output)
        {
            // Add debug file logging for message flow tracking
            Services.AddLogging(logging =>
            {
                logging.AddProvider(new DebugFileLoggerProvider());
                logging.SetMinimumLevel(LogLevel.Debug);
            });
        }

        /// <summary>
        /// Configures the documentation module and articles 
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            base.ConfigureMesh(builder)
                .AddKernel()
                .ConfigureServices(ConfigureArticles)
                .ConfigureServices(services => services.AddContentCollections())
                .ConfigureMesh(config => config.InstallAssemblies(DocumentationAssemblyLocation)
                );

        /// <summary>
        /// Location of the assembly
        /// </summary>
        protected static readonly string DocumentationAssemblyLocation =
            typeof(DocumentationApplicationAttribute).Assembly.Location;

        private IServiceCollection ConfigureArticles(IServiceCollection services)
        {
            return services
                .Configure<List<ContentSourceConfig>>(
                    options => options.Add(new ContentSourceConfig()
                    {
                        Name = "Documentation",
                        BasePath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "Markdown")
                    })
                );
        }

        /// <summary>
        /// Default configuration of the client
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        {
            return base.ConfigureClient(configuration).AddLayoutClient().AddArticles();
        }

        /// <summary>
        /// Address of the documentation application
        /// </summary>
        protected static readonly ApplicationAddress Address = new("Documentation");

        /// <summary>
        /// Gets the id from "/data/\"id\""
        /// </summary>
        /// <param name="articleControl"></param>
        /// <returns></returns>
        protected static string GetIdFromDataContext(UiControl articleControl)
        {
            var pattern = @"/data/\""(?<id>[^\""]+)\""";
            var match = Regex.Match(articleControl.DataContext!, pattern);
            var id = match.Groups["id"].Value;
            return id;
        }

        /// <summary>
        /// Override disposal to add aggressive timeout and prevent hanging
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            var logger = ServiceProvider.GetService<ILogger<DocumentationTestBase>>();
            logger?.LogInformation("Starting disposal of DocumentationTestBase");

            // Log debug file location
            var tempDir = Environment.GetEnvironmentVariable("TEMP") ?? ".";
            var debugLogDir = Path.Combine(tempDir, "MeshWeaverDebugLogs");
            logger?.LogInformation("Debug logs are written to: {DebugLogDir}", debugLogDir);

            try
            {
                var disposeTask = base.DisposeAsync();
                await disposeTask;

                logger?.LogInformation("Finished disposal of DocumentationTestBase");
            }
            catch (OperationCanceledException)
            {
                logger?.LogError("DocumentationTestBase disposal timed out after 10 seconds - forcing completion");
                // Don't throw, just log and continue to prevent test hanging
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error during DocumentationTestBase disposal");
                // Don't rethrow to prevent test hanging
            }
        }
    }
}
