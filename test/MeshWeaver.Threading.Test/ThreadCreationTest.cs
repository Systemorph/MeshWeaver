using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
/// Tests for thread creation via CreateNodeRequest + ThreadNodeType.BuildThreadNode and IMeshService.CreateNode.
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
    public void CreateThread_ViaCreateNodeRequest_UsesThreadPartitionAndSpeakingId()
    {

        // Arrange Ã¢â‚¬â€ create context node so the node hub exists
        var contextPath = "ACME";
        NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "ACME Corp", NodeType = "Markdown" }).Should().Emit();

        // Act Ã¢â‚¬â€ send CreateNodeRequest to the context node's hub (production path)
        var client = GetClient();
        var response = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Hello, can you help me with this project?")), o => o.WithTarget(new Address(contextPath))).Should().Within(15.Seconds()).Emit();

        // Assert Ã¢â‚¬â€ response
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        response.Message.Node?.Path.Should().NotBeNullOrEmpty();
        response.Message.Node?.Name.Should().NotBeNullOrEmpty();

        var threadPath = response.Message.Node?.Path!;
        Output.WriteLine($"Created thread at: {threadPath}");

        // Assert Ã¢â‚¬â€ path uses _Thread partition
        threadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/",
            "thread path must use _Thread partition: {namespace}/_Thread/{speakingId}");
        threadPath.Should().StartWith($"{contextPath}/",
            "thread path must start with the context node path");

        // Assert Ã¢â‚¬â€ speaking ID is human-readable (derived from message text)
        var speakingId = threadPath.Split('/').Last();
        speakingId.Should().Contain("hello",
            "speaking ID should be derived from the message text");

        // Assert Ã¢â‚¬â€ retrieve node and verify MainNode (satellite auto-set, stream read)
        var node = ReadNode(threadPath).Should().Emit();
        node.Should().NotBeNull("thread node should be retrievable");
        node!.NodeType.Should().Be(ThreadNodeType.NodeType);
        node.MainNode.Should().Be(contextPath,
            "satellite MainNode should point to the content entity, not the _Thread namespace");
        node.MainNode.Should().NotBe(node.Path,
            "satellite MainNode must NOT be self-referencing");

        // Assert Ã¢â‚¬â€ content is a MeshThread, and MainNode points to context
        node.Content.Should().BeOfType<MeshThread>();
        node.MainNode.Should().Be(contextPath);
    }

    [Fact]
    public void CreateThread_ViaCreateNodeRequest_OnDifferentContextNode()
    {

        // Arrange Ã¢â‚¬â€ create a different context node
        var contextPath = "TestProject";
        NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Test Project", NodeType = "Markdown" }).Should().Emit();

        // Act Ã¢â‚¬â€ send to the context node's hub
        var client = GetClient();
        var response = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "A thread on TestProject")), o => o.WithTarget(new Address(contextPath))).Should().Within(15.Seconds()).Emit();

        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        var threadPath = response.Message.Node?.Path!;
        Output.WriteLine($"Created thread at: {threadPath}");

        threadPath.Should().StartWith($"{contextPath}/{ThreadNodeType.ThreadPartition}/",
            "thread should be under {contextPath}/_Thread/{speakingId}");
    }

    [Fact]
    public void CreateThread_SpeakingId_IsDeterministicFromMessageText()
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
    public void CreateThread_ViaIMeshCatalog_Succeeds()
    {
        // Arrange
        var userId = "TestUser";
        var threadContent = new MeshThread();
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";
        var node = new MeshNode(threadPath)
        {
            Name = "Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act
        var createdNode = NodeFactory.CreateNode(node).Should().Emit();

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.State.Should().Be(MeshNodeState.Active);
        createdNode.Content.Should().BeOfType<MeshThread>();
    }

    [Fact]
    public void CreateThread_WithMessageAsChildNode_Succeeds()
    {
        // Arrange - Create thread and then add a message as a child node
        var userId = "TestUser";

        var threadContent = new MeshThread();
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread with Child Messages",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Create the thread
        var createdThread = NodeFactory.CreateNode(threadNode).Should().Emit();

        // Create a message as child node
        var messageId = Guid.NewGuid().AsString();
        var messagePath = $"{threadPath}/{messageId}";
        var messageContent = new ThreadMessage
        {
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
        var createdMessage = NodeFactory.CreateNode(messageNode).Should().Emit();

        // Assert
        createdThread.Should().NotBeNull();
        createdMessage.Should().NotBeNull();
        createdMessage.Path.Should().Be(messagePath);
        createdMessage.NodeType.Should().Be(ThreadMessageNodeType.NodeType);

        var content = createdMessage.Content.Should().BeOfType<ThreadMessage>().Subject;
        content.Text.Should().Be("Hello, world!");
        content.Type.Should().Be(ThreadMessageType.ExecutedInput);

        // Query child messages
        var children = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
            .Should().Match(c => c.Items.Count >= 1).Items;

        children.Should().HaveCount(1);
        var childContent = children[0].Content.Should().BeOfType<ThreadMessage>().Subject;
        childContent.Text.Should().Be("Hello, world!");
    }

    [Fact]
    public void CreateThread_CanBeRetrieved()
    {
        // Arrange
        var userId = "TestUser";
        var threadContent = new MeshThread();
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";
        var node = new MeshNode(threadPath)
        {
            Name = "Retrievable Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = threadContent
        };

        // Act - Create
        var createdNode = NodeFactory.CreateNode(node).Should().Emit();

        // Act - Retrieve via stream
        var retrievedNode = ReadNode(threadPath).Should().Emit();

        // Assert
        retrievedNode.Should().NotBeNull();
        retrievedNode!.Path.Should().Be(threadPath);
        retrievedNode.NodeType.Should().Be(ThreadNodeType.NodeType);
        retrievedNode.Name.Should().Be("Retrievable Thread");
        retrievedNode.Content.Should().BeOfType<MeshThread>();
    }

    [Fact(Timeout = 15000)]
    public void GetDataRequest_ToNonExistentNode_ReturnsErrorNotEndlessMessages()
    {
        // A malformed path that mimics the bug. Posted through the CLIENT hub
        // (same path the GUI takes) â€” the routing layer must resolve within the
        // timeout (response OR DeliveryFailure), not spin forever. Folding the
        // error into an emission via Catch lets the reactive assertion accept
        // both terminal outcomes while still failing if routing hangs.
        var nonExistentPath = "User/Roland/User/Roland/IOvAlUVOUUubAUdRoDaPwQ";
        var client = GetClient();

        var outcome = client.Observe(
                new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
                o => o.WithTarget(new Address(nonExistentPath)))
            .Select(r => (object?)r.Message?.GetType().Name)
            .Catch((Exception ex) => Observable.Return((object?)ex))
            .Should().Within(3.Seconds()).Emit();
        Output.WriteLine($"Routing resolved: {outcome}");
    }

    [Fact(Timeout = 15000)]
    public void GetDataRequest_ToNonExistentThread_ReturnsErrorNotEndlessMessages()
    {
        // Thread path that looks valid but doesn't exist. Posted through the CLIENT
        // â€” routing must resolve (response or DeliveryFailure) within the timeout.
        var nonExistentPath = "User/TestUser/nonexistent123";
        var client = GetClient();

        var outcome = client.Observe(
                new Data.GetDataRequest(new Data.EntityReference(nameof(MeshNode), nonExistentPath)),
                o => o.WithTarget(new Address(nonExistentPath)))
            .Select(r => (object?)r.Message?.GetType().Name)
            .Catch((Exception ex) => Observable.Return((object?)ex))
            .Should().Within(3.Seconds()).Emit();
        Output.WriteLine($"Routing resolved: {outcome}");
    }

    [Fact]
    public void CreateThread_AsDirectChild_FollowsPattern()
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
        NodeFactory.CreateNode(parentNode).Should().Emit();

        // Create thread as direct child
        var threadPath = $"{parentPath}/{threadId}";
        var threadContent = new MeshThread();
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread as Direct Child",
            NodeType = ThreadNodeType.NodeType,
            MainNode = parentPath,
            Content = threadContent
        };

        // Act
        var createdNode = NodeFactory.CreateNode(threadNode).Should().Emit();

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath);
        createdNode.Path.Should().StartWith($"{parentPath}/", "Thread should be a direct child");

        // Verify MainNode points to the logical parent
        createdNode.MainNode.Should().Be(parentPath, "MainNode should point to logical parent");

        // Cleanup
        NodeFactory.DeleteNode(parentPath).Should().Emit();
    }

    [Fact]
    public void CreateThread_AsChildOfParent_Succeeds()
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
        NodeFactory.CreateNode(parentNode).Should().Emit();

        // Create thread as direct child: {parentPath}/{threadId}
        var threadPath = $"{parentPath}/{threadId}";
        var threadContent = new MeshThread();
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Thread under Parent",
            NodeType = ThreadNodeType.NodeType,
            MainNode = parentPath,
            Content = threadContent
        };

        // Act
        var createdNode = NodeFactory.CreateNode(threadNode).Should().Emit();

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(threadPath, "Thread path should be parent/threadId");
        createdNode.Path.Should().StartWith($"{parentPath}/", "Thread should be a direct child");
        createdNode.NodeType.Should().Be(ThreadNodeType.NodeType);

        // Verify MainNode points to the logical parent
        createdNode.MainNode.Should().Be(parentPath, "MainNode should point to logical parent for navigation");

        // Verify the thread can be retrieved by full path (stream read)
        var retrievedNode = ReadNode(threadPath).Should().Emit();
        retrievedNode.Should().NotBeNull();
        retrievedNode!.MainNode.Should().Be(parentPath);

        // Cleanup
        NodeFactory.DeleteNode(parentPath).Should().Emit();
    }

    [Fact]
    public void ThreadMessages_ToChatMessages_ConvertsCorrectly()
    {
        // Arrange - Use the extension method on a list of ThreadMessages
        var messages = new List<ThreadMessage>
        {
            new ThreadMessage
            {
                Role = "user",
                Text = "Hello",
                AuthorName = "Alice",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            },
            new ThreadMessage
            {
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
                Role = "user",
                Text = "Submitted message",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            },
            new ThreadMessage
            {
                Role = "user",
                Text = "Currently typing...",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.EditingPrompt // Should be excluded
            },
            new ThreadMessage
            {
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
    public void ThreadMessage_DefaultType_IsExecutedInput()
    {
        // Arrange & Act
        var message = new ThreadMessage
        {
            Role = "user",
            Text = "Hello"
        };

        // Assert - Default type should be ExecutedInput for backward compatibility
        message.Type.Should().Be(ThreadMessageType.ExecutedInput);
    }

    [Fact]
    public void CreateThreadMessage_WithDifferentTypes_PreservesType()
    {
        // Arrange
        var userId = "TestUser";
        var threadPath = $"User/{userId}/{Guid.NewGuid()}";

        // Create thread first
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread()
        };
        NodeFactory.CreateNode(threadNode).Should().Emit();

        // Create a single message to test type preservation (simplify test)
        var responseMessageId = Guid.NewGuid().AsString();
        var responseMessage = new ThreadMessage
        {
            Role = "assistant",
            Text = "Response",
            Type = ThreadMessageType.AgentResponse
        };

        // Act - Create message node
        var responseNode = new MeshNode($"{threadPath}/{responseMessageId}")
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = responseMessage
        };

        var createdResponse = NodeFactory.CreateNode(responseNode).Should().Emit();

        // Assert - Type should be preserved
        var responseContent = createdResponse.Content.Should().BeOfType<ThreadMessage>().Subject;
        responseContent.Type.Should().Be(ThreadMessageType.AgentResponse);
        responseContent.Text.Should().Be("Response");
        responseContent.Role.Should().Be("assistant");
    }

#pragma warning disable CS0618 // Type or member is obsolete - testing backward compatibility
}

