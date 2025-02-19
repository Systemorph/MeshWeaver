using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using HtmlAgilityPack;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Views;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
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
            .AddArticles()
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
                    Name = "Test", BasePath = Path.Combine(GetAssemblyLocation(), "Markdown")
                })
            );
    }


    private string GetAssemblyLocation()
    {
        var location = GetType().Assembly.Location;
        return Path.GetDirectoryName(location);
    }


    [Fact]
    public virtual async Task BasicArticle()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(
            new LayoutAreaReference("Article") { Id = "Test/Overview" });

        var control = await articleStream
            .GetControlStream("Article")
            .Timeout(3.Seconds())
            .FirstAsync(x => x is not null);

        var articleControl = control.Should().BeOfType<ArticleControl>().Subject;
        articleControl.Name.Should().Be("Overview");
        articleControl.Content.Should().BeNull();
        articleControl.Html.Should().NotBe(null);
    }
    [Fact]
    public virtual async Task NotFound()
    {
        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(
            new LayoutAreaReference("Article") { Id = "Test/NotFound" });

        var control = await articleStream
            .GetControlStream("Article")
            .Timeout(3.Seconds())
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
            .Timeout(3.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCount(3);

        var articles = await stack.Areas.ToAsyncEnumerable()
            .SelectAwait(async a => await articleStream.GetControlStream(a.Area.ToString()).FirstAsync())
            .ToArrayAsync();

        articles.Should().HaveCount(3);
        articles.First().Should().BeOfType<ArticleCatalogItemControl>()
            .Which.Article.Should().BeOfType<Article>()
            .Which.Name.Should().Be("ReadMe");
    }

    [Fact]
    public virtual async Task CalculatorThroughArticle()
    {

        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(
            new LayoutAreaReference("Article") { Id = "Test/Calculator" });

        var control = await articleStream
            .GetControlStream("Article")
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var articleControl = control.Should().BeOfType<ArticleControl>().Subject;
        articleControl.Name.Should().Be("Calculator");
        articleControl.Content.Should().BeNull();
        articleControl.Html.Should().NotBe(null);
        var (addressString, area) = HtmlParser
            .ExtractDataAddressAttributes(articleControl.Html.ToString())
            .Single();
        var address = client.GetAddress(addressString);
        var calcStream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(address, new(area));
        control = await calcStream.GetControlStream(area)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        control = await calcStream.GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("3");
    }


    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayout(x => x.AddArticleLayouts());
    }
}

public static class HtmlParser
{
    public static List<(string Address, string Area)> ExtractDataAddressAttributes(string htmlContent)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        var nodes = doc.DocumentNode.SelectNodes("//*[@data-address]");
        return nodes?.Select(node => 
            (node.GetAttributeValue("data-address", string.Empty), node.GetAttributeValue("data-area", string.Empty))
            ).ToList() ?? new();
    }
}
