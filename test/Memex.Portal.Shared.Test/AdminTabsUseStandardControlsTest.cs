using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Memex.Portal.Shared.Test.SettingsUseStandardControlsTest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The Admin app's Invitations and Inbox tabs are composed from the platform's controls (maintainer,
/// 2026-10-18: hand-built HTML replaced fleet-wide). Read end to end: the admin-gated areas
/// (<see cref="PlatformSettingsTabAreas.InvitationsArea"/>, <see cref="PlatformSettingsTabAreas.InboxArea"/>)
/// rendered on a booted mesh as a platform admin, through the layout client. A frame is judged once
/// it has SETTLED on the tab's content, and the Invite form is driven like a person drives it: a
/// blank submit, then a real one — each outcome must arrive as a <see cref="MessageBarControl"/>
/// with the right intent and text, never as markup.
/// </summary>
public class AdminTabsUseStandardControlsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PlatformAdmin = "admintabs-admin";
    private const string Invitee = "new.teammate@example.com";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services => services.AddSingleton<InvitationService>())
            .ConfigureDefaultNodeHub(config => config.AddPlatformSettingsTabAreas())
            .AddMeshNodes(
                new MeshNode(PlatformAdmin)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Admin Tabs Admin",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Admin Tabs Admin", Email = $"{PlatformAdmin}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", PlatformAdmin),
                // The platform-admin grant the areas' AdminGated check reads.
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminAppNodeType.Path));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private ISynchronizationStream<JsonElement> Open(string area)
    {
        TestUsers.DevLogin(Mesh, new AccessContext
            { ObjectId = PlatformAdmin, Name = "Admin Tabs Admin", Email = $"{PlatformAdmin}@meshweaver.io" });
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(PlatformAdmin), new LayoutAreaReference(area));
    }

    private static string? TypeOf(JsonElement control)
        => control.TryGetProperty("$type", out var t) ? t.GetString() : null;

    private static string? Text(JsonElement control, string property = "data")
        => control.TryGetProperty(property, out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    /// <summary>The message bars of a frame, as (intent, body).</summary>
    private static (string? Intent, string? Body)[] Notices(JsonElement frame)
        => [.. Areas(frame).Where(a => TypeOf(a.Control) == nameof(MessageBarControl))
            .Select(a => (Text(a.Control, "intent"), Text(a.Control, "body")))];

    private static bool HasLabel(JsonElement frame, string text)
        => Areas(frame).Any(a => TypeOf(a.Control) == nameof(LabelControl) && Text(a.Control) == text);

    private static bool HasMarkdown(JsonElement frame, string fragment)
        => Areas(frame).Any(a => TypeOf(a.Control) == nameof(MarkdownControl)
            && (Text(a.Control, "markdown") ?? Text(a.Control) ?? "").Contains(fragment, StringComparison.Ordinal));

    private static string? ButtonArea(JsonElement frame, string title)
        => Areas(frame).FirstOrDefault(a => TypeOf(a.Control) == nameof(ButtonControl) && Text(a.Control) == title).Id;

    [Fact(Timeout = 120000)]
    public async Task Invitations_RendersStandardControls_AndTheInviteOutcomesAreMessageBars()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = Open(PlatformSettingsTabAreas.InvitationsArea);

        var initial = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => HasLabel(f, "Invitations") && ButtonArea(f, "Invite") is not null,
                "the Invitations tab renders its heading and the Invite form", ct);

        NonStandard(initial).Should().BeEmpty("the Invitations tab holds no hand-built markup");
        HasMarkdown(initial, "`Features:Onboarding:InvitationOnly`").Should()
            .BeTrue("the config key is code formatting in Markdown, not a <code> tag");
        Notices(initial).Should().BeEmpty("no outcome is shown before a click");

        // Blank email → the Error banner.
        var invite = ButtonArea(initial, "Invite")!;
        stream.SubmitUserAction(new ClickedEvent(invite, stream.StreamId), actingUser: null, onRefused: null, onAccepted: null);
        var refused = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => Notices(f).Contains((MessageBarIntent.Error, "Enter a valid email address.")),
                "a blank invite shows the Error banner", ct);
        NonStandard(refused).Should().BeEmpty();

        // A real address → the Success banner, and the invitation is created.
        stream.UpdatePointer(JsonValue.Create(Invitee),
            LayoutAreaReference.GetDataPointer("invitationForm"), new JsonPointerReference("email"));
        stream.SubmitUserAction(new ClickedEvent(invite, stream.StreamId), actingUser: null, onRefused: null, onAccepted: null);
        var invited = await stream.Select(c => c.Value).Should().Within(Budget)
            .Match(f => Notices(f).Any(n => n.Intent == MessageBarIntent.Success
                    && (n.Body ?? "").StartsWith($"Invited {Invitee}", StringComparison.Ordinal)),
                "a valid invite shows the Success banner naming the address", ct);
        NonStandard(invited).Should().BeEmpty();
        Notices(invited).Should().NotContain(n => n.Intent == MessageBarIntent.Error,
            "the Success banner REPLACES the earlier error — one slot, the latest outcome");
    }

    [Fact(Timeout = 120000)]
    public async Task Inbox_RendersStandardControls()
    {
        var frame = await Open(PlatformSettingsTabAreas.InboxArea).Select(c => c.Value).Should().Within(Budget)
            .Match(f => HasMarkdown(f, "**not**"), "the Inbox tab renders its explanation",
                TestContext.Current.CancellationToken);

        NonStandard(frame).Should().BeEmpty("the Inbox tab holds no hand-built markup");
    }
}
