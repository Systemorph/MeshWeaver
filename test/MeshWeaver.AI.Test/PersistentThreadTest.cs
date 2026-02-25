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

    #region InMemoryChatPersistenceService Thread Storage

    private static InMemoryChatPersistenceService CreatePersistenceService(AccessService? accessService = null)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        accessService ??= new AccessService();
        return new InMemoryChatPersistenceService(serviceProvider, accessService);
    }

    [Fact]
    public async Task SaveAndLoadThread_Roundtrip_ReturnsOriginalData()
    {
        var service = CreatePersistenceService();
        var threadData = JsonSerializer.SerializeToElement(new { PersistentThreadId = "t1", ProviderType = "TestProvider" });

        await service.SaveThreadAsync("thread-1", "agent-a", threadData);
        var loaded = await service.LoadThreadAsync("thread-1", "agent-a");

        loaded.Should().NotBeNull();
        loaded!.Value.GetProperty("PersistentThreadId").GetString().Should().Be("t1");
        loaded.Value.GetProperty("ProviderType").GetString().Should().Be("TestProvider");
    }

    [Fact]
    public async Task LoadThread_NonExistent_ReturnsNull()
    {
        var service = CreatePersistenceService();

        var loaded = await service.LoadThreadAsync("nonexistent", "agent-a");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveThread_OverwritesExisting()
    {
        var service = CreatePersistenceService();
        var first = JsonSerializer.SerializeToElement(new { Value = "first" });
        var second = JsonSerializer.SerializeToElement(new { Value = "second" });

        await service.SaveThreadAsync("thread-1", "agent-a", first);
        await service.SaveThreadAsync("thread-1", "agent-a", second);
        var loaded = await service.LoadThreadAsync("thread-1", "agent-a");

        loaded.Should().NotBeNull();
        loaded!.Value.GetProperty("Value").GetString().Should().Be("second");
    }

    [Fact]
    public async Task SaveThread_DifferentAgentNames_IndependentStorage()
    {
        var service = CreatePersistenceService();
        var dataA = JsonSerializer.SerializeToElement(new { Agent = "A" });
        var dataB = JsonSerializer.SerializeToElement(new { Agent = "B" });

        await service.SaveThreadAsync("thread-1", "agent-a", dataA);
        await service.SaveThreadAsync("thread-1", "agent-b", dataB);

        var loadedA = await service.LoadThreadAsync("thread-1", "agent-a");
        var loadedB = await service.LoadThreadAsync("thread-1", "agent-b");

        loadedA!.Value.GetProperty("Agent").GetString().Should().Be("A");
        loadedB!.Value.GetProperty("Agent").GetString().Should().Be("B");
    }

    [Fact]
    public async Task SaveThread_DifferentUsers_Isolation()
    {
        var accessService = new AccessService();
        var service = CreatePersistenceService(accessService);

        // Save as user1
        accessService.SetContext(new AccessContext { ObjectId = "user1" });
        var user1Data = JsonSerializer.SerializeToElement(new { Owner = "user1" });
        await service.SaveThreadAsync("thread-1", "agent-a", user1Data);

        // Save as user2
        accessService.SetContext(new AccessContext { ObjectId = "user2" });
        var user2Data = JsonSerializer.SerializeToElement(new { Owner = "user2" });
        await service.SaveThreadAsync("thread-1", "agent-a", user2Data);

        // Load as user1 — should get user1's data
        accessService.SetContext(new AccessContext { ObjectId = "user1" });
        var loaded1 = await service.LoadThreadAsync("thread-1", "agent-a");
        loaded1!.Value.GetProperty("Owner").GetString().Should().Be("user1");

        // Load as user2 — should get user2's data
        accessService.SetContext(new AccessContext { ObjectId = "user2" });
        var loaded2 = await service.LoadThreadAsync("thread-1", "agent-a");
        loaded2!.Value.GetProperty("Owner").GetString().Should().Be("user2");
    }

    [Fact]
    public async Task LoadThread_AnonymousUser_UsesAnonymousKey()
    {
        var service = CreatePersistenceService();
        var data = JsonSerializer.SerializeToElement(new { Test = "anonymous" });

        // No context set → anonymous user
        await service.SaveThreadAsync("thread-1", "agent-a", data);
        var loaded = await service.LoadThreadAsync("thread-1", "agent-a");

        loaded.Should().NotBeNull();
        loaded!.Value.GetProperty("Test").GetString().Should().Be("anonymous");
    }

    #endregion

    #region IChatClientFactory.IsPersistent

    private class NonPersistentTestFactory : IChatClientFactory
    {
        public string Name => "NonPersistent";
        public IReadOnlyList<string> Models => [];
        public int DisplayOrder => 0;
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
        public int DisplayOrder => 0;
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
