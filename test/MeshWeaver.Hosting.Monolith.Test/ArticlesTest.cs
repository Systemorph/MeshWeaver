using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Equivalency;
using FluentAssertions.Extensions;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Views;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Utility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;


public class ArticlesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Test = nameof(Test);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddKernel()
            .ConfigureServices(ConfigureArticles)
            .ConfigureServices(services => services.AddArticles())
            .ConfigureMesh(config => config.AddMeshNodes(
                    TestHubExtensions.Node
                )
            );

    protected virtual IServiceCollection ConfigureArticles(IServiceCollection services)
    {
        return services
            .Configure<List<ArticleSourceConfig>>(
                options => options.Add(new ArticleSourceConfig()
                {
                    Name = Test, BasePath = Path.Combine(GetAssemblyLocation(), "Markdown")
                })
            );
    }


    protected string GetAssemblyLocation()
    {
        var location = GetType().Assembly.Location;
        return Path.GetDirectoryName(location);
    }


    [Fact]
    public virtual async Task BasicArticle()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(new LayoutAreaReference("Content") {Id = "Test/Overview"});

        var control = await articleStream
            .GetControlStream("Content")
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        var articleControl = control.Should().BeOfType<ArticleControl>().Subject;
        articleControl.Article.Should().BeOfType<JsonPointerReference>();
        var article = await articleStream
            .GetDataAsync<Article>(GetIdFromDataContext(articleControl))
            .Timeout(20.Seconds());
        article.Name.Should().Be("Overview");
        article.Content.Should().NotBeNull();
        article.PrerenderedHtml.Should().NotBe(null);
    }
    [Fact]
    public virtual async Task NotFound()
    {
        var client = GetClient();
        var articleStream = client.RenderArticle("Test","NotFound");

        var control = await articleStream
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
    }
    [Fact]
    public virtual async Task Catalog()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(
            new LayoutAreaReference("Catalog")
            );

        var control = await articleStream
            .GetControlStream("Catalog")
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCount(2);

        var articles = await stack.Areas.ToAsyncEnumerable()
            .SelectAwait(async a => await articleStream.GetControlStream(a.Area.ToString()).FirstAsync())
            .ToArrayAsync();

        articles.Should().HaveCount(2);
        articles.First().Should().BeOfType<ArticleCatalogItemControl>()
            .Which.Article.Should().BeOfType<Article>()
            .Which.Name.Should().Be("ReadMe");
    }



    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayout(x => x.AddArticleLayouts());
    }

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

