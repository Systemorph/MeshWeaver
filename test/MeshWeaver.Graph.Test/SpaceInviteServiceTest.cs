using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests <see cref="SpaceInviteService"/> — the two invite cases: an existing account is granted
/// immediately; an unknown email is scheduled (an <see cref="EventSubscription"/>) and invited (an
/// <c>Invitation</c> node), so the grant lands automatically when they sign up.
/// </summary>
public class SpaceInviteServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "TeamSpace";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Team Space", NodeType = "Space" });

    private SpaceInviteService NewService()
        => new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<SpaceInviteService>>());

    [Fact(Timeout = 60000)]
    public async Task InviteExistingUser_GrantsImmediately()
    {
        const string email = "bob@acme.com";
        const string userId = "bob";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(userId)
            {
                NodeType = "User",
                Name = "Bob",
                Content = new User { Email = email, FullName = "Bob" },
            }).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        // Wait until the account is queryable by email (the service looks it up that way).
        await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{email}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == userId))
            .FirstAsync().Timeout(30.Seconds()).Await();

        var outcome = await NewService().Invite(Space, email, "Editor", pin: true, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds()).Await();
        Assert.Equal(SpaceInviteOutcome.Granted, outcome);

        // The AccessAssignment landed with Editor, and the Space was pinned.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{userId}_Access")
            .Where(n => n?.Content is AccessAssignment a && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(20.Seconds()).Await();
        await Mesh.GetWorkspace().GetMeshNodeStream(userId)
            .Where(n => n?.Content is User u && u.PinnedPaths.Contains(Space))
            .FirstAsync().Timeout(20.Seconds()).Await();
    }

    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_SchedulesGrantAndCreatesInvitation()
    {
        const string email = "carol@acme.com";

        var outcome = await NewService().Invite(Space, email, "Viewer", pin: false, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);
        Assert.Equal(SpaceInviteOutcome.Invited, outcome);

        // An event subscription was created to grant Viewer on the Space when this email's User appears.
        await Mesh.GetWorkspace().GetQuery("inv-subs",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}")
            .Where(nodes => (nodes ?? []).Any(n => n.Content is EventSubscription s
                && s.TriggerType == EventTriggerType.NodeChange
                && s.MatchValue == email && s.TargetPath == Space && s.Role == "Viewer"))
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);

        // An Invitation node was created for the email (the InvitationEmailSender emails it),
        // carrying the target Space so the email addresses it by name + links to it.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}")
            .Where(n => n?.Content is Invitation inv && inv.Email == email && inv.SpacePath == Space)
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The host's personal message travels on the invitation as <see cref="Invitation.Message"/>, kept
    /// apart from the platform's admin-facing <see cref="Invitation.Note"/> (Plugins#4551): the email
    /// renders the first and must never render the second.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_CarriesTheHostsMessage_ApartFromTheSystemNote()
    {
        const string email = "dave@acme.com";

        var outcome = await NewService()
            .Invite(Space, email, "Viewer", pin: false, invitedBy: "admin", message: "  Welcome aboard, Dave!  ")
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);
        Assert.Equal(SpaceInviteOutcome.Invited, outcome);

        var invitation = await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}")
            .Where(n => n?.Content is Invitation inv && inv.Email == email)
            .Select(n => (Invitation)n!.Content!)
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);

        Assert.Equal("Welcome aboard, Dave!", invitation.Message);
        Assert.Equal($"Invited to space {Space}", invitation.Note);
    }

    /// <summary>Negative control: an invite without a message, or with only whitespace, stores none —
    /// so the email adds no empty paragraph.</summary>
    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_WithABlankMessage_StoresNone()
    {
        const string email = "erin@acme.com";

        await NewService()
            .Invite(Space, email, "Viewer", pin: false, invitedBy: "admin", message: "   ")
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);

        var invitation = await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}")
            .Where(n => n?.Content is Invitation inv && inv.Email == email)
            .Select(n => (Invitation)n!.Content!)
            .FirstAsync().Timeout(30.Seconds()).Await(TestContext.Current.CancellationToken);

        Assert.Null(invitation.Message);
    }

    /// <summary>A German-speaking inviting host (#7482).</summary>
    private static readonly AccessContext GermanHost = new() { ObjectId = "admin", Name = "Admin", Locale = "de" };

    /// <summary>
    /// Invites <paramref name="email"/> AS <see cref="GermanHost"/> with <paramref name="locale"/> as the
    /// pick, and reads the stored invitation back. The invite is issued inside the host's context and
    /// awaited outside it: the language must be read on the host's own turn.
    /// </summary>
    /// <param name="email">The invitee.</param>
    /// <param name="locale">The host's pick, or null.</param>
    /// <param name="ct">The test's cancellation.</param>
    /// <returns>The stored invitation.</returns>
    private async Task<Invitation> InviteAsGermanHost(string email, string? locale, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<SpaceInviteOutcome> invite;
        using (access.SwitchAccessContext(GermanHost))
            invite = NewService().Invite(Space, email, "Viewer", pin: false, invitedBy: "admin",
                message: null, locale: locale);
        Assert.Equal(SpaceInviteOutcome.Invited,
            await invite.FirstAsync().Timeout(30.Seconds()).Await(ct));

        return await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}")
            .Select(n => n?.ContentAs<Invitation>(Mesh.JsonSerializerOptions))
            .Where(inv => inv is not null && inv.Email == email)
            .Select(inv => inv!)
            .FirstAsync().Timeout(30.Seconds()).Await(ct);
    }

    /// <summary>#7482: with no language picked, the invitation is written in the INVITER's language.</summary>
    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_WithNoLanguagePicked_StoresTheInvitersLanguage()
    {
        var invitation = await InviteAsGermanHost("frank@acme.com", locale: null, TestContext.Current.CancellationToken);
        Assert.Equal("de", invitation.Locale);
    }

    /// <summary>#7482: the host's explicit pick wins over their own language.</summary>
    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_AnExplicitLanguagePick_Wins()
    {
        var invitation = await InviteAsGermanHost("grace@acme.com", locale: "en", TestContext.Current.CancellationToken);
        Assert.Equal("en", invitation.Locale);
    }

    /// <summary>
    /// #7482 end to end: the email rendered from the stored invitation is in the stored language —
    /// German heading, body, button and footer, and no English left in it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheInvitationEmail_IsWrittenInTheStoredLanguage()
    {
        var invitation = await InviteAsGermanHost("heidi@acme.com", locale: null, TestContext.Current.CancellationToken);

        var (subject, html) = InvitationEmail.Build(invitation, "Team Space", "https://portal.example.com");

        Assert.Equal("Sie wurden zu Team Space eingeladen", subject);
        Assert.Contains("Sie wurden zu „Team Space“ auf Memex eingeladen.", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("Team Space öffnen", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains(LocalizationCatalog.Get("notification.email.standingFooter", "de"), System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("invited", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://portal.example.com/TeamSpace", html);
    }
}
