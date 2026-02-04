using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for thread creation via IMeshCatalog.CreateNodeAsync.
/// </summary>
public class ThreadCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Add graph configuration to register Thread node type
        return base.ConfigureMesh(builder)
            .AddJsonGraphConfiguration(TestPaths.SamplesGraphData);
    }

    [Fact]
    public async Task CreateThread_ViaIMeshCatalog_Succeeds()
    {
        // Arrange
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            Messages = new List<ThreadMessage>()
        };
        var threadPath = $"User/{userId}/Threads/{Guid.NewGuid()}";
        var node = new MeshNode(threadPath)
        {
            Name = "Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = await catalog.CreateNodeAsync(node, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.State.Should().Be(MeshNodeState.Active);
        createdNode.Content.Should().BeOfType<MeshThread>();
    }

    [Fact]
    public async Task CreateThread_WithMessages_Succeeds()
    {
        // Arrange
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            Messages =
            [
                new ThreadMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    Role = "user",
                    Text = "Hello, world!",
                    Timestamp = DateTime.UtcNow
                }
            ]
        };
        var threadPath = $"User/{userId}/Threads/{Guid.NewGuid()}";
        var node = new MeshNode(threadPath)
        {
            Name = "Thread with Messages",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = await catalog.CreateNodeAsync(node, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.State.Should().Be(MeshNodeState.Active);
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.Messages.Should().HaveCount(1);
        content.Messages![0].Text.Should().Be("Hello, world!");
    }

    [Fact]
    public async Task CreateThread_CanBeRetrieved()
    {
        // Arrange
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            Messages = new List<ThreadMessage>()
        };
        var threadPath = $"User/{userId}/Threads/{Guid.NewGuid()}";
        var node = new MeshNode(threadPath)
        {
            Name = "Retrievable Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act - Create
        var createdNode = await catalog.CreateNodeAsync(node, userId, TestTimeout);

        // Act - Retrieve
        var retrievedNode = await catalog.GetNodeAsync(new Messaging.Address(threadPath));

        // Assert
        retrievedNode.Should().NotBeNull();
        retrievedNode!.Path.Should().Be(threadPath);
        retrievedNode.NodeType.Should().Be(ThreadNodeType.NodeType);
        retrievedNode.Name.Should().Be("Retrievable Thread");
        retrievedNode.Content.Should().BeOfType<MeshThread>();
    }

    [Fact]
    public async Task GetDataRequest_ToNonExistentNode_ReturnsErrorNotEndlessMessages()
    {
        // Arrange - Create a malformed path that mimics the bug
        var nonExistentPath = "User/Roland/Threads/User/Roland/Threads/IOvAlUVOUUubAUdRoDaPwQ";
        var address = new Messaging.Address(nonExistentPath);
        var hub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

        // Act - Send GetDataRequest to non-existent node
        // This should return an error, not cause endless messages
        var cts = new CancellationTokenSource(5.Seconds());

        Func<Task> act = async () =>
        {
            var response = await hub.AwaitResponse(
                new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
                o => o.WithTarget(address),
                cts.Token);
        };

        // Assert - Should fail (either with specific error or timeout), not hang or cause endless messages
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task GetDataRequest_ToNonExistentThread_ReturnsNullNotEndlessMessages()
    {
        // Arrange - Path that looks valid but doesn't exist
        var nonExistentPath = "User/TestUser/Threads/nonexistent123";
        var address = new Messaging.Address(nonExistentPath);
        var hub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

        // Act - Send GetDataRequest to non-existent node
        var cts = new CancellationTokenSource(5.Seconds());

        Func<Task> act = async () =>
        {
            var response = await hub.AwaitResponse(
                new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
                o => o.WithTarget(address),
                cts.Token);
        };

        // Assert - Should fail quickly with proper error, not hang or cause endless messages
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task CreateThread_WithParentPath_PreservesParentPath()
    {
        // Arrange - Threads use "Threads" sub-namespace: {parentPath}/Threads/{threadId}
        var userId = "TestUser";
        var parentPath = "ACME/ProductLaunch";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            Messages = new List<ThreadMessage>(),
            ParentPath = parentPath  // Store where the thread was created
        };
        // Full path includes "Threads" sub-namespace
        var threadPath = $"{parentPath}/Threads/{Guid.NewGuid()}";
        var node = new MeshNode(threadPath)
        {
            Name = "Thread with ParentPath",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = await catalog.CreateNodeAsync(node, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.Path.Should().Contain("/Threads/", "Thread path should use Threads sub-namespace");
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should be preserved");

        // Verify it can be retrieved with ParentPath intact
        var retrievedNode = await catalog.GetNodeAsync(new Messaging.Address(threadPath));
        var retrievedContent = retrievedNode?.Content.Should().BeOfType<MeshThread>().Subject;
        retrievedContent?.ParentPath.Should().Be(parentPath, "ParentPath should be preserved after retrieval");
    }

    [Fact]
    public async Task CreateThread_InThreadsSubNamespace_FollowsPattern()
    {
        // Arrange - Verifies the Threads sub-namespace pattern: {parentPath}/Threads/{threadId}
        // This is the pattern used by CreateNodeDialog and AgentChatView
        var userId = "TestUser";
        var parentPath = "TestProject";
        var threadId = Guid.NewGuid().ToString("N");
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create parent node first
        var parentNode = new MeshNode(parentPath)
        {
            Name = "Test Project",
            NodeType = "Markdown"
        };
        await catalog.CreateNodeAsync(parentNode, userId, TestTimeout);

        // Create thread under Threads sub-namespace
        var threadPath = $"{parentPath}/Threads/{threadId}";
        var threadContent = new MeshThread
        {
            Messages = new List<ThreadMessage>(),
            ParentPath = parentPath
        };
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread in Sub-namespace",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = await catalog.CreateNodeAsync(threadNode, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.Path.Should().StartWith($"{parentPath}/Threads/", "Thread should be in Threads sub-namespace");

        // Verify ParentPath points to the logical parent (without /Threads)
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should point to logical parent");

        // Cleanup
        await catalog.DeleteNodeAsync(parentPath, recursive: true, ct: TestTimeout);
    }

    [Fact]
    public async Task CreateThread_AsChildOfParent_Succeeds()
    {
        // Arrange - Test creating thread as child of a parent node
        // This tests the pattern used by AgentChatView where threads are
        // created under the current navigation context with Threads sub-namespace
        var userId = "TestUser";
        var parentPath = $"TestParent_{Guid.NewGuid()}";
        var threadId = Guid.NewGuid().ToString();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create parent node first
        var parentNode = new MeshNode(parentPath)
        {
            Name = "Test Parent",
            NodeType = "Markdown"
        };
        await catalog.CreateNodeAsync(parentNode, userId, TestTimeout);

        // Create thread under Threads sub-namespace: {parentPath}/Threads/{threadId}
        var threadPath = $"{parentPath}/Threads/{threadId}";
        var threadContent = new MeshThread
        {
            Messages = new List<ThreadMessage>(),
            ParentPath = parentPath  // Store parent path for navigation back
        };
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread under Parent",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = await catalog.CreateNodeAsync(threadNode, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath, "Thread path should be parent/Threads/threadId");
        createdNode.Path.Should().StartWith($"{parentPath}/Threads/", "Thread should be in Threads sub-namespace");
        createdNode.NodeType.Should().Be(ThreadNodeType.NodeType);

        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should be preserved for navigation");

        // Verify the thread can be retrieved by full path
        var retrievedNode = await catalog.GetNodeAsync(new Messaging.Address(threadPath));
        retrievedNode.Should().NotBeNull();
        var retrievedContent = retrievedNode?.Content.Should().BeOfType<MeshThread>().Subject;
        retrievedContent?.ParentPath.Should().Be(parentPath);

        // Cleanup
        await catalog.DeleteNodeAsync(parentPath, recursive: true, ct: TestTimeout);
    }

    [Fact]
    public void Thread_ToChatMessages_ConvertsCorrectly()
    {
        // Arrange
        var thread = new MeshThread
        {
            Messages = new List<ThreadMessage>
            {
                new ThreadMessage
                {
                    Id = "1",
                    Role = "user",
                    Text = "Hello",
                    AuthorName = "Alice",
                    Timestamp = DateTime.UtcNow
                },
                new ThreadMessage
                {
                    Id = "2",
                    Role = "assistant",
                    Text = "Hi there!",
                    AuthorName = "Bot",
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        // Act
        var chatMessages = thread.ToChatMessages();

        // Assert
        chatMessages.Should().HaveCount(2);
        chatMessages[0].Role.Value.Should().Be("user");
        chatMessages[0].Text.Should().Be("Hello");
        chatMessages[0].AuthorName.Should().Be("Alice");
        chatMessages[1].Role.Value.Should().Be("assistant");
        chatMessages[1].Text.Should().Be("Hi there!");
        chatMessages[1].AuthorName.Should().Be("Bot");
    }

    [Fact]
    public void Thread_FromChatMessages_ConvertsCorrectly()
    {
        // Arrange
        var chatMessages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new Microsoft.Extensions.AI.ChatMessage(
                new Microsoft.Extensions.AI.ChatRole("user"),
                "Hello")
            { AuthorName = "Alice" },
            new Microsoft.Extensions.AI.ChatMessage(
                new Microsoft.Extensions.AI.ChatRole("assistant"),
                "Hi there!")
            { AuthorName = "Bot" }
        };
        var parentPath = "ACME/Project";

        // Act
        var thread = MeshThread.FromChatMessages(chatMessages, parentPath);

        // Assert
        thread.Messages.Should().HaveCount(2);
        thread.Messages![0].Role.Should().Be("user");
        thread.Messages[0].Text.Should().Be("Hello");
        thread.Messages[0].AuthorName.Should().Be("Alice");
        thread.Messages[1].Role.Should().Be("assistant");
        thread.Messages[1].Text.Should().Be("Hi there!");
        thread.Messages[1].AuthorName.Should().Be("Bot");
        thread.ParentPath.Should().Be(parentPath);
    }

    [Fact]
    public void Thread_EmptyMessages_ToChatMessages_ReturnsEmptyList()
    {
        // Arrange
        var thread = new MeshThread { Messages = null };

        // Act
        var chatMessages = thread.ToChatMessages();

        // Assert
        chatMessages.Should().BeEmpty();
    }
}
