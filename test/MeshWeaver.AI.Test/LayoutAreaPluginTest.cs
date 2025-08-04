using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for LayoutAreaPlugin functionality including retrieving layout area definitions and listings
/// </summary>
/// <param name="output">Test output helper for logging</param>
public class LayoutAreaPluginTest(ITestOutputHelper output) : HubTestBase(output)
{

    /// <summary>
    /// Configures the host message hub for layout area plugin testing
    /// </summary>
    /// <param name="configuration">The message hub configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(layout =>
                layout
                    .WithView(nameof(TestArea1), TestArea1)
                    .WithView(nameof(TestArea2), TestArea2)
                    .WithView(nameof(TestArea3), TestArea3)
            );
    }

    /// <summary>
    /// The TestArea1 layout area definition
    /// </summary>
    /// <param name="host"></param>
    /// <param name="_"></param>
    /// <returns></returns>
    private static UiControl TestArea1(LayoutAreaHost host, RenderingContext _)
        => Controls.Html("Test Area 1 Content");

    /// <summary>
    /// Another test layout area
    /// </summary>
    /// <param name="host"></param>
    /// <param name="_"></param>
    /// <returns></returns>
    private static UiControl TestArea2(LayoutAreaHost host, RenderingContext _)
        => Controls.Html("Test Area 2 Content");

    /// <summary>
    /// The TestArea3 layout area definition
    /// </summary>
    /// <param name="host"></param>
    /// <param name="_"></param>
    /// <returns></returns>
    private static UiControl TestArea3(LayoutAreaHost host, RenderingContext _)
        => Controls.Html("Test Area 3 Content");

    /// <summary>
    /// Configures the client message hub for layout area plugin testing
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration).AddLayout(x => x);

    /// <summary>
    /// Tests that GetLayoutAreasRequest returns all available layout areas
    /// </summary>
    [Fact]
    public async Task GetLayoutAreasRequest_ShouldReturnAvailableAreas()
    {
        // arrange
        var client = GetClient();

        // act
        var response = await client.AwaitResponse(
            new GetLayoutAreasRequest(),
            o => o.WithTarget(new HostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );

        // assert
        var areasResponse = response.Message.Should().BeOfType<LayoutAreasResponse>().Which;
        areasResponse.Areas.Should().NotBeEmpty();

        // Should contain our test areas
        var testAreas = areasResponse.Areas.Where(a => a.Area.StartsWith("TestArea")).ToArray();
        testAreas.Should().HaveCount(3);
        testAreas.Should().Contain(a => a.Area == "TestArea1");
        testAreas.Should().Contain(a => a.Area == "TestArea2");
        testAreas.Should().Contain(a => a.Area == "TestArea3");
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin GetLayoutAreas function
    /// </summary>
    [Fact]
    public async Task LayoutAreaPlugin_GetLayoutAreas_ShouldReturnJsonString()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new LayoutAreaPlugin(client, mockChat);

        // act
        var result = await plugin.GetLayoutAreas();

        // assert
        result.Should().NotBeNullOrEmpty();
        
        // Verify it's valid JSON
        var jsonDoc = JsonDocument.Parse(result);
        jsonDoc.Should().NotBeNull();

        // Verify it contains layout areas
        var areas = jsonDoc.RootElement.EnumerateArray().ToArray();
        areas.Should().NotBeEmpty();

        // Should contain our test areas
        var testAreas = areas.Where(a => 
            a.TryGetProperty("area", out var areaProp) && 
            areaProp.GetString()?.StartsWith("TestArea") == true).ToArray();
        testAreas.Should().HaveCount(3);
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin DisplayLayoutArea function for existing area
    /// </summary>
    [Fact]
    public void LayoutAreaPlugin_GetLayoutArea_ForExistingArea_ShouldReturnUrl()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new LayoutAreaPlugin(client, mockChat);
        var areaName = "TestArea1";

        // act
        var result = plugin.DisplayLayoutArea(areaName);

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().StartWith("@");
        result.Should().Contain(areaName);
        result.Should().Be($"@{new HostAddress()}/{areaName}");
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin DisplayLayoutArea function with id parameter
    /// </summary>
    [Fact]
    public void LayoutAreaPlugin_GetLayoutArea_WithId_ShouldReturnUrlWithId()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new LayoutAreaPlugin(client, mockChat);
        var areaName = "TestArea1";
        var id = "testId";

        // act
        var result = plugin.DisplayLayoutArea(areaName, id);

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().StartWith("@");
        result.Should().Contain(areaName);
        result.Should().Contain(id);
        result.Should().Be($"@{new HostAddress()}/{areaName}/{id}");
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin DisplayLayoutArea function for area without address
    /// </summary>
    [Fact]
    public void LayoutAreaPlugin_GetLayoutArea_WithoutAddress_ShouldReturnErrorMessage()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = null };
        var plugin = new LayoutAreaPlugin(client, mockChat);
        var areaName = "TestArea1";

        // act
        var result = plugin.DisplayLayoutArea(areaName);

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("No address defined");
        result.Should().Contain(areaName);
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin with predefined area definitions
    /// </summary>
    [Fact]
    public async Task LayoutAreaPlugin_WithPredefinedAreas_ShouldUsePredefinedAreas()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };

        var getAreasResponse = await client
            .AwaitResponse(
                new GetLayoutAreasRequest(), 
                o => o.WithTarget(new HostAddress())
                , CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(3.Seconds()).Token).Token
                );

        var predefinedAreas = getAreasResponse.Message
            .Should().BeOfType<LayoutAreasResponse>()
            .Which.Areas
            .Select(a => new LayoutAreaDefinition(a.Area, a.Url))
            .ToDictionary(x => x.Area);
        var plugin = new LayoutAreaPlugin(client, mockChat, predefinedAreas);

        // act
        var result = await plugin.GetLayoutAreas();

        // assert
        result.Should().NotBeNullOrEmpty();
        
        // Verify it's valid JSON
        var jsonDoc = JsonDocument.Parse(result);
        jsonDoc.Should().NotBeNull();

        // Verify it contains exactly the predefined areas
        var areas = jsonDoc.RootElement.EnumerateArray().ToArray();
        areas.Should().HaveCountGreaterThan(3);
        
        var areaNames = areas.Select(a => a.GetProperty("area").GetString()).ToArray();
        areaNames.Should().Contain("TestArea1");
        areaNames.Should().Contain("TestArea2");
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin without agent context should return appropriate message
    /// </summary>
    [Fact]
    public async Task LayoutAreaPlugin_WithoutAgentContext_ShouldReturnContextMessage()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = null };
        var plugin = new LayoutAreaPlugin(client, mockChat);

        // act
        var result = await plugin.GetLayoutAreas();

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("navigate to a context");
    }

    /// <summary>
    /// Tests the LayoutAreaPlugin CreateKernelPlugin functionality
    /// </summary>
    [Fact]
    public void LayoutAreaPlugin_CreateKernelPlugin_ShouldReturnValidPlugin()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new LayoutAreaPlugin(client, mockChat);

        // act
        var kernelPlugin = plugin.CreateKernelPlugin();

        // assert
        kernelPlugin.Should().NotBeNull();
        kernelPlugin.Name.Should().Be(nameof(LayoutAreaPlugin));
        kernelPlugin.FunctionCount.Should().Be(2);
        kernelPlugin.GetFunctionsMetadata().Should().Contain(f => f.Name == nameof(LayoutAreaPlugin.GetLayoutAreas));
        kernelPlugin.GetFunctionsMetadata().Should().Contain(f => f.Name == nameof(LayoutAreaPlugin.DisplayLayoutArea));
    }

    /// <summary>
    /// Mock implementation of IAgentChat for testing
    /// </summary>
    private class MockAgentChat : IAgentChat
    {
        public void SetContext(AgentContext applicationContext)
        {
            throw new NotImplementedException();
        }

        public Task ResumeAsync(ChatConversation conversation)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public string Delegate(string agentName, string message, bool askUserFeedback = false)
        {
            throw new NotImplementedException();
        }

        public AgentContext? Context { get; set; }
    }
}
