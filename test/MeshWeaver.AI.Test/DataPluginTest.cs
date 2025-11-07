using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for DataPlugin functionality
/// </summary>
public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Tests that the DataPlugin can create a valid set of AI tools.
    /// </summary>
    [Fact]
    public void CreateTools_ShouldReturnValidTools()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new DataPlugin(client, mockChat);

        // act
        var tools = plugin.CreateTools();

        // assert
        tools.Should().NotBeNull();
        tools.Should().NotBeEmpty();
        tools.Should().HaveCountGreaterThan(0);
    }

    /// <summary>
    /// Tests that GetDataTypes returns an appropriate message when no context is available.
    /// </summary>
    [Fact]
    public async Task GetDataTypes_WithoutContext_ShouldReturnMessage()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = null };
        var plugin = new DataPlugin(client, mockChat);

        // act
        var result = await plugin.GetDataTypes();

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("navigate to a context");
    }

    /// <summary>
    /// Tests that GetSchema returns a non-empty schema for a known type.
    /// </summary>
    [Fact]
    public async Task GetSchema_KnownType_ShouldReturnNonEmptySchema()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new DataPlugin(client, mockChat);

        // act
        var result = await plugin.GetSchema(nameof(TestType));

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().NotBe("{}");
    }

    /// <summary>
    /// Tests that GetSchema returns an empty schema object for unknown types.
    /// </summary>
    [Fact]
    public async Task GetSchema_ForUnknownType_ShouldReturnEmptySchema()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new DataPlugin(client, mockChat);

        // act
        var result = await plugin.GetSchema("UnknownType");

        // assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Be("{}");
    }
    /// <summary>
    /// Configuration for test host
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source => source.WithType<TestType>()));
    }


    /// <summary>
    /// Test type used for testing the DataPlugin functionality.
    /// </summary>
    /// <param name="Id">Id field</param>
    /// <param name="Name">name field</param>
    public record TestType([property: Key] string Id, string Name);

    private class MockAgentChat : IAgentChat
    {

        public AgentContext? Context { get; set; }

        public void SetContext(AgentContext? applicationContext) => Context = applicationContext;

        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;

        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public void SetThreadId(string threadId)
            => throw new NotImplementedException();

        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl)
        {
            throw new NotImplementedException();
        }

    }

}
