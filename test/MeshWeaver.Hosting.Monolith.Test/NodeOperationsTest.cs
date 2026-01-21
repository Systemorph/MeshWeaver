using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// A validator that rejects nodes with "rejected" in their name during creation.
/// </summary>
public class RejectingNodeValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        if (context.Node.Name?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(NodeValidationResult.Invalid(
                $"Node name '{context.Node.Name}' is not allowed by hub policy",
                NodeRejectionReason.ValidationFailed));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// A validator that requires Content to be set on nodes of specific type during creation.
/// </summary>
public class RequireContentValidator(string nodeType) : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Only validate nodes of the specific type
        if (context.Node.NodeType != nodeType)
            return Task.FromResult(NodeValidationResult.Valid());

        if (context.Node.Content == null)
        {
            return Task.FromResult(NodeValidationResult.Invalid(
                "Node must have Content set",
                NodeRejectionReason.ValidationFailed));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// A validator that prevents deletion of nodes marked as protected (via Content).
/// </summary>
public class ProtectedNodeDeletionValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Check if Content contains a protected flag
        if (context.Node.Content is ProtectedContent { IsProtected: true })
        {
            return Task.FromResult(NodeValidationResult.Invalid(
                $"Node '{context.Node.Path}' is protected and cannot be deleted",
                NodeRejectionReason.ValidationFailed));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Sample content type with protection flag.
/// </summary>
public record ProtectedContent(string Title, bool IsProtected = false);

public class NodeOperationsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    #region CreateNodeRequest Tests

    [Fact]
    public async Task CreateNode_Success()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("TestNode", "test/path") { Name = "Test Node" };
        var request = new CreateNodeRequest(node) { CreatedBy = "TestUser" };

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<CreateNodeResponse>();
        var createResponse = response.Message;
        createResponse.Success.Should().BeTrue();
        createResponse.Node.Should().NotBeNull();
        createResponse.Node!.Path.Should().Be("test/path/TestNode");
        createResponse.Node.State.Should().Be(MeshNodeState.Active);
        createResponse.Node.Name.Should().Be("Test Node");
    }

    [Fact]
    public async Task CreateNode_AlreadyExists_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("ExistingNode", "test") { Name = "Existing Node" };
        var request = new CreateNodeRequest(node);

        // Create the node first
        var firstResponse = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        firstResponse.Message.Success.Should().BeTrue();

        // Act - try to create the same node again
        var secondResponse = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        secondResponse.Message.Success.Should().BeFalse();
        secondResponse.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.NodeAlreadyExists);
        secondResponse.Message.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateNode_InvalidPath_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("", "test") { Name = "Invalid Node" }; // Empty Id
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task CreateNode_WithContent()
    {
        // Arrange
        var client = GetClient();
        var content = new { Title = "My Content", Value = 42 };
        var node = new MeshNode("ContentNode", "content/test")
        {
            Name = "Content Node",
            Content = content
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateNode_VerifyPersistence()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("PersistNode", "persist/test") { Name = "Persist Node" };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        response.Message.Success.Should().BeTrue();

        // Verify via catalog
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var retrievedNode = await catalog.GetNodeAsync(new Address("persist/test/PersistNode"));

        // Assert
        retrievedNode.Should().NotBeNull();
        retrievedNode!.Name.Should().Be("Persist Node");
        retrievedNode.State.Should().Be(MeshNodeState.Active);
    }

    #endregion

    #region DeleteNodeRequest Tests

    [Fact]
    public async Task DeleteNode_Success()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("ToDelete", "delete/test") { Name = "To Delete" };

        // Create node first
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - delete the node
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("delete/test/ToDelete") { DeletedBy = "TestUser" },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify node is gone
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var deletedNode = await catalog.GetNodeAsync(new Address("delete/test/ToDelete"));
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNode_NotFound_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var request = new DeleteNodeRequest("nonexistent/path/Node");

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.NodeNotFound);
        response.Message.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task DeleteNode_WithChildren_NonRecursive_ShouldFail()
    {
        // Arrange
        var client = GetClient();

        // Create parent node
        var parent = new MeshNode("Parent", "hierarchy") { Name = "Parent" };
        await client.AwaitResponse(
            new CreateNodeRequest(parent),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Create child node
        var child = new MeshNode("Child", "hierarchy/Parent") { Name = "Child" };
        await client.AwaitResponse(
            new CreateNodeRequest(child),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Act - try to delete parent without recursive flag
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("hierarchy/Parent") { Recursive = false },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeFalse();
        deleteResponse.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.HasChildren);
        deleteResponse.Message.Error.Should().Contain("has children");
    }

    [Fact]
    public async Task DeleteNode_WithChildren_Recursive_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();

        // Create parent node
        var parent = new MeshNode("RecursiveParent", "recursive") { Name = "Parent" };
        await client.AwaitResponse(
            new CreateNodeRequest(parent),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Create child node
        var child = new MeshNode("RecursiveChild", "recursive/RecursiveParent") { Name = "Child" };
        await client.AwaitResponse(
            new CreateNodeRequest(child),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Act - delete parent with recursive flag
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("recursive/RecursiveParent") { Recursive = true },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify both parent and child are gone
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var deletedParent = await catalog.GetNodeAsync(new Address("recursive/RecursiveParent"));
        var deletedChild = await catalog.GetNodeAsync(new Address("recursive/RecursiveParent/RecursiveChild"));
        deletedParent.Should().BeNull();
        deletedChild.Should().BeNull();
    }

    [Fact]
    public async Task CreateAndDeleteNode_FullLifecycle()
    {
        // Arrange
        var client = GetClient();
        var nodePath = "lifecycle/test/Node";
        var node = new MeshNode("Node", "lifecycle/test") { Name = "Lifecycle Test" };

        // Act & Assert - Create
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();
        createResponse.Message.Node!.State.Should().Be(MeshNodeState.Active);

        // Verify exists
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var existingNode = await catalog.GetNodeAsync(new Address(nodePath));
        existingNode.Should().NotBeNull();

        // Act - Delete
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest(nodePath),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify deleted
        var deletedNode = await catalog.GetNodeAsync(new Address(nodePath));
        deletedNode.Should().BeNull();
    }

    #endregion
}

/// <summary>
/// Test class with a hub validator registered to test rejection scenarios.
/// </summary>
public class NodeOperationsWithValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register the rejecting validator
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator, RejectingNodeValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task CreateNode_HubValidatorRejects_ShouldFailAndDeleteTransientNode()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("RejectedByHub", "hubvalidation/test")
        {
            Name = "This will be rejected by hub policy"
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Message.Error.Should().Contain("not allowed by hub policy");

        // Verify the transient node was deleted (not left behind)
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var deletedNode = await catalog.GetNodeAsync(new Address("hubvalidation/test/RejectedByHub"));
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task CreateNode_HubValidatorAllows_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("AllowedByHub", "hubvalidation/test")
        {
            Name = "This is perfectly fine"
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.State.Should().Be(MeshNodeState.Active);
        response.Message.Node.Name.Should().Be("This is perfectly fine");
    }

    [Fact]
    public async Task CreateNode_TransientStateIsClearedOnRejection()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var nodePath = "transientcleanup/test/RejectedTransient";

        var node = new MeshNode("RejectedTransient", "transientcleanup/test")
        {
            Name = "rejected by policy"
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - creation failed
        response.Message.Success.Should().BeFalse();

        // Verify no trace of the node exists
        var nodeAfterRejection = await catalog.GetNodeAsync(new Address(nodePath));
        nodeAfterRejection.Should().BeNull("transient node should be deleted after rejection");

        // Also verify via persistence directly
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var existsInPersistence = await persistence.ExistsAsync(nodePath);
        existsInPersistence.Should().BeFalse("transient node should be removed from persistence after rejection");
    }
}

/// <summary>
/// Test class with RequireContentValidator injected into NodeType to test that Content must be set.
/// </summary>
public class NodeOperationsWithContentValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContentRequiredNodeType = "content-required";
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register the NodeType as a MeshNode so it passes NodeType validation
        builder.AddMeshNodes(new MeshNode(ContentRequiredNodeType) { Name = "Content Required" });

        // Register RequireContentValidator globally for the specific NodeType
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator>(new RequireContentValidator(ContentRequiredNodeType)));

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task CreateNode_WithNodeType_WithoutContent_ShouldFailValidation()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("NoContentNode", "content/validation")
        {
            Name = "Node without content",
            NodeType = ContentRequiredNodeType  // NodeType triggers the validator
            // Content is NOT set
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Message.Error.Should().Contain("Content");

        // Verify the transient node was cleaned up
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var deletedNode = await catalog.GetNodeAsync(new Address("content/validation/NoContentNode"));
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task CreateNode_WithNodeType_WithContent_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("WithContentNode", "content/validation")
        {
            Name = "Node with content",
            NodeType = ContentRequiredNodeType,
            Content = new { Title = "My Data", Value = 123 }
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Content.Should().NotBeNull();
        response.Message.Node.State.Should().Be(MeshNodeState.Active);
    }

    [Fact]
    public async Task CreateNode_WithoutNodeType_WithoutContent_ShouldSucceed()
    {
        // Arrange - node without NodeType should NOT trigger NodeType validators
        var client = GetClient();
        var node = new MeshNode("NoTypeNoContent", "content/validation")
        {
            Name = "Node without NodeType"
            // No NodeType set - validator should NOT be applied
            // No Content set - but should still succeed
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - should succeed because validator is only applied to ContentRequiredNodeType
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.State.Should().Be(MeshNodeState.Active);
    }
}

/// <summary>
/// Test class with ProtectedNodeDeletionValidator to test deletion protection.
/// </summary>
public class NodeOperationsWithDeletionValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register validator that prevents deletion of protected nodes
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator, ProtectedNodeDeletionValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task DeleteNode_ProtectedNode_ShouldFailValidation()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create a protected node
        var node = new MeshNode("ProtectedNode", "deletion/validation")
        {
            Name = "Protected Node",
            Content = new ProtectedContent("Important Data", IsProtected: true)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to delete the protected node
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("deletion/validation/ProtectedNode"),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeFalse();
        deleteResponse.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        deleteResponse.Message.Error.Should().Contain("protected");

        // Verify the node still exists
        var existingNode = await catalog.GetNodeAsync(new Address("deletion/validation/ProtectedNode"));
        existingNode.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_UnprotectedNode_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create an unprotected node
        var node = new MeshNode("UnprotectedNode", "deletion/validation")
        {
            Name = "Unprotected Node",
            Content = new ProtectedContent("Regular Data", IsProtected: false)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - delete the unprotected node
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("deletion/validation/UnprotectedNode"),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify the node is deleted
        var deletedNode = await catalog.GetNodeAsync(new Address("deletion/validation/UnprotectedNode"));
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNode_NodeWithoutProtectedContent_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create a node with different content type (not ProtectedContent)
        var node = new MeshNode("RegularNode", "deletion/validation")
        {
            Name = "Regular Node",
            Content = new { Data = "Some data" }  // Not ProtectedContent
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - delete the node
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("deletion/validation/RegularNode"),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify the node is deleted
        var deletedNode = await catalog.GetNodeAsync(new Address("deletion/validation/RegularNode"));
        deletedNode.Should().BeNull();
    }
}

/// <summary>
/// Sample content type for validated node type.
/// </summary>
public record ValidatedContent(string Title, string? Description = null);

/// <summary>
/// A NodeType-specific creation validator that requires Title to be non-empty.
/// Only applies to nodes with NodeType="validated" or "combined".
/// </summary>
public class RequireTitleValidator : INodeValidator
{
    private static readonly string[] ApplicableNodeTypes = ["validated", "combined"];

    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Only apply to specific NodeTypes
        if (!ApplicableNodeTypes.Contains(context.Node.NodeType))
            return Task.FromResult(NodeValidationResult.Valid());

        if (context.Node.Content is ValidatedContent content && string.IsNullOrWhiteSpace(content.Title))
        {
            return Task.FromResult(NodeValidationResult.Invalid(
                "ValidatedContent must have a non-empty Title",
                NodeRejectionReason.ValidationFailed));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// A NodeType-specific deletion validator that prevents deletion if Description is "locked".
/// </summary>
public class PreventLockedDeletionValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        if (context.Node.Content is ValidatedContent { Description: "locked" })
        {
            return Task.FromResult(NodeValidationResult.Invalid(
                "Cannot delete node with locked description",
                NodeRejectionReason.ValidationFailed));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class that registers validators via NodeTypeConfiguration.
/// </summary>
public class NodeOperationsWithNodeTypeValidatorsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ValidatedNodeType = "validated";
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register the NodeType as a MeshNode so it passes NodeType validation
        builder.AddMeshNodes(new MeshNode(ValidatedNodeType) { Name = "Validated" });

        // Register validators globally - they check Content type, so they only apply to ValidatedContent nodes
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeValidator, RequireTitleValidator>();
            services.AddSingleton<INodeValidator, PreventLockedDeletionValidator>();
            return services;
        });

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task CreateNode_NodeTypeValidator_WithEmptyTitle_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("NoTitleNode", "nodetype/validation")
        {
            Name = "No Title Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "")  // Empty title
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Message.Error.Should().Contain("Title");

        // Verify transient node was cleaned up
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var deletedNode = await catalog.GetNodeAsync(new Address("nodetype/validation/NoTitleNode"));
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task CreateNode_NodeTypeValidator_WithValidTitle_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("ValidTitleNode", "nodetype/validation")
        {
            Name = "Valid Title Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "My Valid Title")
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.State.Should().Be(MeshNodeState.Active);
    }

    [Fact]
    public async Task CreateNode_DifferentNodeType_ValidatorNotApplied()
    {
        // Arrange - create a node without the validated NodeType
        var client = GetClient();
        var node = new MeshNode("OtherTypeNode", "nodetype/validation")
        {
            Name = "Other Type Node",
            // No NodeType set - validator should NOT be applied
            Content = new ValidatedContent(Title: "")  // Empty title would fail if validator ran
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - should succeed because validator is not applied to this NodeType
        response.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteNode_NodeTypeValidator_LockedDescription_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create a node with locked description
        var node = new MeshNode("LockedNode", "nodetype/deletion")
        {
            Name = "Locked Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Locked", Description: "locked")
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to delete the locked node
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("nodetype/deletion/LockedNode"),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeFalse();
        deleteResponse.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        deleteResponse.Message.Error.Should().Contain("locked");

        // Verify node still exists
        var existingNode = await catalog.GetNodeAsync(new Address("nodetype/deletion/LockedNode"));
        existingNode.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_NodeTypeValidator_UnlockedDescription_ShouldSucceed()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create a node with unlocked description
        var node = new MeshNode("UnlockedNode", "nodetype/deletion")
        {
            Name = "Unlocked Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Unlocked", Description: "not-locked")
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - delete the unlocked node
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("nodetype/deletion/UnlockedNode"),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify node is deleted
        var deletedNode = await catalog.GetNodeAsync(new Address("nodetype/deletion/UnlockedNode"));
        deletedNode.Should().BeNull();
    }
}

/// <summary>
/// Test class that combines global DI validators with NodeType validators.
/// </summary>
public class NodeOperationsWithCombinedValidatorsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ValidatedNodeType = "combined";
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register the NodeType as a MeshNode so it passes NodeType validation
        builder.AddMeshNodes(new MeshNode(ValidatedNodeType) { Name = "Combined" });

        // Register validators globally via DI
        // RejectingNodeValidator rejects names containing "rejected"
        // RequireTitleValidator checks ValidatedContent has non-empty Title
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeValidator, RejectingNodeValidator>();
            services.AddSingleton<INodeValidator, RequireTitleValidator>();
            return services;
        });

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task CreateNode_GlobalValidatorRejection_TakesPrecedence()
    {
        // Arrange - node name triggers global rejection
        var client = GetClient();
        var node = new MeshNode("RejectedByCombined", "combined/test")
        {
            Name = "This will be rejected by global policy",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Valid Title")  // Would pass NodeType validator
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - global validator rejects first
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Message.Error.Should().Contain("not allowed by hub policy");
    }

    [Fact]
    public async Task CreateNode_GlobalPasses_NodeTypeValidatorRejects()
    {
        // Arrange - node name doesn't trigger global rejection, but empty title triggers NodeType rejection
        var client = GetClient();
        var node = new MeshNode("ValidGlobalInvalidNodeType", "combined/test")
        {
            Name = "Valid global name",  // Passes global validator
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "")  // Fails NodeType validator
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - NodeType validator rejects after global passes
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
        response.Message.Error.Should().Contain("Title");
    }

    [Fact]
    public async Task CreateNode_BothValidatorsPass_ShouldSucceed()
    {
        // Arrange - node passes both validators
        var client = GetClient();
        var node = new MeshNode("ValidCombined", "combined/test")
        {
            Name = "Valid name",  // Passes global validator
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Valid Title")  // Passes NodeType validator
        };
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.State.Should().Be(MeshNodeState.Active);
    }
}

/// <summary>
/// Content type for read validation tests.
/// </summary>
public record ReadableContent(string Title, bool IsHidden = false);

/// <summary>
/// A read validator that hides nodes with IsHidden=true in Content.
/// </summary>
public class HiddenNodeReadValidator : INodeValidator
{
    private const string ReadableNodeType = "readable";

    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Only apply to nodes with the readable NodeType
        if (context.Node.NodeType != ReadableNodeType)
            return Task.FromResult(NodeValidationResult.Valid());

        if (context.Node.Content is ReadableContent { IsHidden: true })
        {
            return Task.FromResult(new NodeValidationResult(
                false,
                $"Node '{context.Node.Path}' is hidden",
                NodeRejectionReason.NodeHidden));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with read validators via NodeType configuration.
/// </summary>
public class NodeOperationsWithReadValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ReadableNodeType = "readable";
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register the NodeType as a MeshNode so it passes NodeType validation
        builder.AddMeshNodes(new MeshNode(ReadableNodeType) { Name = "Readable" });

        // Register HiddenNodeReadValidator globally - it checks Content type, so only applies to ReadableContent nodes
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator, HiddenNodeReadValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task GetNode_HiddenNode_ShouldReturnNull()
    {
        // Arrange - create a hidden node
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var node = new MeshNode("HiddenNode", "read/validation")
        {
            Name = "Hidden Node",
            NodeType = ReadableNodeType,
            Content = new ReadableContent(Title: "Hidden", IsHidden: true)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to read the hidden node
        var readNode = await catalog.GetNodeAsync(new Address("read/validation/HiddenNode"));

        // Assert - should return null because the node is hidden
        readNode.Should().BeNull();
    }

    [Fact]
    public async Task GetNode_VisibleNode_ShouldReturnNode()
    {
        // Arrange - create a visible node
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var node = new MeshNode("VisibleNode", "read/validation")
        {
            Name = "Visible Node",
            NodeType = ReadableNodeType,
            Content = new ReadableContent(Title: "Visible", IsHidden: false)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - read the visible node
        var readNode = await catalog.GetNodeAsync(new Address("read/validation/VisibleNode"));

        // Assert - should return the node
        readNode.Should().NotBeNull();
        readNode!.Name.Should().Be("Visible Node");
    }

    [Fact]
    public async Task GetNode_NodeWithoutNodeType_ReadValidatorNotApplied()
    {
        // Arrange - create a node without NodeType (validator should not apply)
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var node = new MeshNode("NoTypeHiddenNode", "read/validation")
        {
            Name = "No Type Hidden Node",
            // No NodeType - validator should NOT be applied
            Content = new ReadableContent(Title: "Hidden", IsHidden: true)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - read the node
        var readNode = await catalog.GetNodeAsync(new Address("read/validation/NoTypeHiddenNode"));

        // Assert - should return the node because validator doesn't apply to nodes without NodeType
        readNode.Should().NotBeNull();
    }
}

/// <summary>
/// A global read validator that blocks all nodes with "blocked" in their name.
/// </summary>
public class BlockedNodeReadValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        if (context.Node.Name?.Contains("blocked", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(new NodeValidationResult(
                false,
                $"Node '{context.Node.Path}' is blocked by global policy",
                NodeRejectionReason.Unauthorized));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with global read validator via DI.
/// </summary>
public class NodeOperationsWithGlobalReadValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register global read validator via DI
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator, BlockedNodeReadValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task GetNode_BlockedByGlobalValidator_ShouldReturnNull()
    {
        // Arrange - create a node with "blocked" in name
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var node = new MeshNode("BlockedByPolicy", "global/read")
        {
            Name = "This is blocked by global policy"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to read the blocked node
        var readNode = await catalog.GetNodeAsync(new Address("global/read/BlockedByPolicy"));

        // Assert - should return null because the node is blocked
        readNode.Should().BeNull();
    }

    [Fact]
    public async Task GetNode_NotBlockedByGlobalValidator_ShouldReturnNode()
    {
        // Arrange - create a node without "blocked" in name
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var node = new MeshNode("AllowedNode", "global/read")
        {
            Name = "This is allowed"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - read the allowed node
        var readNode = await catalog.GetNodeAsync(new Address("global/read/AllowedNode"));

        // Assert - should return the node
        readNode.Should().NotBeNull();
        readNode!.Name.Should().Be("This is allowed");
    }
}

/// <summary>
/// Content type for update validation tests.
/// </summary>
public record UpdatableContent(string Title, int Version);

/// <summary>
/// An update validator that prevents version downgrades.
/// </summary>
public class NoVersionDowngradeValidator : INodeValidator
{
    private const string UpdatableNodeType = "updatable";

    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        // Only apply to Update operations with existing node
        if (context.ExistingNode == null)
            return Task.FromResult(NodeValidationResult.Valid());

        // Only apply to nodes with the updatable NodeType
        if (context.ExistingNode.NodeType != UpdatableNodeType)
            return Task.FromResult(NodeValidationResult.Valid());

        if (context.ExistingNode.Content is UpdatableContent existingContent &&
            context.Node.Content is UpdatableContent updatedContent)
        {
            if (updatedContent.Version < existingContent.Version)
            {
                return Task.FromResult(new NodeValidationResult(
                    false,
                    $"Cannot downgrade version from {existingContent.Version} to {updatedContent.Version}",
                    NodeRejectionReason.ValidationFailed));
            }
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with update validators via NodeType configuration.
/// </summary>
public class NodeOperationsWithUpdateValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UpdatableNodeType = "updatable";
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register the NodeType as a MeshNode so it passes NodeType validation
        builder.AddMeshNodes(new MeshNode(UpdatableNodeType) { Name = "Updatable" });

        // Register NoVersionDowngradeValidator globally - it checks Content type, so only applies to UpdatableContent nodes
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator, NoVersionDowngradeValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task UpdateNode_VersionUpgrade_ShouldSucceed()
    {
        // Arrange - create a node with version 1
        var client = GetClient();
        var node = new MeshNode("VersionedNode", "update/validation")
        {
            Name = "Versioned Node",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "Original", Version: 1)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - update with version 2
        var updatedNode = node with
        {
            Name = "Updated Node",
            Content = new UpdatableContent(Title: "Updated", Version: 2)
        };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(updatedNode),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Node.Should().NotBeNull();
        updateResponse.Message.Node!.Name.Should().Be("Updated Node");
    }

    [Fact]
    public async Task UpdateNode_VersionDowngrade_ShouldFail()
    {
        // Arrange - create a node with version 5
        var client = GetClient();
        var node = new MeshNode("HighVersionNode", "update/validation")
        {
            Name = "High Version Node",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "High Version", Version: 5)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to downgrade to version 3
        var downgradedNode = node with
        {
            Name = "Downgraded Node",
            Content = new UpdatableContent(Title: "Downgraded", Version: 3)
        };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(downgradedNode),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeFalse();
        updateResponse.Message.RejectionReason.Should().Be(NodeUpdateRejectionReason.ValidationFailed);
        updateResponse.Message.Error.Should().Contain("downgrade");
    }

    [Fact]
    public async Task UpdateNode_SameVersion_ShouldSucceed()
    {
        // Arrange - create a node with version 1
        var client = GetClient();
        var node = new MeshNode("SameVersionNode", "update/validation")
        {
            Name = "Same Version Node",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "Original", Version: 1)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - update with same version (just change title)
        var updatedNode = node with
        {
            Name = "Updated Same Version",
            Content = new UpdatableContent(Title: "Updated", Version: 1)
        };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(updatedNode),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Node.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateNode_NonExistentNode_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var node = new MeshNode("NonExistentNode", "update/validation")
        {
            Name = "Non Existent",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "Ghost", Version: 1)
        };

        // Act - try to update a node that doesn't exist
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeFalse();
        updateResponse.Message.RejectionReason.Should().Be(NodeUpdateRejectionReason.NodeNotFound);
    }

    [Fact]
    public async Task UpdateNode_NodeWithoutNodeType_ValidatorNotApplied()
    {
        // Arrange - create a node without NodeType
        var client = GetClient();
        var node = new MeshNode("NoTypeNode", "update/validation")
        {
            Name = "No Type Node",
            // No NodeType - validator should NOT be applied
            Content = new UpdatableContent(Title: "Original", Version: 5)
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to downgrade version (would fail with validator, but should succeed without)
        var downgradedNode = node with
        {
            Name = "Downgraded No Type",
            Content = new UpdatableContent(Title: "Downgraded", Version: 1)
        };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(downgradedNode),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - should succeed because validator doesn't apply
        updateResponse.Message.Success.Should().BeTrue();
    }
}

/// <summary>
/// A global update validator that prevents changing the Name to contain "forbidden".
/// </summary>
public class ForbiddenNameUpdateValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

    public Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default)
    {
        if (context.Node.Name?.Contains("forbidden", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(new NodeValidationResult(
                false,
                "Cannot update node name to contain 'forbidden'",
                NodeRejectionReason.ValidationFailed));
        }
        return Task.FromResult(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with global update validator via DI.
/// </summary>
public class NodeOperationsWithGlobalUpdateValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Register global update validator via DI
        builder.ConfigureServices(services =>
            services.AddSingleton<INodeValidator, ForbiddenNameUpdateValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task UpdateNode_ForbiddenName_ShouldFail()
    {
        // Arrange - create a node
        var client = GetClient();
        var node = new MeshNode("NormalNode", "global/update")
        {
            Name = "Normal Node"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - try to update with forbidden name
        var updatedNode = node with
        {
            Name = "This is forbidden by policy"
        };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(updatedNode),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeFalse();
        updateResponse.Message.RejectionReason.Should().Be(NodeUpdateRejectionReason.ValidationFailed);
        updateResponse.Message.Error.Should().Contain("forbidden");
    }

    [Fact]
    public async Task UpdateNode_AllowedName_ShouldSucceed()
    {
        // Arrange - create a node
        var client = GetClient();
        var node = new MeshNode("AllowedUpdateNode", "global/update")
        {
            Name = "Allowed Node"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - update with allowed name
        var updatedNode = node with
        {
            Name = "Updated Allowed Node"
        };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(updatedNode),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Node.Should().NotBeNull();
        updateResponse.Message.Node!.Name.Should().Be("Updated Allowed Node");
    }
}
