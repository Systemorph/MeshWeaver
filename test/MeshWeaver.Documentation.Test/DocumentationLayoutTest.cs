using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hub.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Testing the documentation layout.
/// </summary>
/// <param name="output"></param>
public class DocumentationLayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configuration of client
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient(x => x);
    }

    /// <summary>
    /// Configure the documentation service
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return configuration.ConfigureDocumentationTestHost();
    }

    /// <summary>
    /// Tests the basic layout
    /// </summary>
    /// <returns></returns>
    [HubFact]
    public async Task BasicLayout()
    {
        var reference = new LayoutAreaReference(DocumentationHubConfiguration.HtmlView)
        {
            Layout = DocumentationLayout.Documentation
        };

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        var control = await stream
            .GetControlStream(DocumentationLayout.Documentation)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var tabs = control
            .Should()
            .BeOfType<TabsControl>()
            .Which;

        tabs.Areas.Should()
            .HaveCount(2);

        control = await stream
            .GetControlStream(tabs.Areas.First().Area.ToString())
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        
        var named = control.Should()
            .BeOfType<NamedAreaControl>().Which;

        named.Area.Should().Be(DocumentationHubConfiguration.HtmlView);

        control = await stream
            .GetControlStream(named.Area.ToString())
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<HtmlControl>();
    }
}
