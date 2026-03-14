using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Documentation;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Integration tests for the commenting and reply workflow using ObserveQuery.
/// All verifications go through IMeshService.ObserveQuery — no low-level hub internals.
/// </summary>
[Collection("SamplesGraphData")]
public class CollaborativeEditingReplyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";
    private const string CommentPartition = "_Comment";
    private const string CommentC1Path = DocPath + "/" + CommentPartition + "/c1";
    private const string Reply1Path = CommentC1Path + "/reply1";

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverReplyTests",
        ".mesh-cache");

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
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddMeshWeaverDocs()
            .AddDoc()
            .AddDocumentation()
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

    /// <summary>
    /// Observes a query until at least one matching item appears.
    /// </summary>
    private async Task<MeshNode> ObserveFirst(string query)
    {
        return await MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .SelectMany(c => c.Items)
            .Timeout(15.Seconds())
            .FirstAsync();
    }

    /// <summary>
    /// Observes a query and returns the initial result set.
    /// </summary>
    private async Task<IReadOnlyList<MeshNode>> ObserveAll(string query)
    {
        var change = await MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Timeout(15.Seconds())
            .FirstAsync();
        return change.Items;
    }

    /// <summary>
    /// Verifies all 6 sample comments (c1-c6) are queryable via ObserveQuery.
    /// </summary>
    [Fact]
    public async Task SampleComments_AllSixLoadable()
    {
        var commentIds = new[] { "c1", "c2", "c3", "c4", "c5", "c6" };
        foreach (var id in commentIds)
        {
            var node = await ObserveFirst($"path:{DocPath}/{CommentPartition}/{id}");
            node.Should().NotBeNull($"Comment {id} should be queryable");
            var content = node.Content.Should().BeOfType<Comment>().Subject;
            content.Author.Should().NotBeNullOrEmpty($"Comment {id} should have an author");
            Output.WriteLine($"  {id}: by {content.Author}");
        }
    }

    /// <summary>
    /// Verifies the sample reply (reply1 under c1) is queryable with correct path and content.
    /// </summary>
    [Fact]
    public async Task SampleReply_LoadableByPath()
    {
        var reply = await ObserveFirst($"path:{Reply1Path}");

        reply.Id.Should().Be("reply1");
        reply.Path.Should().Be(Reply1Path);
        reply.Namespace.Should().Be(CommentC1Path);

        var content = reply.Content.Should().BeOfType<Comment>().Subject;
        content.Author.Should().NotBeNullOrEmpty();
        content.PrimaryNodePath.Should().Be(DocPath,
            "Reply's PrimaryNodePath should point to the document, not the parent comment");
        Output.WriteLine($"reply1: Author={content.Author}, Text={content.Text}");
    }

    /// <summary>
    /// Creates a reply under parent comment c1 (matching BuildReplyButton code path),
    /// then verifies it via ObserveQuery.
    /// </summary>
    [Fact]
    public async Task CreateReply_UnderParentComment()
    {
        // Verify parent comment exists
        var parent = await ObserveFirst($"path:{CommentC1Path}");
        var parentContent = (Comment)parent.Content!;
        Output.WriteLine($"Parent: '{parentContent.Text}' by {parentContent.Author}");

        // Create reply under parent comment — same path structure as BuildReplyButton
        var replyId = Guid.NewGuid().AsString();
        var replyNode = new MeshNode(replyId, CommentC1Path)
        {
            Name = $"Reply to {parentContent.Author}",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = replyId,
                PrimaryNodePath = DocPath,
                Author = "TestReviewer",
                Text = "Great suggestion!",
                CreatedAt = DateTimeOffset.UtcNow,
                Status = CommentStatus.Active
            }
        };

        var created = await NodeFactory.CreateNodeAsync(replyNode);
        created.Path.Should().Be($"{CommentC1Path}/{replyId}");

        try
        {
            // Verify reply is queryable via ObserveQuery
            var observed = await ObserveFirst($"path:{created.Path}");
            observed.Id.Should().Be(replyId);
            var content = (Comment)observed.Content!;
            content.Author.Should().Be("TestReviewer");
            content.Text.Should().Be("Great suggestion!");
            content.PrimaryNodePath.Should().Be(DocPath);
        }
        finally
        {
            await NodeFactory.DeleteNodeAsync(created.Path!);
        }
    }

    /// <summary>
    /// Creates multiple replies under c2 and verifies all are individually queryable.
    /// </summary>
    [Fact]
    public async Task MultipleReplies_AllQueryable()
    {
        var commentC2Path = $"{DocPath}/{CommentPartition}/c2";
        var parent = await ObserveFirst($"path:{commentC2Path}");

        var replyPaths = new List<string>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var replyId = Guid.NewGuid().AsString();
                var replyNode = new MeshNode(replyId, commentC2Path)
                {
                    Name = "Reply",
                    NodeType = CommentNodeType.NodeType,
                    Content = new Comment
                    {
                        Id = replyId,
                        PrimaryNodePath = DocPath,
                        Author = $"Reviewer{i + 1}",
                        Text = $"Reply {i + 1}",
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i),
                        Status = CommentStatus.Active
                    }
                };
                var created = await NodeFactory.CreateNodeAsync(replyNode);
                replyPaths.Add(created.Path!);
            }

            // Verify each reply is queryable
            for (var i = 0; i < replyPaths.Count; i++)
            {
                var node = await ObserveFirst($"path:{replyPaths[i]}");
                var content = (Comment)node.Content!;
                content.Author.Should().Be($"Reviewer{i + 1}");
                Output.WriteLine($"  Reply {i + 1}: {node.Path}");
            }
        }
        finally
        {
            foreach (var path in replyPaths)
                await NodeFactory.DeleteNodeAsync(path);
        }
    }

    /// <summary>
    /// Full workflow: create comment → create reply → update reply text → verify via ObserveQuery.
    /// </summary>
    [Fact]
    public async Task FullWorkflow_Comment_Reply_Edit()
    {
        // 1. Create comment
        var commentId = Guid.NewGuid().AsString();
        var commentNode = new MeshNode(commentId, $"{DocPath}/{CommentPartition}")
        {
            Name = "Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                MarkerId = commentId,
                HighlightedText = "some highlighted text",
                Author = "TestAuthor",
                Text = "This is a test comment",
                CreatedAt = DateTimeOffset.UtcNow,
                Status = CommentStatus.Active
            }
        };
        var createdComment = await NodeFactory.CreateNodeAsync(commentNode);
        var commentPath = createdComment.Path!;

        try
        {
            // Verify comment queryable
            var observed = await ObserveFirst($"path:{commentPath}");
            ((Comment)observed.Content!).Author.Should().Be("TestAuthor");

            // 2. Create reply under comment
            var replyId = Guid.NewGuid().AsString();
            var replyNode = new MeshNode(replyId, commentPath)
            {
                Name = "Reply",
                NodeType = CommentNodeType.NodeType,
                Content = new Comment
                {
                    Id = replyId,
                    PrimaryNodePath = DocPath,
                    Author = "TestReplier",
                    Text = "",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Status = CommentStatus.Active
                }
            };
            var createdReply = await NodeFactory.CreateNodeAsync(replyNode);
            createdReply.Path.Should().Be($"{commentPath}/{replyId}");

            // 3. Update reply text
            var updatedReply = createdReply with
            {
                State = MeshNodeState.Active,
                Content = ((Comment)createdReply.Content!) with { Text = "I agree!" }
            };
            await NodeFactory.UpdateNodeAsync(updatedReply);

            // 4. Verify updated text via ObserveQuery
            var finalReply = await MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{createdReply.Path}"))
                .SelectMany(c => c.Items)
                .Where(n => ((Comment)n.Content!).Text == "I agree!")
                .Timeout(15.Seconds())
                .FirstAsync();

            ((Comment)finalReply.Content!).Author.Should().Be("TestReplier");

            await NodeFactory.DeleteNodeAsync(createdReply.Path!);
        }
        finally
        {
            await NodeFactory.DeleteNodeAsync(commentPath);
        }
    }

    /// <summary>
    /// Creates a comment, resolves it, and verifies the status change via ObserveQuery.
    /// Also verifies IsTopLevelComment detection.
    /// </summary>
    [Fact]
    public async Task ResolveComment_StatusUpdated()
    {
        var commentId = Guid.NewGuid().AsString();
        var commentPath = $"{DocPath}/{CommentPartition}/{commentId}";
        var commentNode = new MeshNode(commentId, $"{DocPath}/{CommentPartition}")
        {
            Name = "Comment to resolve",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                MarkerId = commentId,
                Author = "TestAuthor",
                Text = "This will be resolved",
                Status = CommentStatus.Active
            }
        };
        var created = await NodeFactory.CreateNodeAsync(commentNode);

        try
        {
            // Verify IsTopLevelComment detection
            var content = (Comment)created.Content!;
            CommentLayoutAreas.IsTopLevelComment(commentPath, content).Should().BeTrue(
                "Comment in _Comment partition should be detected as top-level");

            // Resolve the comment
            var resolved = created with
            {
                Content = content with { Status = CommentStatus.Resolved }
            };
            await NodeFactory.UpdateNodeAsync(resolved);

            // Verify resolved status via ObserveQuery
            var updated = await MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{commentPath}"))
                .SelectMany(c => c.Items)
                .Where(n => ((Comment)n.Content!).Status == CommentStatus.Resolved)
                .Timeout(15.Seconds())
                .FirstAsync();

            ((Comment)updated.Content!).Status.Should().Be(CommentStatus.Resolved);
        }
        finally
        {
            await NodeFactory.DeleteNodeAsync(commentPath);
        }
    }
}
