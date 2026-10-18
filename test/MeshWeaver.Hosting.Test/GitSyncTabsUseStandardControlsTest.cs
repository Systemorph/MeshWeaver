using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The GitSync settings tabs — GitHub Sync, GitHub Issues, Code workspace — are composed from the
/// platform's controls (maintainer, 2026-10-18: hand-built HTML replaced fleet-wide): hints are
/// <see cref="Controls.Hint"/> labels, headings are H3 labels, code and emphasis are Markdown, and
/// every click outcome is an <see cref="ActionNotice"/> rendered as a <see cref="MessageBarControl"/>.
///
/// <para>Read end to end, the way a person and an MCP <c>get @{space}/area/Settings/{tab}</c> read
/// it: the Settings area on a booted mesh, as the Space's admin, through the layout client. Each tab
/// is judged once it has rendered its own content, and the interactive parts are driven: a blank and
/// then a real "Add sync source", "Sync issues from GitHub", and the OAuth callback's error banner.</para>
/// </summary>
public class GitSyncTabsUseStandardControlsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Person = "gitsync-tabs-person";
    private const string Space = "GitSyncTabsSpace";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services => services.AddGitHubSyncServices())
            .ConfigureDefaultNodeHub(config => config
                .AddGitHubSyncSettingsTab()
                .AddGitHubIssuesTab()
                .AddWorkingTreeTab())
            .AddMeshNodes(
                new MeshNode(Person)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "GitSync Tabs Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "GitSync Tabs Person", Email = $"{Person}@meshweaver.io" },
                },
                new MeshNode(Space) { Name = "GitSync Tabs Space", NodeType = "Space", Content = new Space() },
                AssignmentNodeFactory.UserRole(Person, "Admin", Space));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private ISynchronizationStream<JsonElement> Open(string tabId)
    {
        TestUsers.DevLogin(Mesh, new AccessContext
            { ObjectId = Person, Name = "GitSync Tabs Person", Email = $"{Person}@meshweaver.io" });
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(Space),
                new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = tabId });
    }

    private static IEnumerable<(string Id, JsonElement Control)> Areas(JsonElement frame)
        => frame.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Object
            ? areas.EnumerateObject()
                .Where(a => a.Value.ValueKind == JsonValueKind.Object)
                .Select(a => (JsonSerializer.Deserialize<string>(a.Name) ?? a.Name, a.Value))
            : [];

    private static string? TypeOf(JsonElement control)
        => control.TryGetProperty("$type", out var t) ? t.GetString() : null;

    private static string? Text(JsonElement control, string property = "data")
        => control.TryGetProperty(property, out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    private static string[] HtmlAreas(JsonElement frame)
        => [.. Areas(frame).Where(a => TypeOf(a.Control) == nameof(HtmlControl)).Select(a => a.Id)];

    private static (string? Intent, string? Body)[] Notices(JsonElement frame)
        => [.. Areas(frame).Where(a => TypeOf(a.Control) == nameof(MessageBarControl))
            .Select(a => (Text(a.Control, "intent"), Text(a.Control, "body")))];

    private static bool HasLabel(JsonElement frame, string text)
        => Areas(frame).Any(a => TypeOf(a.Control) == nameof(LabelControl) && Text(a.Control) == text);

    /// <summary>A <see cref="Controls.Hint"/>: a label in the theme's hint colour.</summary>
    private static bool HasHint(JsonElement frame, string text)
        => Areas(frame).Any(a => TypeOf(a.Control) == nameof(LabelControl) && Text(a.Control) == text
            && Text(a.Control, "color") == Controls.HintColor);

    private static bool HasMarkdown(JsonElement frame, string fragment)
        => Areas(frame).Any(a => TypeOf(a.Control) == nameof(MarkdownControl)
            && (Text(a.Control, "markdown") ?? "").Contains(fragment, StringComparison.Ordinal));

    private static string? ButtonArea(JsonElement frame, string title)
        => Areas(frame).FirstOrDefault(a => TypeOf(a.Control) == nameof(ButtonControl) && Text(a.Control) == title).Id;

    private static void Click(ISynchronizationStream<JsonElement> stream, string area)
        => stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId), actingUser: null, onRefused: null, onAccepted: null);

    [Fact(Timeout = 120000)]
    public async Task GitHubSync_RendersStandardControls_AndAddSourceOutcomesAreMessageBars()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = Open(GitHubSyncSettingsTab.TabId);

        var initial = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => HasLabel(f, "Additional sync sources")
                    && HasHint(f, "No additional sync sources.")
                    && HasMarkdown(f, "`GitHub:OAuth:ClientId`")
                    && ButtonArea(f, "Add sync source") is not null,
                "the GitHub Sync tab renders its sections, hints and forms", ct);

        HtmlAreas(initial).Should().BeEmpty("the GitHub Sync tab holds no hand-built markup");
        HasLabel(initial, "Your GitHub account").Should().BeTrue("the section headings are labels");
        Notices(initial).Should().BeEmpty("no outcome is shown before a click");

        // Blank name → the Error banner.
        var add = ButtonArea(initial, "Add sync source")!;
        Click(stream, add);
        var refused = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => Notices(f).Contains((MessageBarIntent.Error, "Enter a name for the new sync source.")),
                "a blank source name shows the Error banner", ct);
        HtmlAreas(refused).Should().BeEmpty();

        // A name → the Success banner, and the new source is listed under its name.
        stream.UpdatePointer(JsonValue.Create("mirror"),
            LayoutAreaReference.GetDataPointer("ghAddSourceForm"), new JsonPointerReference("name"));
        Click(stream, add);
        var added = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => Notices(f).Any(n => n.Intent == MessageBarIntent.Success
                        && (n.Body ?? "").StartsWith("Sync source 'mirror' added", StringComparison.Ordinal))
                    && Areas(f).Any(a => TypeOf(a.Control) == nameof(LabelControl)
                        && (Text(a.Control) ?? "").Contains("mirror", StringComparison.OrdinalIgnoreCase)
                        && !HasHint(f, "No additional sync sources.")),
                "adding a source shows the Success banner and lists the source", ct);
        HtmlAreas(added).Should().BeEmpty();
        Notices(added).Should().NotContain(n => n.Intent == MessageBarIntent.Error,
            "the Success banner replaces the earlier error — one slot, the latest outcome");
    }

    [Fact(Timeout = 120000)]
    public async Task GitHubSync_OAuthCallbackError_IsAnErrorBanner()
    {
        var frame = await Open($"{GitHubSyncSettingsTab.TabId}?connect=github-error&reason=access_denied")
            .Select(c => c.Value).Should().Within(Budget)
            .Match(f => Notices(f).Contains((MessageBarIntent.Error, "GitHub connect failed: access_denied")),
                "the callback's error is surfaced as an Error banner", TestContext.Current.CancellationToken);

        HtmlAreas(frame).Should().BeEmpty();
    }

    [Fact(Timeout = 120000)]
    public async Task GitHubIssues_RendersStandardControls_AndSyncReportsInABanner()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = Open(GitHubIssuesTab.TabId);

        var initial = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => HasMarkdown(f, "**GitHub Sync**") && ButtonArea(f, "Sync issues from GitHub") is not null,
                "the GitHub Issues tab renders its explanation and actions", ct);
        HtmlAreas(initial).Should().BeEmpty("the GitHub Issues tab holds no hand-built markup");
        HasLabel(initial, "Issues").Should().BeTrue();
        HasLabel(initial, "Pull requests").Should().BeTrue();

        // No repository configured: the sync starts (Info) and then fails (Error) — both banners.
        Click(stream, ButtonArea(initial, "Sync issues from GitHub")!);
        var reported = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => Notices(f).Any(n => n.Intent is MessageBarIntent.Info or MessageBarIntent.Error
                    && !string.IsNullOrEmpty(n.Body)),
                "the click reports its outcome in a message bar", ct);
        HtmlAreas(reported).Should().BeEmpty();
    }

    [Fact(Timeout = 120000)]
    public async Task CodeWorkspace_WithoutARepository_SaysSoInAHint()
    {
        const string explanation = "Check out this Space's connected GitHub repository as a working tree, edit a file, and " +
                                   "commit + push as yourself. The same checkout is what the AI assistants operate on.";
        var frame = await Open(WorkingTreeTab.TabId).Select(c => c.Value).Should().Within(Budget)
            .Match(f => HasHint(f, "Connect a GitHub repository in the GitHub Sync tab first.") && HasHint(f, explanation),
                "the Code workspace tab renders its explanation and its no-repository hint, both as hint labels",
                TestContext.Current.CancellationToken);

        HtmlAreas(frame).Should().BeEmpty("the Code workspace tab holds no hand-built markup");
    }

    /// <summary>The working-tree status: a clean tree is a hint, a dirty one a grid of its changes.</summary>
    [Fact]
    public void CodeWorkspace_Status_IsAHintOrAGrid()
    {
        WorkingTreeTab.StatusView(new WorkingTreeStatus("main", true, []))
            .Should().BeOfType<LabelControl>().Which.Color.Should().Be(Controls.HintColor);
        WorkingTreeTab.StatusView(new WorkingTreeStatus("main", false, [new GitFileChange("a.cs", "M")]))
            .Should().BeOfType<StackControl>();
    }
}
