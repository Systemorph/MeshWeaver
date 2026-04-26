using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// A validator that rejects nodes with "rejected" in their name during creation.
/// </summary>
public class RejectingNodeValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.Node.Name?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Observable.Return(NodeValidationResult.Invalid(
                $"Node name '{context.Node.Name}' is not allowed by hub policy",
                NodeRejectionReason.ValidationFailed));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// A validator that requires Content to be set on nodes of specific type during creation.
/// </summary>
public class RequireContentValidator(string nodeType) : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // Only validate nodes of the specific type
        if (context.Node.NodeType != nodeType)
            return Observable.Return(NodeValidationResult.Valid());

        if (context.Node.Content == null)
        {
            return Observable.Return(NodeValidationResult.Invalid(
                "Node must have Content set",
                NodeRejectionReason.ValidationFailed));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// A validator that prevents deletion of nodes marked as protected (via Content).
/// </summary>
public class ProtectedNodeDeletionValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // Check if Content contains a protected flag
        if (context.Node.Content is ProtectedContent { IsProtected: true })
        {
            return Observable.Return(NodeValidationResult.Invalid(
                $"Node '{context.Node.Path}' is protected and cannot be deleted",
                NodeRejectionReason.ValidationFailed));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Sample content type with protection flag.
/// </summary>
public record ProtectedContent(string Title, bool IsProtected = false);

[Collection("NodeOperationsTests")]
public class NodeOperationsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    #region CreateNodeRequest Tests

    [Fact]
    public async Task CreateNode_Success()
    {
        // Arrange — legal node: has NodeType (and would also pass with just Content set).
        var node = new MeshNode("TestNode", "test/path")
        {
            Name = "Test Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Test" }
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be("test/path/TestNode");
        createdNode.State.Should().Be(MeshNodeState.Active);
        createdNode.Name.Should().Be("Test Node");
    }

    [Fact]
    public async Task CreateNode_AlreadyExists_ShouldFail()
    {
        // Arrange
        var node = new MeshNode("ExistingNode", "test")
        {
            Name = "Existing Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Existing" }
        };

        // Create the node first
        await NodeFactory.CreateNode(node);

        // Act - try to create the same node again
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateNode_InvalidPath_ShouldFail()
    {
        // Arrange
        var node = new MeshNode("", "test")
        {
            Name = "Invalid Node", // Empty Id
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "" }
        };

        // Act
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task CreateNode_NoTypeAndNoContent_ShouldReject()
    {
        // A bare MeshNode with neither NodeType nor Content can't spawn a useful per-node
        // hub, so HandleCreateNodeRequest must reject it before persisting.
        var node = new MeshNode("BareNode", "bare/test") { Name = "Bare" };

        var act = () => NodeFactory.CreateNode(node).ToTask();

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*NodeType or Content*");
    }

    [Fact]
    public async Task CreateNode_WithContent()
    {
        // Arrange
        var content = new { Title = "My Content", Value = 42 };
        var node = new MeshNode("ContentNode", "content/test")
        {
            Name = "Content Node",
            Content = content
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateNode_VerifyPersistence()
    {
        // Arrange — legal node: has NodeType + Content so it passes the bare-node rejection.
        var node = new MeshNode("PersistNode", "persist/test")
        {
            Name = "Persist Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Persist" }
        };

        // Act
        await NodeFactory.CreateNode(node);

        // Verify via query
        var retrievedNode = await ReadNodeAsync("persist/test/PersistNode");

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
        var node = new MeshNode("ToDelete", "delete/test")
        {
            Name = "To Delete",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Placeholder" }
        };
        await NodeFactory.CreateNode(node);

        // Act - delete the node
        await NodeFactory.DeleteNode("delete/test/ToDelete");

        // Assert - Verify node is gone
        var deletedNode = await ReadNodeAsync("delete/test/ToDelete");
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNode_NotFound_ShouldFail()
    {
        // Act
        var act = () => NodeFactory.DeleteNode("nonexistent/path/Node").ToTask();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact(Skip = "Non-recursive delete via message routing not yet supported — needs proper node hub target")]
    public async Task DeleteNode_WithChildren_NonRecursive_ShouldFail()
    {
        // Arrange
        var client = GetClient();

        // Create parent node under TestData partition (Markdown hub with node operation handlers)
        var parent = new MeshNode("HierParent", TestPartition) { Name = "Parent", NodeType = "Markdown" };
        await NodeFactory.CreateNode(parent);

        // Create child node
        var child = new MeshNode("HierChild", $"{TestPartition}/HierParent") { Name = "Child", NodeType = "Markdown" };
        await NodeFactory.CreateNode(child);

        // Act - try to delete parent without recursive flag — target the TestData partition node (real Markdown hub)
        var deleteResponse = await client.Observe(new DeleteNodeRequest($"{TestPartition}/HierParent") { Recursive = false }, o => o.WithTarget(new Address(TestPartition))).FirstAsync().ToTask();

        // Assert
        deleteResponse.Message.Success.Should().BeFalse();
        deleteResponse.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.HasChildren);
        deleteResponse.Message.Error.Should().Contain("has children");
    }

    [Fact]
    public async Task DeleteNode_WithChildren_Recursive_ShouldSucceed()
    {
        // Arrange
        var parent = new MeshNode("RecursiveParent", "recursive")
        {
            Name = "Parent",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Placeholder" }
        };
        await NodeFactory.CreateNode(parent);

        var child = new MeshNode("RecursiveChild", "recursive/RecursiveParent")
        {
            Name = "Child",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Placeholder" }
        };
        await NodeFactory.CreateNode(child);

        // Act - delete parent (NodeFactory.DeleteNodeAsync uses recursive by default)
        await NodeFactory.DeleteNode("recursive/RecursiveParent");

        // Assert - Verify both parent and child are gone
        var deletedParent = await ReadNodeAsync("recursive/RecursiveParent");
        var deletedChild = await ReadNodeAsync("recursive/RecursiveParent/RecursiveChild");
        deletedParent.Should().BeNull();
        deletedChild.Should().BeNull();
    }

    [Fact]
    public async Task CreateAndDeleteNode_FullLifecycle()
    {
        // Arrange — use a legal NodeType so the per-node hub gets AddMeshDataSource
        // (and therefore a GetDataRequest handler for ReadNodeAsync below).
        var nodePath = "lifecycle/test/Node";
        var node = new MeshNode("Node", "lifecycle/test") { Name = "Lifecycle Test", NodeType = "Markdown" };

        // Act & Assert - Create
        var createdNode = await NodeFactory.CreateNode(node);
        createdNode.State.Should().Be(MeshNodeState.Active);

        // Verify exists via stream (CQRS-correct)
        var existingNode = await ReadNodeAsync(nodePath);
        existingNode.Should().NotBeNull();

        // Act - Delete
        await NodeFactory.DeleteNode(nodePath);

        // Verify deleted — per-node hub returns null Data for missing path.
        var deletedNode = await ReadNodeAsync(nodePath);
        deletedNode.Should().BeNull();
    }

    #endregion
}

/// <summary>
/// Test class with a hub validator registered to test rejection scenarios.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

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
        // Arrange — legal node (NodeType + Content); validator must reject on Name.
        var node = new MeshNode("RejectedByHub", "hubvalidation/test")
        {
            Name = "This will be rejected by hub policy",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Rejected" }
        };

        // Act
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*not allowed by hub policy*");

        // Verify the transient node was deleted (not left behind)
        var deletedNode = await ReadNodeAsync("hubvalidation/test/RejectedByHub");
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task CreateNode_HubValidatorAllows_ShouldSucceed()
    {
        // Arrange — legal node (NodeType + Content)
        var node = new MeshNode("AllowedByHub", "hubvalidation/test")
        {
            Name = "This is perfectly fine",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Allowed" }
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.State.Should().Be(MeshNodeState.Active);
        createdNode.Name.Should().Be("This is perfectly fine");
    }

    [Fact]
    public async Task CreateNode_TransientStateIsClearedOnRejection()
    {
        // Arrange — legal node (NodeType + Content); validator must reject on Name.
        var nodePath = "transientcleanup/test/RejectedTransient";

        var node = new MeshNode("RejectedTransient", "transientcleanup/test")
        {
            Name = "rejected by policy",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Rejected" }
        };

        // Act — creation should fail due to validator
        var act = () => NodeFactory.CreateNode(node).ToTask();
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // Verify no trace of the node exists.
        var nodeAfterRejection = await ReadNodeAsync(nodePath);
        nodeAfterRejection.Should().BeNull("transient node should be deleted after rejection");
    }
}

/// <summary>
/// Test class with RequireContentValidator injected into NodeType to test that Content must be set.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithContentValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContentRequiredNodeType = "content-required";
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // NodeType definition lives in IStaticNodeProvider so the per-node hub
        // gets AddMeshDataSource via HubConfiguration. See Doc/Architecture/TestStateIsolation.
        builder.ConfigureServices(services => services
            .AddSingleton<IStaticNodeProvider, ContentRequiredTypeProvider>()
            .AddSingleton<INodeValidator>(new RequireContentValidator(ContentRequiredNodeType)));

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task CreateNode_WithNodeType_WithoutContent_ShouldFailValidation()
    {
        // Arrange
        var node = new MeshNode("NoContentNode", "content/validation")
        {
            Name = "Node without content",
            NodeType = ContentRequiredNodeType  // NodeType triggers the validator
            // Content is NOT set
        };

        // Act
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Content*");

        // Verify the transient node was cleaned up
        var deletedNode = await ReadNodeAsync("content/validation/NoContentNode");
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task CreateNode_WithNodeType_WithContent_ShouldSucceed()
    {
        // Arrange
        var node = new MeshNode("WithContentNode", "content/validation")
        {
            Name = "Node with content",
            NodeType = ContentRequiredNodeType,
            Content = new { Title = "My Data", Value = 123 }
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Content.Should().NotBeNull();
        createdNode.State.Should().Be(MeshNodeState.Active);
    }

    [Fact]
    public async Task CreateNode_DifferentNodeType_WithoutContent_ShouldSucceed()
    {
        // Arrange - node with a NodeType OTHER than ContentRequiredNodeType should NOT
        // trigger the type-keyed RequireContentValidator. We use "Markdown" so the bare-node
        // guard also passes (NodeType is set), letting us exercise the validator-not-applied path.
        var node = new MeshNode("NoTypeNoContent", "content/validation")
        {
            Name = "Node with non-validated NodeType",
            NodeType = "Markdown" // Different from ContentRequiredNodeType — validator should NOT fire
            // No Content set - but should still succeed because validator is type-keyed
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert - should succeed because validator is only applied to ContentRequiredNodeType
        createdNode.Should().NotBeNull();
        createdNode.State.Should().Be(MeshNodeState.Active);
    }
}

/// <summary>
/// Test class with ProtectedNodeDeletionValidator to test deletion protection.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithDeletionValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

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
        // Arrange — create a protected node. NodeType is required so the per-node hub
        // for "deletion/validation/ProtectedNode" gets AddMeshDataSource via Markdown's
        // HubConfiguration; otherwise the post-rejection ReadNodeAsync at the bottom of
        // the test routes to a non-existent hub and returns null instead of the node.
        var node = new MeshNode("ProtectedNode", "deletion/validation")
        {
            Name = "Protected Node",
            NodeType = "Markdown",
            Content = new ProtectedContent("Important Data", IsProtected: true)
        };
        await NodeFactory.CreateNode(node);

        // Act - try to delete the protected node
        var act = () => NodeFactory.DeleteNode("deletion/validation/ProtectedNode").ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*protected*");

        // Verify the node still exists
        var existingNode = await ReadNodeAsync("deletion/validation/ProtectedNode");
        existingNode.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_UnprotectedNode_ShouldSucceed()
    {
        // Arrange — create an unprotected node
        var node = new MeshNode("UnprotectedNode", "deletion/validation")
        {
            Name = "Unprotected Node",
            Content = new ProtectedContent("Regular Data", IsProtected: false)
        };
        await NodeFactory.CreateNode(node);

        // Act - delete the unprotected node
        await NodeFactory.DeleteNode("deletion/validation/UnprotectedNode");

        // Assert — verify the node is deleted
        var deletedNode = await ReadNodeAsync("deletion/validation/UnprotectedNode");
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNode_NodeWithoutProtectedContent_ShouldSucceed()
    {
        // Arrange — create a node with different content type (not ProtectedContent)
        var node = new MeshNode("RegularNode", "deletion/validation")
        {
            Name = "Regular Node",
            Content = new { Data = "Some data" }  // Not ProtectedContent
        };
        await NodeFactory.CreateNode(node);

        // Act - delete the node
        await NodeFactory.DeleteNode("deletion/validation/RegularNode");

        // Assert — verify the node is deleted
        var deletedNode = await ReadNodeAsync("deletion/validation/RegularNode");
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

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // Only apply to specific NodeTypes
        if (!ApplicableNodeTypes.Contains(context.Node.NodeType))
            return Observable.Return(NodeValidationResult.Valid());

        if (context.Node.Content is ValidatedContent content && string.IsNullOrWhiteSpace(content.Title))
        {
            return Observable.Return(NodeValidationResult.Invalid(
                "ValidatedContent must have a non-empty Title",
                NodeRejectionReason.ValidationFailed));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// A NodeType-specific deletion validator that prevents deletion if Description is "locked".
/// </summary>
public class PreventLockedDeletionValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.Node.Content is ValidatedContent { Description: "locked" })
        {
            return Observable.Return(NodeValidationResult.Invalid(
                "Cannot delete node with locked description",
                NodeRejectionReason.ValidationFailed));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class that registers validators via NodeTypeConfiguration.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithNodeTypeValidatorsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ValidatedNodeType = "validated";
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // NodeType definition lives in IStaticNodeProvider (see TestStateIsolation).
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, ValidatedTypeProvider>();
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
        var node = new MeshNode("NoTitleNode", "nodetype/validation")
        {
            Name = "No Title Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "")  // Empty title
        };

        // Act
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Title*");

        // Verify transient node was cleaned up
        var deletedNode = await ReadNodeAsync("nodetype/validation/NoTitleNode");
        deletedNode.Should().BeNull();
    }

    [Fact]
    public async Task CreateNode_NodeTypeValidator_WithValidTitle_ShouldSucceed()
    {
        // Arrange
        var node = new MeshNode("ValidTitleNode", "nodetype/validation")
        {
            Name = "Valid Title Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "My Valid Title")
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.State.Should().Be(MeshNodeState.Active);
    }

    [Fact]
    public async Task CreateNode_DifferentNodeType_ValidatorNotApplied()
    {
        // Arrange - create a node without the validated NodeType
        var node = new MeshNode("OtherTypeNode", "nodetype/validation")
        {
            Name = "Other Type Node",
            // No NodeType set - validator should NOT be applied
            Content = new ValidatedContent(Title: "")  // Empty title would fail if validator ran
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert - should succeed because validator is not applied to this NodeType
        createdNode.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_NodeTypeValidator_LockedDescription_ShouldFail()
    {
        // Arrange — create a node with locked description
        var node = new MeshNode("LockedNode", "nodetype/deletion")
        {
            Name = "Locked Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Locked", Description: "locked")
        };
        await NodeFactory.CreateNode(node);

        // Act - try to delete the locked node
        var act = () => NodeFactory.DeleteNode("nodetype/deletion/LockedNode").ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*locked*");

        // Verify node still exists
        var existingNode = await ReadNodeAsync("nodetype/deletion/LockedNode");
        existingNode.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_NodeTypeValidator_UnlockedDescription_ShouldSucceed()
    {
        // Arrange — create a node with unlocked description
        var node = new MeshNode("UnlockedNode", "nodetype/deletion")
        {
            Name = "Unlocked Node",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Unlocked", Description: "not-locked")
        };
        await NodeFactory.CreateNode(node);

        // Act - delete the unlocked node
        await NodeFactory.DeleteNode("nodetype/deletion/UnlockedNode");

        // Assert — verify node is deleted
        var deletedNode = await ReadNodeAsync("nodetype/deletion/UnlockedNode");
        deletedNode.Should().BeNull();
    }
}

/// <summary>
/// Test class that combines global DI validators with NodeType validators.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithCombinedValidatorsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ValidatedNodeType = "combined";
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // NodeType definition lives in IStaticNodeProvider (see TestStateIsolation).
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, CombinedTypeProvider>();
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
        var node = new MeshNode("RejectedByCombined", "combined/test")
        {
            Name = "This will be rejected by global policy",
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Valid Title")  // Would pass NodeType validator
        };

        // Act
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert - global validator rejects first
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*not allowed by hub policy*");
    }

    [Fact]
    public async Task CreateNode_GlobalPasses_NodeTypeValidatorRejects()
    {
        // Arrange - node name doesn't trigger global rejection, but empty title triggers NodeType rejection
        var node = new MeshNode("ValidGlobalInvalidNodeType", "combined/test")
        {
            Name = "Valid global name",  // Passes global validator
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "")  // Fails NodeType validator
        };

        // Act
        var act = () => NodeFactory.CreateNode(node).ToTask();

        // Assert - NodeType validator rejects after global passes
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Title*");
    }

    [Fact]
    public async Task CreateNode_BothValidatorsPass_ShouldSucceed()
    {
        // Arrange - node passes both validators
        var node = new MeshNode("ValidCombined", "combined/test")
        {
            Name = "Valid name",  // Passes global validator
            NodeType = ValidatedNodeType,
            Content = new ValidatedContent(Title: "Valid Title")  // Passes NodeType validator
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.State.Should().Be(MeshNodeState.Active);
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

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // Only apply to nodes with the readable NodeType
        if (context.Node.NodeType != ReadableNodeType)
            return Observable.Return(NodeValidationResult.Valid());

        if (context.Node.Content is ReadableContent { IsHidden: true })
        {
            return Observable.Return(new NodeValidationResult(
                false,
                $"Node '{context.Node.Path}' is hidden",
                NodeRejectionReason.NodeHidden));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with read validators via NodeType configuration.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithReadValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ReadableNodeType = "readable";
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // NodeType definition lives in IStaticNodeProvider (see TestStateIsolation).
        builder.ConfigureServices(services => services
            .AddSingleton<IStaticNodeProvider, ReadableTypeProvider>()
            .AddSingleton<INodeValidator, HiddenNodeReadValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task GetNode_HiddenNode_ShouldReturnNull()
    {
        // Arrange - create a hidden node
        var node = new MeshNode("HiddenNode", "read/validation")
        {
            Name = "Hidden Node",
            NodeType = ReadableNodeType,
            Content = new ReadableContent(Title: "Hidden", IsHidden: true)
        };
        await NodeFactory.CreateNode(node);

        // Act - try to read the hidden node
        var readNode = await ReadNodeAsync("read/validation/HiddenNode");

        // Assert - should return null because the node is hidden
        readNode.Should().BeNull();
    }

    [Fact]
    public async Task GetNode_VisibleNode_ShouldReturnNode()
    {
        // Arrange - create a visible node
        var node = new MeshNode("VisibleNode", "read/validation")
        {
            Name = "Visible Node",
            NodeType = ReadableNodeType,
            Content = new ReadableContent(Title: "Visible", IsHidden: false)
        };
        await NodeFactory.CreateNode(node);

        // Act - read the visible node
        var readNode = await ReadNodeAsync("read/validation/VisibleNode");

        // Assert - should return the node
        readNode.Should().NotBeNull();
        readNode!.Name.Should().Be("Visible Node");
    }

}

/// <summary>
/// A global read validator that blocks all nodes with "blocked" in their name.
/// </summary>
public class BlockedNodeReadValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.Node.Name?.Contains("blocked", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Observable.Return(new NodeValidationResult(
                false,
                $"Node '{context.Node.Path}' is blocked by global policy",
                NodeRejectionReason.Unauthorized));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with global read validator via DI.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithGlobalReadValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

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
        // Arrange - create a node with "blocked" in name (legal: NodeType + Content)
        var node = new MeshNode("BlockedByPolicy", "global/read")
        {
            Name = "This is blocked by global policy",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Blocked" }
        };
        await NodeFactory.CreateNode(node);

        // Act - try to read the blocked node
        var readNode = await ReadNodeAsync("global/read/BlockedByPolicy");

        // Assert - should return null because the node is blocked
        readNode.Should().BeNull();
    }

    [Fact]
    public async Task GetNode_NotBlockedByGlobalValidator_ShouldReturnNode()
    {
        // Arrange - create a node without "blocked" in name (legal: NodeType + Content)
        var node = new MeshNode("AllowedNode", "global/read")
        {
            Name = "This is allowed",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Allowed" }
        };
        await NodeFactory.CreateNode(node);

        // Act - read the allowed node
        var readNode = await ReadNodeAsync("global/read/AllowedNode");

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

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // Only apply to Update operations with existing node
        if (context.ExistingNode == null)
            return Observable.Return(NodeValidationResult.Valid());

        // Only apply to nodes with the updatable NodeType
        if (context.ExistingNode.NodeType != UpdatableNodeType)
            return Observable.Return(NodeValidationResult.Valid());

        if (context.ExistingNode.Content is UpdatableContent existingContent &&
            context.Node.Content is UpdatableContent updatedContent)
        {
            if (updatedContent.Version < existingContent.Version)
            {
                return Observable.Return(new NodeValidationResult(
                    false,
                    $"Cannot downgrade version from {existingContent.Version} to {updatedContent.Version}",
                    NodeRejectionReason.ValidationFailed));
            }
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with update validators via NodeType configuration.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithUpdateValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UpdatableNodeType = "updatable";
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // NodeType definition lives in IStaticNodeProvider (see TestStateIsolation).
        builder.ConfigureServices(services => services
            .AddSingleton<IStaticNodeProvider, UpdatableTypeProvider>()
            .AddSingleton<INodeValidator, NoVersionDowngradeValidator>());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task UpdateNode_VersionUpgrade_ShouldSucceed()
    {
        // Arrange - create a node with version 1
        var node = new MeshNode("VersionedNode", "update/validation")
        {
            Name = "Versioned Node",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "Original", Version: 1)
        };
        await NodeFactory.CreateNode(node);

        // Act - update with version 2
        var updatedNode = node with
        {
            Name = "Updated Node",
            Content = new UpdatableContent(Title: "Updated", Version: 2)
        };
        var result = await NodeFactory.UpdateNode(updatedNode);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Updated Node");
    }

    [Fact]
    public async Task UpdateNode_VersionDowngrade_ShouldFail()
    {
        // Arrange - create a node with version 5
        var node = new MeshNode("HighVersionNode", "update/validation")
        {
            Name = "High Version Node",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "High Version", Version: 5)
        };
        await NodeFactory.CreateNode(node);

        // Act - try to downgrade to version 3
        var downgradedNode = node with
        {
            Name = "Downgraded Node",
            Content = new UpdatableContent(Title: "Downgraded", Version: 3)
        };
        var act = () => NodeFactory.UpdateNode(downgradedNode).ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*downgrade*");
    }

    [Fact]
    public async Task UpdateNode_SameVersion_ShouldSucceed()
    {
        // Arrange - create a node with version 1
        var node = new MeshNode("SameVersionNode", "update/validation")
        {
            Name = "Same Version Node",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "Original", Version: 1)
        };
        await NodeFactory.CreateNode(node);

        // Act - update with same version (just change title)
        var updatedNode = node with
        {
            Name = "Updated Same Version",
            Content = new UpdatableContent(Title: "Updated", Version: 1)
        };
        var result = await NodeFactory.UpdateNode(updatedNode);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateNode_NonExistentNode_ShouldFail()
    {
        // Arrange
        var node = new MeshNode("NonExistentNode", "update/validation")
        {
            Name = "Non Existent",
            NodeType = UpdatableNodeType,
            Content = new UpdatableContent(Title: "Ghost", Version: 1)
        };

        // Act - try to update a node that doesn't exist
        var act = () => NodeFactory.UpdateNode(node).ToTask();

        // Assert — routing returns "No node found for address ..." when no per-node hub
        // exists for the path; framework wraps as InvalidOperationException.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No node found*");
    }

}

/// <summary>
/// A global update validator that prevents changing the Name to contain "forbidden".
/// </summary>
public class ForbiddenNameUpdateValidator : INodeValidator
{
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (context.Node.Name?.Contains("forbidden", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Observable.Return(new NodeValidationResult(
                false,
                "Cannot update node name to contain 'forbidden'",
                NodeRejectionReason.ValidationFailed));
        }
        return Observable.Return(NodeValidationResult.Valid());
    }
}

/// <summary>
/// Test class with global update validator via DI.
/// </summary>
[Collection("NodeOperationsTests")]
public class NodeOperationsWithGlobalUpdateValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

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
        // Arrange - create a node (legal: NodeType + Content)
        var node = new MeshNode("NormalNode", "global/update")
        {
            Name = "Normal Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Normal" }
        };
        await NodeFactory.CreateNode(node);

        // Act - try to update with forbidden name
        var updatedNode = node with
        {
            Name = "This is forbidden by policy"
        };
        var act = () => NodeFactory.UpdateNode(updatedNode).ToTask();

        // Assert — validator rejection surfaces as UnauthorizedAccessException
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*forbidden*");
    }

    [Fact]
    public async Task UpdateNode_AllowedName_ShouldSucceed()
    {
        // Arrange - create a node (legal: NodeType + Content)
        var node = new MeshNode("AllowedUpdateNode", "global/update")
        {
            Name = "Allowed Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Allowed" }
        };
        await NodeFactory.CreateNode(node);

        // Act - update with allowed name
        var updatedNode = node with
        {
            Name = "Updated Allowed Node"
        };
        var result = await NodeFactory.UpdateNode(updatedNode);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Updated Allowed Node");
    }
}
