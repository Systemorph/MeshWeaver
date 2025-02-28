using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test;


/// <summary>
/// Tests the calculator area
/// </summary>
/// <param name="output"></param>
public class CalculatorTest(ITestOutputHelper output) : DocumentationTestBase(output)
{
    /// <summary>
    /// Tests the calculator area by means of the article
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task CalculatorThroughArticle()
    {

        var client = GetClient();
        var articleStream = client.GetWorkspace().GetStream(
            new LayoutAreaReference("Article") { Id = "Documentation/Calculator" });

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




}
