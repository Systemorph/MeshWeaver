using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Utility;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;


public class ArticlesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Test = nameof(Test);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddKernel()
            .AddMeshNodes(TestHubExtensions.Node)
            ;

    protected virtual MessageHubConfiguration ConfigureContentCollections(MessageHubConfiguration hub) =>
        hub.AddContentCollections(new ContentCollectionConfig()
        {
            SourceType = FileSystemStreamProvider.SourceType,
            Name = Test,
            BasePath = Path.Combine(GetAssemblyLocation(), "Markdown")
        });


    protected string GetAssemblyLocation()
    {
        var location = GetType().Assembly.Location;
        return Path.GetDirectoryName(location)!;
    }


    [Fact]
    public virtual async Task BasicArticle()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(new LayoutAreaReference("Content") { Id = "Test/Overview" });

        var control = await articleStream
            .GetControlStream("Content")
            .Timeout(40.Seconds())
            .FirstAsync(x => x is not null);

        var articleControl = control.Should().BeOfType<ArticleControl>().Subject;
        articleControl.Article.Should().BeOfType<Article>();
        var article = await articleStream
            .GetDataAsync<Article>(GetIdFromDataContext(articleControl))
            .Timeout(40.Seconds());
        article.Name.Should().Be("Overview");
        article.Content.Should().NotBeNull();
        article.PrerenderedHtml.Should().NotBe(null);
    }
    [Fact]
    public virtual async Task NotFound()
    {
        var client = GetClient();
        var articleStream = await client.RenderArticle("Test", "NotFound", TestContext.Current.CancellationToken);

        var control = await articleStream
            .Timeout(20.Seconds())
            .FirstAsync();

        control.Should().BeOfType<MarkdownControl>();
    }
    [Fact]
    public virtual async Task Catalog()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(
            new LayoutAreaReference("Articles") { Id = Test }
            );

        var control = await articleStream
            .GetControlStream("Articles")
            .Timeout(40.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<ArticleCatalogControl>().Which;
        var configs = stack.CollectionConfigurations.Should().BeAssignableTo<IReadOnlyCollection<ContentCollectionConfig>>().Subject;
        var config = configs.Single();
        config.Name.Should().Be(Test);

    }



    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return ConfigureContentCollections(base.ConfigureClient(configuration))
            .AddArticles();
    }

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

}

