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
/// Tests <see cref="GroupInviteExtensions.ParseEmails"/> — the pasted-list parser behind the bulk
/// group invite. Pure static, no mesh needed.
/// </summary>
public class GroupBulkInviteParseTest
{
    [Fact]
    public void ParseEmails_SplitsOnSeparators_DedupesCaseInsensitively_PreservesOrder()
    {
        var (valid, invalid) = GroupInviteExtensions.ParseEmails(
            "anna@acme.com, ben@acme.com;carol@acme.com\nANNA@acme.com\r\n  dave@acme.com  ");
        Assert.Equal(["anna@acme.com", "ben@acme.com", "carol@acme.com", "dave@acme.com"], valid);
        Assert.Empty(invalid);
    }

    [Fact]
    public void ParseEmails_UnwrapsDisplayNameAngleBracketEntries()
    {
        // The Outlook copy-paste shape: "Display Name <email>" — the display name is NOT junk.
        var (valid, invalid) = GroupInviteExtensions.ParseEmails(
            "Anna Ammann <anna@acme.com>, Ben Berger <ben@acme.com>");
        Assert.Equal(["anna@acme.com", "ben@acme.com"], valid);
        Assert.Empty(invalid);
    }

    [Theory]
    [InlineData("not-an-email")]      // no @
    [InlineData("@acme.com")]         // empty local part
    [InlineData("anna@")]             // empty domain
    [InlineData("anna@acmecom")]      // undotted domain (the classic truncated paste)
    [InlineData("anna@@acme.com")]    // double @
    [InlineData("anna@.acme.com.")]   // leading/trailing dot in domain
    public void ParseEmails_FlagsJunkTokensAsInvalid(string junk)
    {
        var (valid, invalid) = GroupInviteExtensions.ParseEmails($"good@acme.com\n{junk}");
        Assert.Equal(["good@acme.com"], valid);
        Assert.Equal([junk], invalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n , ; ")]
    public void ParseEmails_EmptyInput_YieldsNothing(string? input)
    {
        var (valid, invalid) = GroupInviteExtensions.ParseEmails(input);
        Assert.Empty(valid);
        Assert.Empty(invalid);
    }
}

/// <summary>
/// Tests <see cref="GroupInviteExtensions.InviteAllToGroup"/> — the bulk "invite a pasted list of
/// emails to a group with a role" feature behind the group's "Invite by Email" dialog:
/// <list type="bullet">
///   <item>An existing account is added immediately — a <c>GroupMembership</c> node PLUS the selected
///     role as an <c>AccessAssignment</c> on the group (groups aren't publicly readable, so the grant is
///     what lets the member see the group).</item>
///   <item>An unknown email gets an <c>Invitation</c> (emailed by <c>InvitationEmailSender</c>, addressing
///     the group by name via <c>SpacePath</c>) and a durable <see cref="EventSubscription"/> carrying the
///     role — fired by <see cref="EventSubscriptionRunner"/> on sign-up, landing the identical
///     membership + grant.</item>
///   <item>Junk tokens are skipped and reported, never written.</item>
/// </list>
/// </summary>
public class GroupBulkInviteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "BulkSpace";
    private const string GroupPath = Space + "/Crew";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Bulk Space", NodeType = "Space" });

    private async Task SeedGroupAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode("Crew", Space)
            {
                NodeType = "Group",
                Name = "Crew",
                Content = new AccessObject { Description = "Bulk-invited crew" },
            }).Should().Emit();
    }

    private async Task SeedUserAsync(string userId, string email)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(userId)
            {
                NodeType = "User",
                Name = userId,
                Content = new User { Email = email, FullName = userId },
            }).Should().Emit();

        // Wait until the account is queryable by email (the invite looks it up that way).
        await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{email}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == userId))
            .FirstAsync().Timeout(30.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task BulkInvite_MixedList_AddsExisting_InvitesUnknown_SkipsJunk_DedupesRepeats()
    {
        const string existingEmail = "bob@acme.com";
        const string existingId = "bob";
        const string newEmail = "carol@acme.com";

        await SeedGroupAsync();
        await SeedUserAsync(existingId, existingEmail);

        // One existing account, one unknown email (repeated in Outlook shape → deduped), one junk token.
        var result = await Mesh.InviteAllToGroup(GroupPath,
                $"{existingEmail}\n{newEmail}\nnot-an-email\nCarol <{newEmail}>",
                invitedBy: "admin", role: Role.Editor.Id)
            .FirstAsync().Timeout(30.Seconds());

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.InvitedCount);
        Assert.Equal(1, result.InvalidCount);
        Assert.Equal(["not-an-email"], result.InvalidEmails.ToArray());
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(GroupBulkInviteStatus.Added,
            result.Entries.Single(e => e.Email == existingEmail).Status);
        Assert.Equal(GroupBulkInviteStatus.Invited,
            result.Entries.Single(e => e.Email == newEmail).Status);

        // The existing user landed BOTH the membership and the selected role on the group.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{GroupPath}/{existingId}_Membership")
            .Where(n => n?.Content is GroupMembership gm
                        && gm.Member == existingId
                        && gm.Groups.Any(e => e.Group == GroupPath))
            .FirstAsync().Timeout(20.Seconds());
        await Mesh.GetWorkspace().GetMeshNodeStream($"{GroupPath}/_Access/{existingId}_Access")
            .Where(n => n?.Content is AccessAssignment a
                        && a.AccessObject == existingId
                        && a.Roles.Any(r => r.Role == Role.Editor.Id && !r.Denied))
            .FirstAsync().Timeout(20.Seconds());

        // The unknown email got an Invitation addressing the group (SpacePath drives the invite email)…
        await Mesh.GetWorkspace().GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(newEmail)}")
            .Where(n => n?.Content is Invitation inv
                        && inv.Email == newEmail
                        && inv.SpacePath == GroupPath)
            .FirstAsync().Timeout(30.Seconds());

        // …and a deferred subscription carrying the SELECTED role, so sign-up lands the identical grant.
        await Mesh.GetWorkspace().GetQuery("bulk-inv-subs",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}")
            .Where(nodes => (nodes ?? []).Any(n => n.Content is EventSubscription s
                && s.TriggerType == EventTriggerType.NodeChange
                && s.ContinuationType == EventContinuationType.AddToGroup
                && s.MatchValue == newEmail
                && s.TargetPath == GroupPath
                && s.Role == Role.Editor.Id))
            .FirstAsync().Timeout(30.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task BulkInvitedEmail_SignsUp_MembershipAndRoleLand()
    {
        const string email = "dave@acme.com";
        const string userId = "dave";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        await SeedGroupAsync();

        // Arm the runner BEFORE the triggering write so the live change-feed path fires the continuation.
        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        await runner.StartAsync(default);

        var result = await Mesh.InviteAllToGroup(GroupPath, email, invitedBy: "admin", role: Role.Editor.Id)
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(1, result.InvitedCount);

        // The invitee signs up — their User node is created (as onboarding does).
        await SeedUserAsync(userId, email);

        // Wait for the subscription's terminal state first (race-free: both writes complete BEFORE the
        // Fired write, so once Fired is observed the membership + grant already exist).
        var subId = $"addgroup_{SpaceInviteService.Slug(email)}_{SpaceInviteService.Slug(GroupPath)}";
        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subId))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");

        // Membership + the selected role landed — identical to what an immediate add would have written.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{GroupPath}/{userId}_Membership")
            .Where(n => n?.Content is GroupMembership gm
                        && gm.Member == userId
                        && gm.Groups.Any(e => e.Group == GroupPath))
            .FirstAsync().Timeout(20.Seconds());
        await Mesh.GetWorkspace().GetMeshNodeStream($"{GroupPath}/_Access/{userId}_Access")
            .Where(n => n?.Content is AccessAssignment a
                        && a.AccessObject == userId
                        && a.Roles.Any(r => r.Role == Role.Editor.Id && !r.Denied))
            .FirstAsync().Timeout(20.Seconds());
    }
}