/// <summary>
/// Tests that CreateNodeRequest for threads is denied when the user lacks Permission.Update.
/// Uses ConfigureMeshBase (no PublicAdminAccess) so permissions are enforced.
/// </summary>
public class ThreadPermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminUserId = "admin-user";
    private const string ViewerUserId = "viewer-user";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // No PublicAdminAccess Ã¢â‚¬â€ permissions are enforced
        return ConfigureMeshBase(builder)
            .AddAI();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    // Framework setup hook: must keep the base's `Task` signature (the base
    // awaits it during fixture init). Not a test body â€” no deadlock risk â€” so
    // blocking .Should().Emit() on the CreateNode streams is the right shape here.
    protected override Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Admin user gets full access globally
        meshService.CreateNode(AssignmentNodeFactory.UserRole(AdminUserId, "Admin", null)).Should().Emit();

        // Viewer gets only Read+Execute (no Update) at the test context
        meshService.CreateNode(AssignmentNodeFactory.UserRole(ViewerUserId, "Viewer", "SecureProject")).Should().Emit();

        return Task.CompletedTask;
    }

    [Fact]
    public void CreateThread_WithUpdatePermission_Succeeds()
    {
        // Login as admin
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = AdminUserId, Name = "Admin" });

        // SetupAccessRightsAsync created the AccessAssignment for AdminUserId, but
        // SecurityService loads assignments via SyncedQuery which is eventually
        // consistent. On cold-start CI, the first HasPermissionAsync call can
        // return false before the synced query catches up. Stream-poll until
        // admin has Create permission â€” replaces a hand-rolled while +
        // Task.Delay(100) loop.
        Mesh.GetEffectivePermissions("SecureProject", AdminUserId)
            .Should().Within(10.Seconds()).Match(p => p.HasFlag(Permission.Create));

        // Create context node
        NodeFactory.CreateNode(
            new MeshNode("SecureProject") { Name = "Secure Project", NodeType = "Markdown" }).Should().Emit();

        // Act Ã¢â‚¬â€ admin creates thread (has Update permission)
        var client = GetClient();
        var response = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode("SecureProject", "Admin creating a thread")), o => o.WithTarget(new Address("SecureProject"))).Should().Within(15.Seconds()).Emit();

        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        response.Message.Node?.Path.Should().Contain($"/{ThreadNodeType.ThreadPartition}/");
    }

    [Fact]
    public void CreateThread_WithoutUpdatePermission_IsDenied()
    {
        // Create context node as admin first
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = AdminUserId, Name = "Admin" });

        // Wait for the admin AccessAssignment from SetupAccessRightsAsync to land
        // through SyncedQuery â€” same eventually-consistent race as the sibling
        // test. Stream-poll until admin has Create permission.
        Mesh.GetEffectivePermissions("SecureProject", AdminUserId)
            .Should().Within(10.Seconds()).Match(p => p.HasFlag(Permission.Create));

        NodeFactory.CreateNode(
            new MeshNode("SecureProject") { Name = "Secure Project", NodeType = "Markdown" }).Should().Emit();

        // Switch to viewer (Read+Execute only, no Update)
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = ViewerUserId, Name = "Viewer" });

        // Act Ã¢â‚¬â€ viewer tries to create thread (lacks Thread permission)
        var client = GetClient();
        Action act = () => client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode("SecureProject", "Viewer trying to create a thread")), o => o.WithTarget(new Address("SecureProject"))).Should().Within(15.Seconds()).Emit();

        // Assert Ã¢â‚¬â€ should be denied (thrown as DeliveryFailureException)
        var ex = act.Should().Throw<Exception>();
        ex.Which.Message.Should().Contain("Access denied");
        Output.WriteLine($"Denial message: {ex.Which.Message}");
    }
}
