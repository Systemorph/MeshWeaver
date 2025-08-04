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
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for DataPlugin functionality
/// </summary>
public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public void CreateKernelPlugin_ShouldReturnValidPlugin()
    {
        // arrange
        var client = GetClient();
        var mockChat = new MockAgentChat { Context = new AgentContext { Address = new HostAddress() } };
        var plugin = new DataPlugin(client, mockChat);

        // act
        var kernelPlugin = plugin.CreateKernelPlugin();

        // assert
        kernelPlugin.Should().NotBeNull();
        kernelPlugin.Name.Should().Be(nameof(DataPlugin));
    }

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
        
        public void SetContext(AgentContext applicationContext) => Context = applicationContext;
        
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default) 
            => throw new NotImplementedException();
        
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default) 
            => throw new NotImplementedException();
        
        public string Delegate(string agentName, string message, bool askUserFeedback = false) 
            => throw new NotImplementedException();
    }

}
