using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
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
                    new FileSystemCollection(
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
        var articleStream = client.GetWorkspace().GetRemoteStream(new ArticleAddress(Test),
            ArticleLayoutArea.GetArticleLayoutReference("Overview.md"));

        var control = await articleStream
            .GetControlStream("Article")
            .Timeout(3.Seconds())
            .FirstAsync(x => x is not null);

        var html = control.Should().BeOfType<HtmlControl>().Subject;
        html.Skins.Should().HaveCount(1);
        html.Skins[0].Should().BeOfType<ArticleSkin>().Subject.Name.Should().Be("Overview");
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }
}
