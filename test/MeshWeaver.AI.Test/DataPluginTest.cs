using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
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
    }

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