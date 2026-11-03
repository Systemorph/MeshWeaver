using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Space invite FORM as a person uses it (<see cref="SpaceInviteLayoutArea"/>): open
/// <c>{Space}/Invite</c>, type an email, click Invite, and read what the page shows.
///
/// <para><b>The defect</b> (maintainer, 2026-11-03: <i>"no screen change happening after invite"</i>).
/// The click fired the invite on a subscription of its own and answered the click at once, so the
/// button's busy state — the only in-form feedback the framework has (<c>Doc/GUI/ButtonPendingState</c>)
/// — ended before the invite started, and the click settled with NO outcome line. The outcome went
/// only to a closable <c>$Dialog</c> without a close action: the store keeps a closed dialog, and the
/// Invite dialog's control is the same whatever its text (the text is in its content area), so on a
/// page that had shown one dialog every later outcome — success or error — changed nothing a renderer
/// that de-duplicates the control could show; and while the invite had not answered, nothing showed
/// at all. These tests read the click state the button renders: on the old code every click settled
/// with neither <see cref="ClickProgress.Summary"/> nor <see cref="ClickProgress.Error"/>.</para>
///
/// <para><b>The authorization</b> pinned by <see cref="AViewerIsRefused_AndNothingIsScheduled"/>: the
/// area is served to every reader, and for an unknown email the service writes the Invitation and the
/// deferred grant as System — on the old code a Viewer could schedule any role, Admin included, for
/// any address.</para>
/// </summary>
public class SpaceInviteFormTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "InviteFormSpace";

    private static readonly AccessContext Host = new() { ObjectId = "inviteformhost", Name = "Host", Email = "host@acme.com" };
    private static readonly AccessContext Reader = new() { ObjectId = "inviteformreader", Name = "Reader", Email = "reader@acme.com" };

    // ConfigureMeshBase: no public admin grant — the host is an Admin of THIS Space only, the reader a Viewer.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode(Space) { Name = "Invite Form Space", NodeType = "Space" },
            Grant(Host, "Admin"),
            Grant(Reader, "Viewer"));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static MeshNode Grant(AccessContext who, string role) =>
        new($"{who.ObjectId}_Access", $"{Space}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{who.Name} — {role}",
            MainNode = Space,
            Content = new AccessAssignment
            {
                AccessObject = who.ObjectId!,
                DisplayName = who.Name,
                Roles = [new RoleAssignment { Role = role }],
            },
        };

    [Fact(Timeout = 120000)]
    public async Task InvitingANewEmail_SettlesTheClickNamingTheInvitation()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "newcomer@acme.com";
        ActAs(Host);
        var form = await OpenForm(ct);

        var state = await InviteAndSettle(form, email, ct);

        state.Error.Should().BeNull();
        state.Summary.Should().Be($"Invited {email} as Editor. They get access automatically when they sign up.",
            "the click itself states the outcome — the old click settled before the invite ran, with no outcome line");
        var invitation = (await InvitationOf(email, ct)).Should().ContainSingle(
            "the click settled after the invitation was written").Subject.Content.Should().BeOfType<Invitation>().Subject;
        invitation.SpacePath.Should().Be(Space);
        invitation.InvitedBy.Should().Be(Host.ObjectId, "the invitation is stamped with the inviter");
    }

    [Fact(Timeout = 120000)]
    public async Task InvitingAnExistingAccount_SettlesTheClickNamingTheGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "existing@acme.com";
        const string userId = "inviteformexisting";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(userId)
            {
                NodeType = "User", Name = "Existing", Content = new User { Email = email, FullName = "Existing" },
            }).Timeout(TestTimeouts.Convergence).Await(ct);
        await meshService.Query<MeshNode>(SpaceInviteService.AccountLookup(email))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == userId))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        ActAs(Host);
        var form = await OpenForm(ct);

        var state = await InviteAndSettle(form, email, ct);

        state.Error.Should().BeNull();
        state.Summary.Should().Be($"{email} already has an account — granted Editor on this Space.");
        await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{userId}_Access")
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is AccessAssignment a && a.Roles.Any(r => r.Role == "Editor"), "the grant landed", ct);
    }

    /// <summary>
    /// A second invite on the same page is as visible as the first: each settles with ITS outcome, and
    /// the form is cleared for the next one. (On the old code the second outcome reached only the
    /// dialog area, unchanged in shape from the first.)
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ASecondInviteOnTheSamePage_SettlesWithItsOwnOutcome_OnAClearedForm()
    {
        var ct = TestContext.Current.CancellationToken;
        ActAs(Host);
        var form = await OpenForm(ct);

        var first = await InviteAndSettle(form, "first@acme.com", ct);
        first.Summary.Should().Contain("first@acme.com");
        await form.Stream.GetDataStream<JsonElement>(new JsonPointerReference(form.DataContext))
            .Should().Within(TestTimeouts.Convergence)
            .Match(f => f.ValueKind == JsonValueKind.Object && f.GetProperty("email").GetString() == "",
                "a settled invite clears the email for the next one", ct);

        var second = await InviteAndSettle(form, "second@acme.com", ct, afterSession: first.Session);
        second.Summary.Should().Contain("second@acme.com", "the second click states its own outcome");
    }

    [Fact(Timeout = 120000)]
    public async Task AnInvalidEmail_FailsTheClickWithTheReason()
    {
        var ct = TestContext.Current.CancellationToken;
        ActAs(Host);
        var form = await OpenForm(ct);

        var state = await InviteAndSettle(form, "not-an-email", ct);

        state.Error.Should().Contain("Please enter a valid email address.");
    }

    [Fact(Timeout = 120000)]
    public async Task AViewerIsRefused_AndNothingIsScheduled()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "alias-of-the-reader@acme.com";
        ActAs(Reader);
        var form = await OpenForm(ct);

        var state = await InviteAndSettle(form, email, ct, role: "Admin");

        state.Error.Should().Contain("You need edit rights on this Space to invite people to it.");
        var scheduled = await InvitationOf(email, ct);
        scheduled.Should().BeEmpty("a refused invite writes nothing — the refusal comes before the System-identity writes");
    }

    /// <summary>
    /// The form ends with ONE button row — Cancel, then Invite — after the message, the language and
    /// the pin. Cancel only navigates back to the node the form was opened on: it carries no click
    /// action, so it cannot send anything.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheFormEndsWithCancelThenInvite_AndCancelOnlyNavigatesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        ActAs(Host);
        var form = await OpenForm(ct);

        var root = Control(form.Page, SpaceInviteLayoutArea.AreaName);
        var children = Children(root);
        var lastRow = Control(form.Page, children[^1]);
        lastRow.GetProperty("$type").GetString().Should().Be("StackControl");
        var buttons = Children(lastRow).Select(a => Control(form.Page, a)).ToArray();
        buttons.Select(b => b.GetProperty("$type").GetString()).Should().Equal("ButtonControl", "ButtonControl");
        buttons.Select(b => b.GetProperty("data").GetString()).Should().Equal("Cancel", "Invite");

        var cancel = buttons[0];
        cancel.GetProperty("navigateToHref").GetString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(Space, MeshNodeLayoutAreas.OverviewArea));
        (cancel.TryGetProperty("isClickable", out var clickable) && clickable.GetBoolean())
            .Should().BeFalse("Cancel has no click action: leaving sends nothing");

        // Everything else precedes the row: the pin is the last field before it.
        Control(form.Page, children[^2]).GetProperty("$type").GetString().Should().Be("CheckBoxControl");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Form(ISynchronizationStream<JsonElement> Stream, JsonElement Page, string InviteArea, string DataContext);

    /// <summary>The Admin-partition invitation for <paramref name="email"/>, read as System (one snapshot).</summary>
    private async Task<IReadOnlyCollection<MeshNode>> InvitationOf(string email, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}").AsSystem())
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyCollection<MeshNode>)c.Items.ToArray())
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private void ActAs(AccessContext who)
    {
        TestUsers.DevLogin(Mesh, who);
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetContext(who);
    }

    private async Task<Form> OpenForm(CancellationToken ct)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(Space), new LayoutAreaReference(SpaceInviteLayoutArea.AreaName));
        var page = await stream.Select(ci => ci.Value)
            .Where(v => FindArea(v, IsInviteButton) is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var dataContext = Control(page, FindArea(page, c => Is(c, "TextFieldControl"))!)
            .GetProperty("dataContext").GetString()!;
        return new Form(stream, page, FindArea(page, IsInviteButton)!, dataContext);
    }

    private async Task<ClickProgress> InviteAndSettle(
        Form form, string email, CancellationToken ct, string? role = null, int afterSession = 0)
    {
        form.Stream.UpdatePointer(JsonValue.Create(email), form.DataContext, new JsonPointerReference("email"));
        if (role is not null)
            form.Stream.UpdatePointer(JsonValue.Create(role), form.DataContext, new JsonPointerReference("role"));
        await form.Stream.GetDataStream<JsonElement>(new JsonPointerReference(form.DataContext))
            .Should().Within(TestTimeouts.Convergence)
            .Match(f => f.ValueKind == JsonValueKind.Object && f.GetProperty("email").GetString() == email
                        && (role is null || f.GetProperty("role").GetString() == role),
                "the typed values are in the form", ct);

        var answer = new ReplaySubject<string?>(1);
        form.Stream.SubmitUserAction(new ClickedEvent(form.InviteArea, form.Stream.StreamId), actingUser: null,
            onRefused: answer.OnNext, onAccepted: () => answer.OnNext(null));
        await answer.Should().Within(TestTimeouts.Convergence).Emit("the owner answers every click", ct);

        return (await form.Stream.GetDataStream<ClickProgress>(
                new JsonPointerReference(ClickProgress.PointerFor(form.InviteArea)))
            .Should().Within(TestTimeouts.Convergence)
            .Match(p => p is { Running: false } && p.Session > afterSession
                        && (!string.IsNullOrEmpty(p.Summary) || !string.IsNullOrEmpty(p.Error)),
                "the click settles VISIBLY — naming its outcome or its error", ct))!;
    }

    private static bool IsInviteButton(JsonElement control)
        => Is(control, "ButtonControl") && control.TryGetProperty("data", out var d) && d.GetString() == "Invite";

    private static bool Is(JsonElement control, string type)
        => control.TryGetProperty("$type", out var t) && t.GetString() == type;

    private static JsonElement Control(JsonElement page, string area)
        => page.GetProperty("areas").GetProperty(JsonSerializer.Serialize(area));

    private static string[] Children(JsonElement stack)
        => stack.GetProperty("areas").EnumerateArray().Select(a => a.GetProperty("area").GetString()!).ToArray();

    // Area keys in the store are JSON-encoded strings ("\"Invite/3\"").
    private static string? FindArea(JsonElement page, Func<JsonElement, bool> predicate)
    {
        if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("areas", out var areas))
            return null;
        foreach (var area in areas.EnumerateObject())
            if (area.Value.ValueKind == JsonValueKind.Object && predicate(area.Value))
                return area.Name.StartsWith('"') ? JsonSerializer.Deserialize<string>(area.Name) : area.Name;
        return null;
    }
}
