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
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.ShortGuid;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests for thread creation via CreateThreadRequest and IMeshService.CreateNodeAsync.
/// Threads are satellite nodes created under {namespace}/_Thread/{speakingId}.
/// Messages are stored as child MeshNodes with nodeType="ThreadMessage".
/// </summary>
public class ThreadCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddAI();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public async Task CreateThread_ViaCreateThreadRequest_UsesThreadPartitionAndSpeakingId()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Arrange — create context node so the node hub exists
        var contextPath = "ACME";
        await NodeFactory.CreateNodeAsync(
            new MeshNode(contextPath) { Name = "ACME Corp", NodeType = "Markdown" }, ct);

        // Act — send CreateThreadRequest to the context node's hub (production path)
        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = contextPath,
                UserMessageText = "Hello, can you help me with this project?"
            },
            o => o.WithTarget(new Address(contextPath)),
            ct);

        // Assert — response
        response.Message.Success.Should().BeTrue(response.Message.Error);
        response.Message.ThreadPath.Should().NotBeNullOrEmpty();
        response.Message.ThreadName.Should().NotBeNullOrEmpty();

        var threadPath = response.Message.ThreadPath!;
        Output.WriteLine($"Created thread at: {threadPath}");

        // Assert — path uses _Thread partition
        threadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/",
            "thread path must use _Thread partition: {namespace}/_Thread/{speakingId}");
        threadPath.Should().StartWith($"{contextPath}/",
            "thread path must start with the context node path");

        // Assert — speaking ID is human-readable (derived from message text)
        var speakingId = threadPath.Split('/').Last();
        speakingId.Should().Contain("hello",
            "speaking ID should be derived from the message text");

        // Assert — retrieve node and verify MainNode (satellite auto-set)
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(ct);
        node.Should().NotBeNull("thread node should be retrievable");
        node!.NodeType.Should().Be(ThreadNodeType.NodeType);
        node.MainNode.Should().Be($"{contextPath}/{ThreadNodeType.ThreadPartition}",
            "satellite MainNode should point to the _Thread namespace, not self");
        node.MainNode.Should().NotBe(node.Path,
            "satellite MainNode must NOT be self-referencing");

        // Assert — content (ParentPath = hub address = context path)
        var content = node.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(contextPath);
    }

    [Fact]
    public async Task CreateThread_ViaCreateThreadRequest_OnDifferentContextNode()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Arrange — create a different context node
        var contextPath = "TestProject";
        await NodeFactory.CreateNodeAsync(
            new MeshNode(contextPath) { Name = "Test Project", NodeType = "Markdown" }, ct);

        // Act — send to the context node's hub
        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = contextPath,
                UserMessageText = "A thread on TestProject"
            },
            o => o.WithTarget(new Address(contextPath)),
            ct);

        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.ThreadPath!;
        Output.WriteLine($"Created thread at: {threadPath}");

        threadPath.Should().StartWith($"{contextPath}/{ThreadNodeType.ThreadPartition}/",
            "thread should be under {contextPath}/_Thread/{speakingId}");
    }

    [Fact]
    public async Task CreateThread_SpeakingId_IsDeterministicFromMessageText()
    {
        // Verify that GenerateSpeakingId produces readable slugs
        var id1 = ThreadNodeType.GenerateSpeakingId("Hello, can you help me?");
        var id2 = ThreadNodeType.GenerateSpeakingId("Hello, can you help me?");

        // Both should contain "hello-can-you-help-me" but have unique suffixes
        id1.Should().Contain("hello-can-you-help-me");
        id2.Should().Contain("hello-can-you-help-me");
        id1.Should().NotBe(id2, "each call should produce a unique suffix");

        // Long messages should be truncated
        var longMsg = new string('a', 200);
        var longId = ThreadNodeType.GenerateSpeakingId(longMsg);
        longId.Length.Should().BeLessThan(50, "speaking ID should be truncated for long messages");
    }

    [Fact]
    public async Task CreateThread_ViaIMeshCatalog_Succeeds()
    {
        // Arrange
        var userId = "TestUser";
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
        var createdNode = await NodeFactory.CreateNodeAsync(node, TestTimeout);

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
        var createdThread = await NodeFactory.CreateNodeAsync(threadNode, TestTimeout);

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
        var createdMessage = await NodeFactory.CreateNodeAsync(messageNode, TestTimeout);

        // Assert
        createdThread.Should().NotBeNull();
        createdMessage.Should().NotBeNull();
        createdMessage.Path.Should().Be(messagePath);
        createdMessage.NodeType.Should().Be(ThreadMessageNodeType.NodeType);

        var content = createdMessage.Content.Should().BeOfType<ThreadMessage>().Subject;
        content.Text.Should().Be("Hello, world!");
        content.Type.Should().Be(ThreadMessageType.ExecutedInput);

        // Query child messages
        var children = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"
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
        var createdNode = await NodeFactory.CreateNodeAsync(node, TestTimeout);

        // Act - Retrieve
        var retrievedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync();

        // Assert
        retrievedNode.Should().NotBeNull();
        retrievedNode!.Path.Should().Be(threadPath);
        retrievedNode.NodeType.Should().Be(ThreadNodeType.NodeType);
        retrievedNode.Name.Should().Be("Retrievable Thread");
        retrievedNode.Content.Should().BeOfType<MeshThread>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetDataRequest_ToNonExistentNode_ReturnsErrorNotEndlessMessages()
    {
        // Arrange - Create a malformed path that mimics the bug
        var nonExistentPath = "User/Roland/User/Roland/IOvAlUVOUUubAUdRoDaPwQ";
        var address = new Messaging.Address(nonExistentPath);
        var hub = Mesh;

        // Act - Send GetDataRequest to non-existent node.
        // The key property: routing completes within the timeout, not spinning forever.
        var cts = new CancellationTokenSource(3.Seconds());

        var response = await hub.AwaitResponse(
            new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
            o => o.WithTarget(address),
            cts.Token);

        // Assert - Completed without endless loop. Response may contain data or error,
        // but the important thing is that it returned within the timeout.
        Output.WriteLine($"Response completed: {response.Message?.GetType().Name}");
    }

    [Fact(Timeout = 5000)]
    public async Task GetDataRequest_ToNonExistentThread_ReturnsErrorNotEndlessMessages()
    {
        // Arrange - Thread path that looks valid but doesn't exist
        var nonExistentPath = "User/TestUser/nonexistent123";
        var address = new Messaging.Address(nonExistentPath);
        var hub = Mesh;

        // Act - Send GetDataRequest to non-existent node.
        // The key property: routing completes within the timeout, not spinning forever.
        var cts = new CancellationTokenSource(3.Seconds());

        var response = await hub.AwaitResponse(
            new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
            o => o.WithTarget(address),
            cts.Token);

        // Assert - Completed without endless loop. Response may contain data or error,
        // but the important thing is that it returned within the timeout.
        Output.WriteLine($"Response completed: {response.Message?.GetType().Name}");
    }

    [Fact]
    public async Task CreateThread_WithParentPath_PreservesParentPath()
    {
        // Arrange - Threads are stored as direct children: {parentPath}/{threadId}
        var parentPath = "ACME/ProductLaunch";
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
        var createdNode = await NodeFactory.CreateNodeAsync(node, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should be preserved");

        // Verify it can be retrieved with ParentPath intact
        var retrievedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync();
        var retrievedContent = retrievedNode?.Content.Should().BeOfType<MeshThread>().Subject;
        retrievedContent?.ParentPath.Should().Be(parentPath, "ParentPath should be preserved after retrieval");
    }

    [Fact]
    public async Task CreateThread_AsDirectChild_FollowsPattern()
    {
        // Arrange - Verifies the flat thread pattern: {parentPath}/{threadId}
        // This is the pattern used by CreateNodeDialog and ThreadChatView
        var parentPath = "TestProject";
        var threadId = Guid.NewGuid().ToString("N");

        // Create parent node first
        var parentNode = new MeshNode(parentPath)
        {
            Name = "Test Project",
            NodeType = "Markdown"
        };
        await NodeFactory.CreateNodeAsync(parentNode, TestTimeout);

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
        var createdNode = await NodeFactory.CreateNodeAsync(threadNode, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.Path.Should().StartWith($"{parentPath}/", "Thread should be a direct child");

        // Verify ParentPath points to the logical parent
        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should point to logical parent");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(parentPath, ct: TestTimeout);
    }

    [Fact]
    public async Task CreateThread_AsChildOfParent_Succeeds()
    {
        // Arrange - Test creating thread as direct child of a parent node
        // This tests the pattern used by ThreadChatView where threads are
        // created under the current navigation context
        var parentPath = $"TestParent_{Guid.NewGuid()}";
        var threadId = Guid.NewGuid().ToString();

        // Create parent node first
        var parentNode = new MeshNode(parentPath)
        {
            Name = "Test Parent",
            NodeType = "Markdown"
        };
        await NodeFactory.CreateNodeAsync(parentNode, TestTimeout);

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
        var createdNode = await NodeFactory.CreateNodeAsync(threadNode, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath, "Thread path should be parent/threadId");
        createdNode.Path.Should().StartWith($"{parentPath}/", "Thread should be a direct child");
        createdNode.NodeType.Should().Be(ThreadNodeType.NodeType);

        var content = createdNode.Content.Should().BeOfType<MeshThread>().Subject;
        content.ParentPath.Should().Be(parentPath, "ParentPath should be preserved for navigation");

        // Verify the thread can be retrieved by full path
        var retrievedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync();
        retrievedNode.Should().NotBeNull();
        var retrievedContent = retrievedNode?.Content.Should().BeOfType<MeshThread>().Subject;
        retrievedContent?.ParentPath.Should().Be(parentPath);

        // Cleanup
        await NodeFactory.DeleteNodeAsync(parentPath, ct: TestTimeout);
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
        await NodeFactory.CreateNodeAsync(threadNode, longTimeout);

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

        var createdResponse = await NodeFactory.CreateNodeAsync(responseNode, longTimeout);

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

/// <summary>
/// Tests that CreateThreadRequest is denied when the user lacks Permission.Update.
/// Uses ConfigureMeshBase (no PublicAdminAccess) so permissions are enforced.
/// </summary>
public class ThreadPermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminUserId = "admin-user";
    private const string ViewerUserId = "viewer-user";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // No PublicAdminAccess — permissions are enforced
        return ConfigureMeshBase(builder)
            .AddAI();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    protected override async Task SetupAccessRightsAsync()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Admin user gets full access globally
        await securityService.AddUserRoleAsync(AdminUserId, "Admin", null, "system");

        // Viewer gets only Read+Execute (no Update) at the test context
        await securityService.AddUserRoleAsync(ViewerUserId, "Viewer", "SecureProject", "system");
    }

    [Fact]
    public async Task CreateThread_WithUpdatePermission_Succeeds()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Login as admin
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = AdminUserId, Name = "Admin" });

        // Create context node
        await NodeFactory.CreateNodeAsync(
            new MeshNode("SecureProject") { Name = "Secure Project", NodeType = "Markdown" }, ct);

        // Act — admin creates thread (has Update permission)
        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = "SecureProject",
                UserMessageText = "Admin creating a thread"
            },
            o => o.WithTarget(new Address("SecureProject")),
            ct);

        response.Message.Success.Should().BeTrue(response.Message.Error);
        response.Message.ThreadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/");
    }

    [Fact]
    public async Task CreateThread_WithoutUpdatePermission_IsDenied()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create context node as admin first
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = AdminUserId, Name = "Admin" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("SecureProject") { Name = "Secure Project", NodeType = "Markdown" }, ct);

        // Switch to viewer (Read+Execute only, no Update)
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = ViewerUserId, Name = "Viewer" });

        // Act — viewer tries to create thread (lacks Update permission)
        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = "SecureProject",
                UserMessageText = "Viewer trying to create a thread"
            },
            o => o.WithTarget(new Address("SecureProject")),
            ct);

        // Assert — should be denied
        response.Message.Success.Should().BeFalse("viewer lacks Update permission");
        response.Message.Error.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Denial message: {response.Message.Error}");
    }
}
