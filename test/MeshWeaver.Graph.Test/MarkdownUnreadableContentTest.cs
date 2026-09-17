using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>"THERE IS NO CONTENT" AND "I CANNOT READ THE CONTENT" ARE DIFFERENT ANSWERS</b> —
/// Systemorph/MeshWeaver#4600.
///
/// <para><c>GetMarkdownContent</c> returned <see cref="string.Empty"/> from its final fall-through
/// for every payload shape it did not recognise, and the caller could not tell the two apart because
/// they were the same value. A Markdown node whose v1 content was
/// <c>{"markdown": "# … Company Profile\n…"}</c> — a member <see cref="MarkdownContent"/> does not
/// declare — therefore rendered as <i>"No content yet. Use the menu to start editing."</i> over a
/// full document, with nothing logged and nothing to grep (measured on a customer workspace,
/// 2026-09-17).</para>
///
/// <para>🚨 <b>The placeholder is an INVITATION, and that is what makes this data loss rather than a
/// cosmetic bug.</b> A reader who believes it starts editing, and the first save replaces the bytes
/// that were still in the store. So the unreadable state must not render the state that invites the
/// edit — which is the assertion below, not merely "a different string".</para>
///
/// <para>The node is built IN MEMORY and handed straight to the view. Deliberate: a guard on the
/// write boundary is tracked separately (#4601), and a test that created this node through
/// <c>CreateNode</c> would be testing that guard instead of the renderer — and would start failing
/// the moment it landed. The renderer's contract does not depend on it either way: the store holds
/// rows written before any guard existed, and an import, a migration or a restore can put one back
/// at any time.</para>
/// </summary>
public class MarkdownUnreadableContentTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string UnreadableView = nameof(UnreadableView);
    private const string EmptyView = nameof(EmptyView);
    private const string PresentView = nameof(PresentView);

    /// <summary>The v1 payload of the node the issue was measured on.</summary>
    private const string MeasuredPayload = """{"markdown":"# Company Profile\n\nA whole document."}""";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(UnreadableView, UnreadableContentView)
                .WithView(EmptyView, EmptyContentView)
                .WithView(PresentView, PresentContentView));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// 🚨 <c>using</c> + <c>Clone()</c>: <see cref="JsonDocument"/> owns pooled memory, and the
    /// clone is what makes the element independent of it.
    /// </summary>
    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static UiControl UnreadableContentView(LayoutAreaHost host, RenderingContext ctx) =>
        MarkdownOverviewLayoutArea.BuildMarkdownReadView(
            host, "test/unreadable/CompanyProfile",
            MarkdownOverviewLayoutArea.ReadMarkdownContent(new MeshNode("CompanyProfile", "test/unreadable")
            {
                Name = "Company Profile",
                NodeType = MeshWeaver.Graph.Configuration.MarkdownNodeType.NodeType,
                Content = Json(MeasuredPayload),
            }),
            canComment: false, canEdit: true, hideAnnotations: false);

    private static UiControl EmptyContentView(LayoutAreaHost host, RenderingContext ctx) =>
        MarkdownOverviewLayoutArea.BuildMarkdownReadView(
            host, "test/empty/Blank",
            MarkdownOverviewLayoutArea.ReadMarkdownContent(new MeshNode("Blank", "test/empty")
            {
                Name = "Blank",
                NodeType = MeshWeaver.Graph.Configuration.MarkdownNodeType.NodeType,
            }),
            canComment: false, canEdit: true, hideAnnotations: false);

    private static UiControl PresentContentView(LayoutAreaHost host, RenderingContext ctx) =>
        MarkdownOverviewLayoutArea.BuildMarkdownReadView(
            host, "test/present/Page",
            MarkdownOverviewLayoutArea.ReadMarkdownContent(new MeshNode("Page", "test/present")
            {
                Name = "Page",
                NodeType = MeshWeaver.Graph.Configuration.MarkdownNodeType.NodeType,
                Content = Json("""{"$type":"MarkdownContent","content":"# Readable\n\nBody."}"""),
            }),
            canComment: false, canEdit: true, hideAnnotations: false);

    private async Task<UiControl> RenderAsync(string area)
    {
        var reference = new LayoutAreaReference(area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        return (await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null,
                cancellationToken: TestContext.Current.CancellationToken))!;
    }

    [HubFact]
    public async Task UnreadableContent_DoesNotRenderTheInvitationToEdit()
    {
        var control = await RenderAsync(UnreadableView);
        var text = Text(control);

        text.Should().NotContain("No content yet",
            "the empty-state copy is an INVITATION — rendering it over a document whose bytes are "
            + "still in the store is how the reader is led to overwrite them (#4600)");
        text.Should().NotContain("Click Edit to add",
            "…and neither may any other wording that tells the reader to start typing here");
        text.Should().Contain("cannot be displayed",
            "the unreadable state must SAY that something is there and unreadable");
        text.Should().Contain("markdown",
            "…and name the member(s) actually stored, so whoever repairs the node knows what it "
            + "holds without opening the database");
    }

    [HubFact]
    public async Task EmptyContent_StillRendersTheInvitationToEdit()
    {
        var control = await RenderAsync(EmptyView);
        var text = Text(control);

        text.Should().Contain("No content yet",
            "a genuinely empty node is the case the authoring placeholder exists for, and the fix "
            + "must not take it away");
        text.Should().NotContain("cannot be displayed");
    }

    [HubFact]
    public async Task ReadableContent_StillRendersTheMarkdownBody()
    {
        var control = await RenderAsync(PresentView);

        control.Should().BeOfType<CollaborativeMarkdownControl>(
            "the body control is what consumers locate by type — it must not move behind a wrapper");
    }

    /// <summary>
    /// The classification itself, asserted directly: the three states, and the two shapes that
    /// MUST stay Absent because calling them unreadable would be false.
    /// </summary>
    [Fact]
    public void ReadMarkdownContent_SeparatesAbsentFromUnreadable()
    {
        Read(null).State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);
        Read(Node(content: null)).State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);

        var typed = Read(Node(new MarkdownContent { Content = "# hi" }));
        typed.State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Present);
        typed.Text.Should().Be("# hi");
        Read(Node(Json("""{"$type":"MarkdownContent","content":"# hi"}""")))
            .State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Present);
        Read(Node(Json("""{"content":"# hi"}""")))
            .State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Present,
                "the discriminator-less `content` fallback already shipped and must keep working");

        var measured = Read(Node(Json(MeasuredPayload)));
        measured.State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable);
        measured.Shape.Should().Be("markdown", "the shape is the whole lead for a repair");

        // 🚨 The same defect one level in (review on #4626): the declaration's OWN member is
        // present, and holds something that is not text. Mapping it to Absent would put the
        // invitation straight back over a payload that is there and unreadable.
        var nonString = Read(Node(Json("""{"$type":"MarkdownContent","content":{"blocks":[]}}""")));
        nonString.State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable);
        nonString.Shape.Should().Be("content: object");
        Read(Node(Json("""{"content":["a","b"]}""")))
            .State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable,
                "the discriminator-less fallback must judge the value the same way");

        // …but an EXPLICIT null or empty string under that member is an answer about emptiness.
        Read(Node(Json("""{"$type":"MarkdownContent","content":null}""")))
            .State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);
        Read(Node(Json("""{"$type":"MarkdownContent","content":""}""")))
            .State.Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);

        // An object with nothing authored in it IS an empty node — not an unreadable one.
        Read(Node(Json("{}"))).State
            .Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);
        Read(Node(Json("""{"$type":"MarkdownContent"}"""))).State
            .Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);

        // 🚨 Typed CLR content of some OTHER shape stays Absent. GetMarkdownContent is called from
        // the version diff and the notebook view for nodes that are not markdown at all, and
        // calling a node's own, perfectly readable typed content "unreadable" would be false.
        Read(Node(new NotMarkdown("hello"))).State
            .Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Absent);

        // The legacy accessor keeps its contract: empty for everything but Present.
        MarkdownOverviewLayoutArea.GetMarkdownContent(Node(Json(MeasuredPayload)))
            .Should().BeEmpty("the string-returning accessor cannot express Unreadable, which is "
                + "exactly why a caller that tells the USER the node is empty must not use it");
    }

    private static MarkdownOverviewLayoutArea.MarkdownRead Read(MeshNode? node) =>
        MarkdownOverviewLayoutArea.ReadMarkdownContent(node);

    private static MeshNode Node(object? content) =>
        new("Probe", "test/read") { Name = "Probe", Content = content };

    private string Text(UiControl control) =>
        JsonSerializer.Serialize(control, GetHost().JsonSerializerOptions);

    /// <summary>Typed content that is not a markdown shape — a node this area never owns.</summary>
    public record NotMarkdown(string Text);
}
