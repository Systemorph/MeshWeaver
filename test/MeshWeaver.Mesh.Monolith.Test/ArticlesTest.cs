using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Views;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class ArticlesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Test = nameof(Test);
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddKernel()
            .AddArticles(articles => articles
                .WithCollection(
                    new FileSystemArticleCollection(
                        Test, 
                        Path.Combine(GetAssemblyLocation(), "Markdown"))
                ))
            .ConfigureMesh(config => config.AddMeshNodes(
                TestHubExtensions.Node
            ));

    private string GetAssemblyLocation()
    {
        var location = GetType().Assembly.Location;
        return Path.GetDirectoryName(location);
    }


    [Fact]
    public async Task BasicArticle()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetRemoteStream(new ArticlesAddress(Test),
            new LayoutAreaReference("Article"){Id = "Overview"});

        var control = await articleStream
            .GetControlStream("Article")
            .Timeout(3.Seconds())
            .FirstAsync(x => x is not null);

        var articleControl = control.Should().BeOfType<ArticleControl>().Subject;
        articleControl.Name.Should().Be("Overview");
        articleControl.Content.Should().BeNull();
        articleControl.Html.Should().NotBe(null);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }
}
