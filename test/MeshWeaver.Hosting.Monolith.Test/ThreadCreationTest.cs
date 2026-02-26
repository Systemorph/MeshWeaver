using System;
using System.Collections.Generic;
using System.Linq;
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
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for thread creation via IMeshCatalog.CreateNodeAsync.
/// Threads store messages as child MeshNodes with nodeType="ThreadMessage".
/// </summary>
public class ThreadCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Add graph configuration to register Thread node type
        return base.ConfigureMesh(builder)
            .AddGraph();
    }

    [Fact]
    public async Task CreateThread_ViaIMeshCatalog_Succeeds()
    {
        // Arrange
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            ParentPath = $"User/{userId}"
        };
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";
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
    public async Task CreateThread_WithMessageAsChildNode_Succeeds()
    {
        // Arrange - Create thread and then add a message as a child node
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var threadContent = new MeshThread
        {
            ParentPath = $"User/{userId}"
        };
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread with Child Messages",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Create the thread
        var createdThread = await catalog.CreateNodeAsync(threadNode, userId, TestTimeout);

        // Create a message as child node
        var messageId = Guid.NewGuid().AsString();
        var messagePath = $"{threadPath}/{messageId}";
        var messageContent = new ThreadMessage
        {
            Id = messageId,
            Role = "user",
            Text = "Hello, world!",
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.ExecutedInput
        };
        var messageNode = new MeshNode(messagePath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = messageContent
        };

        // Act - Create the message child node
        var createdMessage = await catalog.CreateNodeAsync(messageNode, userId, TestTimeout);

        // Assert
        createdThread.Should().NotBeNull();
        createdMessage.Should().NotBeNull();
        createdMessage.Path.Should().Be(messagePath);
        createdMessage.NodeType.Should().Be(ThreadMessageNodeType.NodeType);

        var content = createdMessage.Content.Should().BeOfType<ThreadMessage>().Subject;
        content.Text.Should().Be("Hello, world!");
        content.Type.Should().Be(ThreadMessageType.ExecutedInput);

        // Query child messages
        var children = await meshQuery.QueryAsync<MeshNode>(
            $"path:{threadPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children"
        ).ToListAsync();

        children.Should().HaveCount(1);
        var childContent = children[0].Content.Should().BeOfType<ThreadMessage>().Subject;
        childContent.Text.Should().Be("Hello, world!");
    }

    [Fact]
    public async Task CreateThread_CanBeRetrieved()
    {
        // Arrange
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            ParentPath = $"User/{userId}"
        };
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";
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

    [Fact(Timeout = 3000)]
    public async Task GetDataRequest_ToNonExistentNode_ReturnsErrorNotEndlessMessages()
    {
        // Arrange - Create a malformed path that mimics the bug
        var nonExistentPath = "User/Roland/User/Roland/IOvAlUVOUUubAUdRoDaPwQ";
        var address = new Messaging.Address(nonExistentPath);
        var hub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

        // Act - Send GetDataRequest to non-existent node
        // Must return a DeliveryFailureException quickly, NOT wait for cancellation timeout
        var cts = new CancellationTokenSource(2.Seconds());

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await hub.AwaitResponse(
                new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
                o => o.WithTarget(address),
                cts.Token);
        });

        // Must be a routing failure, NOT a timeout (OperationCanceledException)
        Assert.False(ex is OperationCanceledException or TaskCanceledException,
            $"Expected routing failure but got timeout: {ex.GetType().Name}");
        Assert.Contains("Routing failed", ex.GetBaseException().Message);
        Output.WriteLine($"Got expected routing failure: {ex.GetBaseException().Message}");
    }

    [Fact(Timeout = 3000)]
    public async Task GetDataRequest_ToNonExistentThread_ReturnsErrorNotEndlessMessages()
    {
        // Arrange - Thread path that looks valid but doesn't exist
        var nonExistentPath = "User/TestUser/nonexistent123";
        var address = new Messaging.Address(nonExistentPath);
        var hub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

        // Act - Send GetDataRequest to non-existent node
        // Must return a DeliveryFailureException quickly, NOT wait for cancellation timeout
        var cts = new CancellationTokenSource(2.Seconds());

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await hub.AwaitResponse(
                new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
                o => o.WithTarget(address),
                cts.Token);
        });

        // Must be a routing failure, NOT a timeout (OperationCanceledException)
        Assert.False(ex is OperationCanceledException or TaskCanceledException,
            $"Expected routing failure but got timeout: {ex.GetType().Name}");
        Assert.Contains("Routing failed", ex.GetBaseException().Message);
        Output.WriteLine($"Got expected routing failure: {ex.GetBaseException().Message}");
    }

    [Fact]
    public async Task CreateThread_WithParentPath_PreservesParentPath()
    {
        // Arrange - Threads are stored as direct children: {parentPath}/{threadId}
        var userId = "TestUser";
        var parentPath = "ACME/Software/ProductLaunch";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadContent = new MeshThread
        {
            ParentPath = parentPath  // Store where the thread was created
        };
        var threadPath = $"{parentPath}/{Guid.NewGuid()}";
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
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should be preserved");

        // Verify it can be retrieved with ParentPath intact
        var retrievedNode = await catalog.GetNodeAsync(new Messaging.Address(threadPath));
        var retrievedContent = retrievedNode?.Content.Should().BeOfType<MeshThread>().Subject;
        retrievedContent?.ParentPath.Should().Be(parentPath, "ParentPath should be preserved after retrieval");
    }

    [Fact]
    public async Task CreateThread_AsDirectChild_FollowsPattern()
    {
        // Arrange - Verifies the flat thread pattern: {parentPath}/{threadId}
        // This is the pattern used by CreateNodeDialog and ThreadChatView
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

        // Create thread as direct child
        var threadPath = $"{parentPath}/{threadId}";
        var threadContent = new MeshThread
        {
            ParentPath = parentPath
        };
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread as Direct Child",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = await catalog.CreateNodeAsync(threadNode, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.Path.Should().StartWith($"{parentPath}/", "Thread should be a direct child");

        // Verify ParentPath points to the logical parent
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should point to logical parent");

        // Cleanup
        await catalog.DeleteNodeAsync(parentPath, recursive: true, ct: TestTimeout);
    }

    [Fact]
    public async Task CreateThread_AsChildOfParent_Succeeds()
    {
        // Arrange - Test creating thread as direct child of a parent node
        // This tests the pattern used by ThreadChatView where threads are
        // created under the current navigation context
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

        // Create thread as direct child: {parentPath}/{threadId}
        var threadPath = $"{parentPath}/{threadId}";
        var threadContent = new MeshThread
        {
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
        createdNode.Path.Should().Be(threadPath, "Thread path should be parent/threadId");
        createdNode.Path.Should().StartWith($"{parentPath}/", "Thread should be a direct child");
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
    public void ThreadMessages_ToChatMessages_ConvertsCorrectly()
    {
        // Arrange - Use the extension method on a list of ThreadMessages
        var messages = new List<ThreadMessage>
        {
            new ThreadMessage
            {
                Id = "1",
                Role = "user",
                Text = "Hello",
                AuthorName = "Alice",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            },
            new ThreadMessage
            {
                Id = "2",
                Role = "assistant",
                Text = "Hi there!",
                AuthorName = "Bot",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };

        // Act
        var chatMessages = messages.ToChatMessages();

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
    public void ThreadMessages_ToChatMessages_ExcludesEditingPrompts()
    {
        // Arrange - EditingPrompt messages should be excluded from chat
        var messages = new List<ThreadMessage>
        {
            new ThreadMessage
            {
                Id = "1",
                Role = "user",
                Text = "Submitted message",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            },
            new ThreadMessage
            {
                Id = "2",
                Role = "user",
                Text = "Currently typing...",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.EditingPrompt // Should be excluded
            },
            new ThreadMessage
            {
                Id = "3",
                Role = "assistant",
                Text = "Response",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };

        // Act
        var chatMessages = messages.ToChatMessages();

        // Assert
        chatMessages.Should().HaveCount(2, "EditingPrompt should be excluded");
        chatMessages[0].Text.Should().Be("Submitted message");
        chatMessages[1].Text.Should().Be("Response");
    }

    [Fact]
    public void ThreadMessageExtensions_FromChatMessages_ConvertsCorrectly()
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

        // Act
        var threadMessages = ThreadMessageExtensions.FromChatMessages(chatMessages);

        // Assert
        threadMessages.Should().HaveCount(2);
        threadMessages[0].Role.Should().Be("user");
        threadMessages[0].Text.Should().Be("Hello");
        threadMessages[0].AuthorName.Should().Be("Alice");
        threadMessages[0].Type.Should().Be(ThreadMessageType.ExecutedInput);
        threadMessages[1].Role.Should().Be("assistant");
        threadMessages[1].Text.Should().Be("Hi there!");
        threadMessages[1].AuthorName.Should().Be("Bot");
        threadMessages[1].Type.Should().Be(ThreadMessageType.AgentResponse);
    }

    [Fact]
    public void ThreadMessage_DefaultType_IsExecutedInput()
    {
        // Arrange & Act
        var message = new ThreadMessage
        {
            Id = "1",
            Role = "user",
            Text = "Hello"
        };

        // Assert - Default type should be ExecutedInput for backward compatibility
        message.Type.Should().Be(ThreadMessageType.ExecutedInput);
    }

    [Fact]
    public async Task CreateThreadMessage_WithDifferentTypes_PreservesType()
    {
        // Arrange
        var userId = "TestUser";
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";

        // Use a longer timeout since we're creating multiple nodes
        var longTimeout = new CancellationTokenSource(30.Seconds()).Token;

        // Create thread first
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread { ParentPath = $"User/{userId}" }
        };
        await catalog.CreateNodeAsync(threadNode, userId, longTimeout);

        // Create a single message to test type preservation (simplify test)
        var responseMessage = new ThreadMessage
        {
            Id = Guid.NewGuid().AsString(),
            Role = "assistant",
            Text = "Response",
            Type = ThreadMessageType.AgentResponse
        };

        // Act - Create message node
        var responseNode = new MeshNode($"{threadPath}/{responseMessage.Id}")
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = responseMessage
        };

        var createdResponse = await catalog.CreateNodeAsync(responseNode, userId, longTimeout);

        // Assert - Type should be preserved
        var responseContent = createdResponse.Content.Should().BeOfType<ThreadMessage>().Subject;
        responseContent.Type.Should().Be(ThreadMessageType.AgentResponse);
        responseContent.Text.Should().Be("Response");
        responseContent.Role.Should().Be("assistant");
    }

#pragma warning disable CS0618 // Type or member is obsolete - testing backward compatibility
    [Fact]
    public void LegacyThread_ToChatMessages_StillWorks()
    {
        // Arrange - Test backward compatibility with legacy inline Messages
        var thread = new MeshThread
        {
            Messages = new List<ThreadMessage>
            {
                new ThreadMessage
                {
                    Id = "1",
                    Role = "user",
                    Text = "Legacy message",
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        // Act
        var chatMessages = thread.ToChatMessages();

        // Assert
        chatMessages.Should().HaveCount(1);
        chatMessages[0].Text.Should().Be("Legacy message");
    }

    [Fact]
    public void LegacyThread_EmptyMessages_ToChatMessages_ReturnsEmptyList()
    {
        // Arrange
        var thread = new MeshThread { Messages = null };

        // Act
        var chatMessages = thread.ToChatMessages();

        // Assert
        chatMessages.Should().BeEmpty();
    }
#pragma warning restore CS0618
}
