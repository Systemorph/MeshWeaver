using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Deterministic repro for the inline-reply bug: clicking the ↩ Reply button on a comment's
/// Overview created a transient reply node but NEVER showed an editor, so the user could not
/// write the reply.
///
/// Root cause (proven by these tests, NOT the earlier "nested-area click-path mismatch"
/// hypothesis): <c>CommentLayoutAreas.BuildReplyCreateForm</c> was defined but never wired into
/// <c>BuildOverview</c>; the <c>replyPath_*</c> data item the ↩ click sets was consumed nowhere.
///
/// <para><see cref="ClickingEdit_ShowsCommentEditor"/> is the routing baseline: the ✎ Edit
/// button uses the IDENTICAL <c>Controls.Html(...).WithClickAction(UpdateData) + GetDataStream</c>
/// toggle and IS wired, so its editor appearing proves the <c>ClickedEvent</c> routes correctly to
/// the comment hub. <see cref="ClickingReply_ShowsReplyCreateForm"/> then isolates the defect:
/// same routing, but no reply editor — because the form was orphaned.</para>
/// </summary>
[Collection("SamplesGraphData")]
public class CommentReplyFormTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(), "MeshWeaverCommentReplyFormTests", ".mesh-cache");

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

    /// <summary>
    /// Routing baseline. Clicking ✎ Edit must surface the comment editor ("Write your comment...").
    /// The ✎ toggle is wired the SAME way the ↩ reply toggle SHOULD be, so this passing proves the
    /// ClickedEvent reaches the comment hub and the GetDataStream toggle re-renders — i.e. routing is
    /// NOT the bug. (Author is "Roland" = the test user, satisfying CommentNodeType.AuthorEditOnly.)
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ClickingEdit_ShowsCommentEditor()
    {
        var (stream, commentAddress, reference) = await RenderCommentOverview();

        var editArea = await FindHtmlArea(stream, reference.Area!, "✎");
        editArea.Should().NotBeNull("the comment Overview must render a ✎ Edit button for the author");
        Output.WriteLine($"Found ✎ Edit button at area '{editArea}'. Clicking…");

        stream.Hub.Post(new ClickedEvent(editArea!, stream.StreamId), o => o.WithTarget(commentAddress));

        await WaitForStoreToContain(stream, "Write your comment",
            "clicking ✎ Edit must show the comment editor — proves ClickedEvent routing works on the comment hub");
        Output.WriteLine("✅ Comment editor appeared — click routing confirmed.");
    }

    /// <summary>
    /// The repro. Clicking ↩ Reply must surface the reply editor ("Write your reply..."). Pre-fix
    /// this times out: BuildReplyCreateForm was never wired into BuildOverview, so the transient reply
    /// node is created but no editor renders. Post-fix the form appears off the replyPath_* data item.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ClickingReply_ShowsReplyCreateForm()
    {
        var (stream, commentAddress, reference) = await RenderCommentOverview();

        var replyArea = await FindHtmlArea(stream, reference.Area!, "↩");
        replyArea.Should().NotBeNull("the comment Overview must render a ↩ Reply button for a logged-in user");
        Output.WriteLine($"Found ↩ Reply button at area '{replyArea}'. Clicking…");

        stream.Hub.Post(new ClickedEvent(replyArea!, stream.StreamId), o => o.WithTarget(commentAddress));

        await WaitForStoreToContain(stream, "Write your reply",
            "clicking ↩ Reply must show the inline reply editor (BuildReplyCreateForm wired into the Overview)");
        Output.WriteLine("✅ Reply create-form appeared.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a top-level Comment authored by the test user under the CollaborativeEditing doc and
    /// returns its live Overview area stream (already rendered to a StackControl root).
    /// </summary>
    private async Task<(ISynchronizationStream<JsonElement> stream, Address commentAddress, LayoutAreaReference reference)>
        RenderCommentOverview()
    {
        var commentId = Guid.NewGuid().AsString();
        var commentNode = new MeshNode(commentId, DocPath)
        {
            Name = "Comment by Roland",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                Author = TestUsers.Admin.Name,   // "Roland" — the auto-logged-in test user
                Text = "Original comment",
                Status = CommentStatus.Active
            }
        };

        var created = await NodeFactory.CreateNode(commentNode).Should().Emit();
        var commentAddress = new Address(created.Path!);
        Output.WriteLine($"Created comment at {created.Path}");

        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(commentAddress, reference);

        // Block until the Overview's root StackControl renders, so the action buttons exist before we walk.
        await stream.GetControlStream(reference.Area!)
            .Should().Within(45.Seconds()).Match(c => c is StackControl);

        return (stream, commentAddress, reference);
    }

    /// <summary>
    /// Reactively waits until the area's serialized store contains <paramref name="needle"/>.
    /// </summary>
    private async Task WaitForStoreToContain(ISynchronizationStream<JsonElement> stream, string needle, string because)
        => (await stream
                .Where(item => item.Value.ValueKind != JsonValueKind.Undefined
                               && item.Value.GetRawText().Contains(needle))
                .Should().Within(15.Seconds()).Emit())
            .Should().NotBeNull(because);

    /// <summary>
    /// Depth-first searches the control tree under <paramref name="rootArea"/> for the first
    /// <see cref="HtmlControl"/> whose markup contains <paramref name="needle"/>, returning its area key
    /// (the key a client posts in a <see cref="ClickedEvent"/>). Descends StackControls.
    /// </summary>
    private async Task<string?> FindHtmlArea(ISynchronizationStream<JsonElement> stream, string rootArea, string needle)
    {
        var control = await TryGetControl(stream, rootArea);
        switch (control)
        {
            case HtmlControl html when (html.Data?.ToString() ?? "").Contains(needle):
                return rootArea;
            case StackControl { Areas: { } areas }:
                foreach (var named in areas)
                {
                    var childArea = named.Area?.ToString();
                    if (string.IsNullOrEmpty(childArea))
                        continue;
                    var found = await FindHtmlArea(stream, childArea, needle);
                    if (found != null)
                        return found;
                }
                break;
        }
        return null;
    }

    /// <summary>Reads the control at <paramref name="area"/>, or null if none materializes quickly.</summary>
    private static async Task<UiControl?> TryGetControl(ISynchronizationStream<JsonElement> stream, string area)
    {
        try
        {
            return await stream.GetControlStream(area).Should().Within(5.Seconds()).Match(c => c != null);
        }
        catch (AssertionException)
        {
            return null;
        }
    }
}
