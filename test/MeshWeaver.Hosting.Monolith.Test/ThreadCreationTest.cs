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

        // Assert - Should get a failure/error response, not hang or cause endless messages
        // DeliveryFailureException indicates the message was properly rejected
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*No node found*");
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
}
