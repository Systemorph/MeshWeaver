using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Utility;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test;


/// <summary>
/// Tests the calculator area
/// </summary>
/// <param name="output"></param>
public class CalculatorTest(ITestOutputHelper output) : DocumentationTestBase(output)
{    /// <summary>
     /// Tests the calculator area by means of the article
     /// </summary>
     /// <returns></returns>
    [Fact(Timeout = 60000)] // 60 second timeout
    public async Task CalculatorThroughArticle()
    {

        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(new LayoutAreaReference("Content") { Id = "Documentation/Calculator" });

        var control = await articleStream
            .GetControlStream("Content")
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var articleControl = control.Should().BeOfType<ArticleControl>().Which;
        var articleReference = articleControl.Article.Should().BeOfType<JsonPointerReference>().Which;
        var id = GetIdFromDataContext(articleControl);
        var entity = await articleStream.GetDataAsync(id).Timeout(5.Seconds());
        var article = entity.Should().BeOfType<Article>().Which;
        article.Name.Should().Be("Calculator");
        article.Content.Should().NotBeNull();
        article.PrerenderedHtml.Should().NotBeNull();
        var kernelAddress = new KernelAddress();
        foreach (var s in article.CodeSubmissions)
            client.Post(s, o => o.WithTarget(kernelAddress));

        var html = article.PrerenderedHtml.ToString()!.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, kernelAddress.ToString());

        var (addressString, area) = HtmlParser
            .ExtractDataAddressAttributes(html)
            .Single();
        var address = client.GetAddress(addressString);
        var calcStream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(address, new(area));
        control = await calcStream.GetControlStream(area)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        control = await calcStream.GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("3");
    }

}
