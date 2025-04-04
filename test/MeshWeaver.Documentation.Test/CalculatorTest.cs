﻿using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Views;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
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
        var articleStream = client.RenderArticle("Documentation","Calculator");

        var control = await articleStream
            .Timeout(10.Seconds())
            .FirstAsync();

        var article = control.Should().BeOfType<ArticleControl>().Which;
        article.Name.Should().Be("Calculator");
        article.Content.Should().BeNull();
        article.Html.Should().NotBeNull();
        var kernelAddress = new KernelAddress();
        foreach (var s in article.CodeSubmissions)
            client.Post(s, o => o.WithTarget(kernelAddress));

        var html = article.Html.ToString()!.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, kernelAddress.ToString());

        var (addressString, area) = HtmlParser
            .ExtractDataAddressAttributes(html)
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
