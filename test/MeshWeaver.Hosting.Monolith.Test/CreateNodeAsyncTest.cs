using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for IMeshCatalog.CreateNodeAsync with FileSystem persistence,
/// focusing on Comment node creation and reply linking.
/// </summary>
[Collection("SamplesGraphData")]
public class CreateNodeAsyncTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverCreateNodeTests",
        ".mesh-cache");

    private CancellationToken TestTimeout => new CancellationTokenSource(30.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNodeAsync_ShouldPersistCommentNode()
    {
        // Arrange
        var parentPath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing";
        var commentId = Guid.NewGuid().AsString();
        var commentPath = $"{parentPath}/{commentId}";

        var comment = new Comment
        {
            Id = commentId,
            PrimaryNodePath = parentPath,
            Author = "TestUser",
            Text = "This is a test comment",
            HighlightedText = "some selected text",
            MarkerId = Guid.NewGuid().AsString(),
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active
        };

        var node = new MeshNode(commentPath)
        {
            Name = $"Comment by TestUser",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        // Act
        var createdNode = await NodeFactory.CreateNodeAsync(node, "TestUser", TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.Path.Should().Be(commentPath);
        createdNode.State.Should().Be(MeshNodeState.Active);
        createdNode.NodeType.Should().Be(CommentNodeType.NodeType);

        var createdComment = createdNode.Content.Should().BeOfType<Comment>().Subject;
        createdComment.Id.Should().Be(commentId);
        createdComment.Author.Should().Be("TestUser");
        createdComment.Text.Should().Be("This is a test comment");
        createdComment.HighlightedText.Should().Be("some selected text");
        createdComment.Status.Should().Be(CommentStatus.Active);

        // Verify round-trip via GetNodeAsync
        var retrievedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{commentPath} scope:exact").FirstOrDefaultAsync();
        retrievedNode.Should().NotBeNull();
        retrievedNode!.Path.Should().Be(commentPath);
        var retrievedComment = retrievedNode.Content.Should().BeOfType<Comment>().Subject;
        retrievedComment.Text.Should().Be("This is a test comment");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(commentPath, recursive: false, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNodeAsync_ReplyNode_ShouldLinkToParent()
    {
        // Arrange
        var parentPath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing";

        // Create parent comment
        var parentCommentId = Guid.NewGuid().AsString();
        var parentCommentPath = $"{parentPath}/{parentCommentId}";
        var parentComment = new Comment
        {
            Id = parentCommentId,
            PrimaryNodePath = parentPath,
            Author = "Alice",
            Text = "Original comment",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active
        };
        var parentNode = new MeshNode(parentCommentPath)
        {
            Name = "Comment by Alice",
            NodeType = CommentNodeType.NodeType,
            Content = parentComment
        };
        await NodeFactory.CreateNodeAsync(parentNode, "Alice", TestTimeout);

        // Create reply as child of parent comment node (nested path)
        var replyId = Guid.NewGuid().AsString();
        var replyPath = $"{parentCommentPath}/{replyId}";
        var replyComment = new Comment
        {
            Id = replyId,
            PrimaryNodePath = parentCommentPath,
            Author = "Bob",
            Text = "This is a reply",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active
        };
        var replyNode = new MeshNode(replyPath)
        {
            Name = "Comment by Bob",
            NodeType = CommentNodeType.NodeType,
            Content = replyComment
        };

        // Act
        var createdReply = await NodeFactory.CreateNodeAsync(replyNode, "Bob", TestTimeout);

        // Assert
        createdReply.Should().NotBeNull();
        var replyContent = createdReply.Content.Should().BeOfType<Comment>().Subject;
        replyContent.Author.Should().Be("Bob");
        replyContent.Text.Should().Be("This is a reply");

        // Verify reply round-trips via path
        var retrievedReply = await MeshQuery.QueryAsync<MeshNode>($"path:{replyPath} scope:exact").FirstOrDefaultAsync();
        retrievedReply.Should().NotBeNull("Reply should be retrievable by path");
        retrievedReply!.Path.Should().StartWith(parentCommentPath);

        // Cleanup: delete reply first, then parent
        await NodeFactory.DeleteNodeAsync(replyPath, recursive: false, ct: TestTimeout);
        await NodeFactory.DeleteNodeAsync(parentCommentPath, recursive: false, ct: TestTimeout);
    }
}
