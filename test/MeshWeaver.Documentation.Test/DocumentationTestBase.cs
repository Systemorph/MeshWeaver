using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using MeshWeaver.Articles;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test
{
    /// <summary>
    /// Base class for testing the documentation module.
    /// </summary>
    /// <param name="output"></param>
    public class DocumentationTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        /// <summary>
        /// Configures the documentation module and articles 
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            base.ConfigureMesh(builder)
                .AddKernel()
                .ConfigureServices(ConfigureArticles)
                .ConfigureServices(services => services.AddArticles())
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
                .Configure<List<ArticleSourceConfig>>(
                    options => options.Add(new ArticleSourceConfig()
                    {
                        Name = "Documentation", BasePath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "Markdown")
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
            return base.ConfigureClient(configuration).AddLayoutClient().AddLayout(x => x.AddArticleLayouts());
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
            var match = Regex.Match(articleControl.DataContext, pattern);
            var id = match.Groups["id"].Value;
            return id;
        }

    }
}
