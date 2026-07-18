using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Regression coverage for the two comment issues:
/// <list type="bullet">
///   <item>#473 — a reply created as a direct-child Comment node is invisible: the Overview rendered
///     replies from the never-maintained <c>Comment.Replies</c> list. Fixed by discovering replies with
///     the same child-query pattern the page-level comment list uses, so write (CreateNode a child) and
///     read (query children) agree on ONE namespace.</item>
///   <item>#391 — a comment can be deleted end-to-end, and a comment's AUTHOR may delete their own
///     comment even without Update on the document (<see cref="SatelliteAccessRule"/> previously mapped
///     Comment Delete → Update on the MainNode, locking authors out).</item>
/// </list>
/// </summary>
[Collection("SamplesGraphData")]
public class CommentReplyDeleteViewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";
    private const string CommentNs = DocPath + "/" + CommentsExtensions.CommentPartition;

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(), "MeshWeaverCommentReplyDeleteViewTest", ".mesh-cache");

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

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // ── #473: reply visibility ───────────────────────────────────────────────

    /// <summary>
    /// A reply authored by user B on user A's comment must be visible to every reader. Pre-fix the parent
    /// comment's Overview read <c>Comment.Replies</c> (which the write path never populated), so the reply
    /// — a real direct-child Comment node — was never rendered. Post-fix the Overview discovers it via a
    /// child query and shows a "Replies (1)" section.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Reply_ByAnotherUser_IsVisibleInParentCommentOverview()
    {
        // User A (Alice) writes a top-level comment.
        var commentId = Guid.NewGuid().AsString();
        var commentPath = $"{CommentNs}/{commentId}";
        await NodeFactory.CreateNode(new MeshNode(commentId, CommentNs)
        {
            Name = "Comment by Alice",
            NodeType = CommentNodeType.NodeType,
            MainNode = DocPath,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                Author = "Alice",
                Text = "Original comment",
                Status = CommentStatus.Active
            }
        }).Should().Emit();

        // User B (Bob) replies — a direct-child Comment node, exactly what the ↩ reply form writes.
        var replyId = Guid.NewGuid().AsString();
        await NodeFactory.CreateNode(new MeshNode(replyId, commentPath)
        {
            Name = "Reply to Alice",
            NodeType = CommentNodeType.NodeType,
            MainNode = DocPath,
            Content = new Comment
            {
                Id = replyId,
                PrimaryNodePath = DocPath,
                Author = "Bob",
                Text = "Bob's answer to Alice",
                Status = CommentStatus.Active
            }
        }).Should().Emit();

        // Render the parent comment's Overview and confirm the reply is discovered.
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(commentPath), reference);

        await WaitForStoreToContain(stream, "Replies (1)",
            "the parent comment Overview must render a replies section once a child reply exists (#473)");
        Output.WriteLine("✅ Reply authored by Bob is visible in Alice's comment Overview.");
    }

    /// <summary>
    /// Zero-write repro against the shipped sample data: <c>Doc/DataMesh/CollaborativeEditing/_Comment/c1</c>
    /// has a direct-child reply <c>c1/reply1</c> authored by a different user, but its own
    /// <c>Comment.Replies</c> list is empty. Pre-fix that reply was invisible; post-fix the child query
    /// surfaces it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SampleComment_WithChildReply_ShowsRepliesSection()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address($"{CommentNs}/c1"), reference);

        await WaitForStoreToContain(stream, "Replies (1)",
            "sample comment c1 has a child reply1 on disk that must render even though c1.Replies is empty (#473)");
        Output.WriteLine("✅ Sample reply1 is discovered under c1.");
    }

    // ── #391: view after creation ────────────────────────────────────────────

    /// <summary>
    /// A written comment must be viewable/openable again: rendering its own Overview shows its text.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Comment_CanBeViewedAfterCreation()
    {
        var commentId = Guid.NewGuid().AsString();
        var commentPath = $"{CommentNs}/{commentId}";
        const string uniqueText = "A distinctive comment body to look for";
        await NodeFactory.CreateNode(new MeshNode(commentId, CommentNs)
        {
            Name = "Comment by Alice",
            NodeType = CommentNodeType.NodeType,
            MainNode = DocPath,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                Author = "Alice",
                Text = uniqueText,
                Status = CommentStatus.Active
            }
        }).Should().Emit();

        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(commentPath), reference);

        await WaitForStoreToContain(stream, uniqueText,
            "a created comment must be re-openable and show its text in the Overview (#391)");
        Output.WriteLine("✅ Comment is viewable after creation.");
    }

    // ── #391: delete ─────────────────────────────────────────────────────────

    /// <summary>
    /// A comment can be deleted end-to-end. The successful <c>DeleteNodeResponse</c> is the authoritative
    /// proof that the comment was removed through the access-controlled delete pipeline (#391) — the same
    /// assertion every delete test in the suite uses.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Comment_CanBeDeleted_EndToEnd()
    {
        var commentId = Guid.NewGuid().AsString();
        var commentPath = $"{CommentNs}/{commentId}";
        await NodeFactory.CreateNode(new MeshNode(commentId, CommentNs)
        {
            Name = "Comment by Alice",
            NodeType = CommentNodeType.NodeType,
            MainNode = DocPath,
            Content = new Comment { Id = commentId, PrimaryNodePath = DocPath, Author = "Alice", Text = "delete me" }
        }).Should().Within(45.Seconds()).Emit();

        await NodeFactory.DeleteNode(commentPath).Should().Within(45.Seconds()).Emit();
        Output.WriteLine("✅ Comment deleted end-to-end.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task WaitForStoreToContain(ISynchronizationStream<JsonElement> stream, string needle, string because)
        => (await stream
                .Where(item => item.Value.ValueKind != JsonValueKind.Undefined
                               && item.Value.GetRawText().Contains(needle))
                .Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull(because);
}
