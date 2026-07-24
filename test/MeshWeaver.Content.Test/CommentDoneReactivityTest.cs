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
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Deterministic repro for the "comment only appears after a page refresh" bug (UWDeepfield treaty
/// tabs, but the defect is the generic comment Overview): pressing <b>Done</b> in the comment editor
/// persists the text, yet the read-only view keeps rendering the text CAPTURED when the Overview was
/// last built — the freshly typed text only shows up after re-subscribing (a browser refresh).
///
/// <para>The flow is the EXACT client flow: render the comment's Overview area over a remote
/// layout-area stream, click ✎ (for the edit case; a fresh empty comment opens straight in the
/// editor), write the text through <see cref="LayoutClientExtensions.UpdatePointer"/> — the same
/// call the Blazor markdown editor makes — then click Done and require the read-only
/// <see cref="MarkdownControl"/> to show the NEW text on the SAME stream, no re-subscribe.</para>
/// </summary>
[Collection("SamplesGraphData")]
public class CommentDoneReactivityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(), "MeshWeaverCommentDoneReactivityTests", ".mesh-cache");

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
    /// EDIT flow: ✎ → editor → type → Done. The read-only view on the SAME stream must render the
    /// edited text — the user must not need a refresh to see their own edit.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task EditingComment_DoneShowsEditedText_WithoutRefresh()
    {
        var (stream, commentAddress, commentPath, reference) =
            await RenderCommentOverview("Original comment");

        var editArea = await FindArea(stream, reference.Area!,
            c => c is HtmlControl h && (h.Data?.ToString() ?? "").Contains("✎"));
        editArea.Should().NotBeNull("the comment Overview must render a ✎ Edit button for the author");
        stream.Hub.Post(new ClickedEvent(editArea!, stream.StreamId), o => o.WithTarget(commentAddress));

        await WaitForStoreToContain(stream, "Write your comment",
            "clicking ✎ Edit must open the comment editor");
        Output.WriteLine("Editor open — typing new text…");

        await TypeCommentText(stream, commentPath, "Edited comment text");
        await ClickDone(stream, commentAddress, reference);
        await WaitForPersistedText(commentPath, "Edited comment text");

        await WaitForReadOnlyMarkdown(stream, reference, "Edited comment text",
            "after Done the read-only view must show the text just saved — not the stale text captured at the last node emission (the 'only after refresh' bug)");
        Output.WriteLine("✅ Edited text rendered read-only without a refresh.");
    }

    /// <summary>
    /// ADD flow (the treaty-tab "+ Comment" path): a fresh EMPTY comment opens straight in the
    /// editor; after typing and Done the comment text must render — pre-fix the view falls back to
    /// the "No comment text" placeholder because it renders the empty text captured at creation.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task NewComment_DoneShowsTypedText_WithoutRefresh()
    {
        var (stream, commentAddress, commentPath, reference) =
            await RenderCommentOverview("");

        // A fresh (empty) comment opens straight in the editor — no ✎ click needed.
        await WaitForStoreToContain(stream, "Write your comment",
            "a fresh empty comment must open straight in the editor");
        Output.WriteLine("Editor open on the fresh comment — typing…");

        await TypeCommentText(stream, commentPath, "My brand-new comment");
        await ClickDone(stream, commentAddress, reference);
        await WaitForPersistedText(commentPath, "My brand-new comment");

        await WaitForReadOnlyMarkdown(stream, reference, "My brand-new comment",
            "after Done the freshly written comment must render — not the 'No comment text' placeholder (the 'only after refresh' bug)");
        Output.WriteLine("✅ New comment rendered read-only without a refresh.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Creates a Comment authored by the test user and renders its live Overview area.</summary>
    private async Task<(ISynchronizationStream<JsonElement> stream, Address commentAddress, string commentPath, LayoutAreaReference reference)>
        RenderCommentOverview(string initialText)
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
                Text = initialText,
                Status = CommentStatus.Active
            }
        };

        var created = await NodeFactory.CreateNode(commentNode).Should().Emit();
        var commentAddress = new Address(created.Path!);
        Output.WriteLine($"Created comment at {created.Path}");

        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(commentAddress, reference);

        await stream.GetControlStream(reference.Area!)
            .Should().Within(45.Seconds()).Match(c => c is StackControl);

        return (stream, commentAddress, created.Path!, reference);
    }

    /// <summary>
    /// Writes <paramref name="text"/> into the comment editor's bound data item the way the Blazor
    /// markdown editor does — a client-side pointer update on the layout-area stream — and waits for
    /// it to land in the synchronized store so the Done click's read sees it.
    /// </summary>
    private async Task TypeCommentText(ISynchronizationStream<JsonElement> stream, string commentPath, string text)
    {
        // Let the render/seed frames finish syncing before "typing": a pointer patch computed on a
        // client state that lags the host is dropped by the sync stream's version guard (a real
        // user's keystrokes span seconds, so their client has long caught up).
        await Task.Delay(1500);
        var textDataId = $"commentText_{commentPath.Replace("/", "_")}";
        stream.UpdatePointer(text, LayoutAreaReference.GetDataPointer(textDataId), new JsonPointerReference("text"));
        await WaitForStoreToContain(stream, text,
            "the typed text must synchronize into the layout-area store before Done reads it");
        // The store-contains check observes the client-side application of the patch; give the
        // round-trip to the host a beat so the Done click's host-side read sees the typed text.
        // (WaitForPersistedText after Done distinguishes a lost patch from the render-staleness bug.)
        await Task.Delay(750);
    }

    /// <summary>
    /// Verifies the Done write itself landed (the persistence half the user DOES see after a
    /// refresh) — separating "the patch/typed text never reached the host" from the actual bug,
    /// "persisted fine but the read-only view still shows the stale text".
    /// </summary>
    private async Task WaitForPersistedText(string commentPath, string expectedText)
        => (await Mesh.GetWorkspace().GetMeshNodeStream(commentPath)
                .Where(n => n?.ContentAs<Comment>(Mesh.JsonSerializerOptions)?.Text == expectedText)
                .Should().Within(20.Seconds()).Emit())
            .Should().NotBeNull($"the Done click must persist '{expectedText}'");

    /// <summary>Finds and clicks the editor's Done button.</summary>
    private async Task ClickDone(ISynchronizationStream<JsonElement> stream, Address commentAddress, LayoutAreaReference reference)
    {
        var doneArea = await FindArea(stream, reference.Area!,
            c => c is ButtonControl b && (b.Data?.ToString() ?? "") == "Done");
        doneArea.Should().NotBeNull("the comment editor must render a Done button");
        Output.WriteLine($"Clicking Done at area '{doneArea}'…");
        stream.Hub.Post(new ClickedEvent(doneArea!, stream.StreamId), o => o.WithTarget(commentAddress));
    }

    /// <summary>
    /// Reactively waits until the rendered control tree contains a read-only
    /// <see cref="MarkdownControl"/> showing <paramref name="expectedText"/> — the post-Done
    /// read-only rendering. Watching the CONTROL TREE (not the raw store text) matters: the typed
    /// text already sits in the editor's /data item, so a raw-store contains-check would pass even
    /// while the visible read-only view still shows the stale text.
    /// </summary>
    private async Task WaitForReadOnlyMarkdown(ISynchronizationStream<JsonElement> stream,
        LayoutAreaReference reference, string expectedText, string because)
    {
        string? found = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (found is null && DateTime.UtcNow < deadline)
        {
            found = await FindArea(stream, reference.Area!,
                c => c is MarkdownControl m && (m.Markdown?.ToString() ?? "").Contains(expectedText));
            if (found is null)
                await Task.Delay(1000);
        }
        found.Should().NotBeNull(because);
    }

    /// <summary>Reactively waits until the area's serialized store contains <paramref name="needle"/>.</summary>
    private async Task WaitForStoreToContain(ISynchronizationStream<JsonElement> stream, string needle, string because)
        => (await stream
                .Where(item => item.Value.ValueKind != JsonValueKind.Undefined
                               && item.Value.GetRawText().Contains(needle))
                .Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull(because);

    /// <summary>
    /// Depth-first searches the control tree under <paramref name="rootArea"/> for the first control
    /// matching <paramref name="predicate"/>, returning its area key. Descends StackControls.
    /// </summary>
    private async Task<string?> FindArea(ISynchronizationStream<JsonElement> stream, string rootArea,
        Func<UiControl?, bool> predicate)
    {
        var control = await TryGetControl(stream, rootArea);
        if (predicate(control))
            return rootArea;
        if (control is StackControl { Areas: { } areas })
        {
            foreach (var named in areas)
            {
                var childArea = named.Area?.ToString();
                if (string.IsNullOrEmpty(childArea))
                    continue;
                var found = await FindArea(stream, childArea, predicate);
                if (found != null)
                    return found;
            }
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
