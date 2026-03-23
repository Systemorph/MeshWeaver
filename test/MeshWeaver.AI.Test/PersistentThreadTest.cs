#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace MeshWeaver.AI.Test;

public class PersistentThreadTest
{
    #region Thread Record — PersistentThreadId & ProviderType

    [Fact]
    public void Thread_PersistentThreadId_DefaultIsNull()
    {
        var thread = new Thread();

        thread.PersistentThreadId.Should().BeNull();
        thread.ProviderType.Should().BeNull();
    }

    [Fact]
    public void Thread_WithPersistentThreadId_SetsValue()
    {
        var thread = new Thread
        {
            PersistentThreadId = "thread_abc123",
            ProviderType = "AzureFoundryPersistent"
        };

        thread.PersistentThreadId.Should().Be("thread_abc123");
        thread.ProviderType.Should().Be("AzureFoundryPersistent");
    }

    [Fact]
    public void Thread_WithExpression_CopiesPersistentThreadId()
    {
        var original = new Thread
        {
            PersistentThreadId = "thread_abc123",
            ProviderType = "AzureFoundryPersistent",
            ParentPath = "/some/path"
        };

        var copy = original with { ParentPath = "/other/path" };

        copy.PersistentThreadId.Should().Be("thread_abc123");
        copy.ProviderType.Should().Be("AzureFoundryPersistent");
        copy.ParentPath.Should().Be("/other/path");
    }

    [Fact]
    public void Thread_WithExpression_OverridesPersistentThreadId()
    {
        var original = new Thread
        {
            PersistentThreadId = "thread_old",
            ProviderType = "OldProvider"
        };

        var updated = original with
        {
            PersistentThreadId = "thread_new",
            ProviderType = "NewProvider"
        };

        updated.PersistentThreadId.Should().Be("thread_new");
        updated.ProviderType.Should().Be("NewProvider");
    }

    [Fact]
    public void Thread_JsonRoundtrip_PreservesPersistentThreadId()
    {
        var thread = new Thread
        {
            PersistentThreadId = "thread_abc123",
            ProviderType = "AzureFoundryPersistent",
            ParentPath = "/test"
        };

        var json = JsonSerializer.Serialize(thread);
        var deserialized = JsonSerializer.Deserialize<Thread>(json);

        deserialized.Should().NotBeNull();
        deserialized!.PersistentThreadId.Should().Be("thread_abc123");
        deserialized.ProviderType.Should().Be("AzureFoundryPersistent");
        deserialized.ParentPath.Should().Be("/test");
    }

    [Fact]
    public void Thread_JsonRoundtrip_NullPersistentFields_DeserializesAsNull()
    {
        // Simulate old JSON without persistent fields (backward compatibility)
        var json = """{"ParentPath":"/test"}""";

        var deserialized = JsonSerializer.Deserialize<Thread>(json);

        deserialized.Should().NotBeNull();
        deserialized!.PersistentThreadId.Should().BeNull();
        deserialized.ProviderType.Should().BeNull();
        deserialized.ParentPath.Should().Be("/test");
    }

    [Fact]
    public void Thread_RecordEquality_IncludesPersistentFields()
    {
        var thread1 = new Thread
        {
            PersistentThreadId = "thread_abc",
            ProviderType = "Provider1"
        };

        var thread2 = new Thread
        {
            PersistentThreadId = "thread_abc",
            ProviderType = "Provider1"
        };

        var thread3 = new Thread
        {
            PersistentThreadId = "thread_xyz",
            ProviderType = "Provider1"
        };

        thread1.Should().Be(thread2);
        thread1.Should().NotBe(thread3);
    }

    #endregion


    #region IChatClientFactory.IsPersistent

    private class NonPersistentTestFactory : IChatClientFactory
    {
        public string Name => "NonPersistent";
        public IReadOnlyList<string> Models => [];
        public int Order => 0;
        // IsPersistent uses default interface member (false)

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => throw new NotImplementedException();
    }

    private class PersistentTestFactory : IChatClientFactory
    {
        public string Name => "Persistent";
        public IReadOnlyList<string> Models => [];
        public int Order => 0;
        public bool IsPersistent => true;

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => throw new NotImplementedException();
    }

    [Fact]
    public void IChatClientFactory_IsPersistent_DefaultIsFalse()
    {
        IChatClientFactory factory = new NonPersistentTestFactory();

        factory.IsPersistent.Should().BeFalse();
    }

    [Fact]
    public void IChatClientFactory_IsPersistent_CanBeOverriddenToTrue()
    {
        IChatClientFactory factory = new PersistentTestFactory();

        factory.IsPersistent.Should().BeTrue();
    }

    #endregion

    #region IAgentChat.SetPersistentThreadId

    private class MinimalAgentChat : IAgentChat
    {
        public AgentContext? Context => null;
        public void SetContext(AgentContext? applicationContext) { }
        public void SetSelectedAgent(string? agentName) { }
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync() => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>([]);
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public void SetThreadId(string threadId) { }
        // SetPersistentThreadId uses default interface member (no-op)
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
    }

    [Fact]
    public void IAgentChat_SetPersistentThreadId_DefaultIsNoOp()
    {
        IAgentChat chat = new MinimalAgentChat();

        var action = () => chat.SetPersistentThreadId("some-id");

        action.Should().NotThrow();
    }

    [Fact]
    public void IAgentChat_SetPersistentThreadId_NullDoesNotThrow()
    {
        IAgentChat chat = new MinimalAgentChat();

        var action = () => chat.SetPersistentThreadId(null);

        action.Should().NotThrow();
    }

    #endregion
}
