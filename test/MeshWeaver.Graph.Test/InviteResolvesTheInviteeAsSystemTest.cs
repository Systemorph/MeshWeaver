using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
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
/// 🚨 An invite-by-email resolves the invitee AS SYSTEM, and grants an existing account whoever is
/// inviting — issue #4309.
///
/// <para><b>The defect.</b> <see cref="SpaceInviteService.GrantOrScheduleAccess"/> and
/// <see cref="GroupInviteExtensions.InviteToGroup"/> decided "already on the system" with a
/// <c>nodeType:User content.email:…</c> query issued as the CALLER. A path-less <c>nodeType:User</c>
/// read is pinned to the <c>auth</c> mirror, and on Postgres that schema is row-level-filtered by the
/// caller's grants there — a Space admin holds none unless the deployment granted <c>Public</c> on
/// <c>auth</c>. Measured on the control instance on 2026-09-14: <c>nodeType:User</c> as a signed-in
/// platform admin → <b>0</b>. So the lookup answered "nobody", the flow took the invitation branch,
/// and a person who already had an account was invited instead of granted.</para>
///
/// <para><b>Why the existing suites never saw it, and why this one is shaped the way it is.</b> Two
/// masks stacked. The harness's DevLogin identity holds a ROOT <c>_Access</c> Admin grant, so every
/// read under it passes; and the in-memory provider re-decides each row through
/// <see cref="UserNodeType"/>'s own access rule, which reads any root User node for any authenticated
/// caller — the Postgres mirror's SQL filter does not consult that rule. Core has no Postgres suite,
/// so the mirror's zero cannot be reproduced here as behaviour; it is pinned instead as a property of
/// the request the service issues, resolved through the SAME <see cref="QueryIdentityResolver"/> every
/// storage provider resolves through (<see cref="TheAccountLookupResolvesAsSystemWhoeverIsAmbient"/>).
/// The behavioural tests run as a per-test Space/group admin with NO root grant — the production shape
/// — and pin the half the in-memory mesh CAN see: the pin on the invitee's own node, a write the
/// inviting admin has no rights on, which the immediate path used to perform as the caller.</para>
/// </summary>
public class InviteResolvesTheInviteeAsSystemTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "InviteSpace";
    private const string GroupPath = Space + "/Team";
    private const string Inviter = "alice";
    private const string Invitee = "bob";
    private const string InviteeEmail = "bob@acme.com";

    // ConfigureMeshBase: no PublicAdminAccess — Public holds nothing, as on a deployment.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Invite Space", NodeType = "Space" });

    private static readonly AccessContext InviterContext = new()
    {
        ObjectId = Inviter,
        Name = "Alice",
        Email = "alice@acme.com",
    };

    private SpaceInviteService NewService()
        => new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<SpaceInviteService>>());

    /// <summary>
    /// The property the Postgres mirror needs: whatever identity is ambient when the service runs,
    /// the lookup's viewer is <see cref="WellKnownUsers.System"/>. Resolved through
    /// <see cref="QueryIdentityResolver.Resolve"/> — the one rule every provider applies — with the
    /// inviting admin supplied as the ambient, exactly what <c>MeshService.StampViewer</c> would see.
    /// A lookup that inherits the ambient resolves as <c>alice</c> here and as nobody on <c>auth</c>.
    /// </summary>
    [Fact]
    public void TheAccountLookupResolvesAsSystemWhoeverIsAmbient()
    {
        var request = SpaceInviteService.AccountLookup(InviteeEmail);

        Assert.Contains("nodeType:User", request.Query);
        Assert.Contains($"content.email:{InviteeEmail}", request.Query);

        var identity = QueryIdentityResolver.Resolve(request, ambientUserId: Inviter);
        Assert.True(identity.IsSystem,
            $"the invitee lookup resolved as '{identity.UserId}' ({identity.Source}) with '{Inviter}' "
            + "ambient — as the caller the mirror-pinned read is filtered to the caller's grants on "
            + "auth, which a Space admin does not hold, and an existing account reads as absent");
    }

    /// <summary>
    /// A Space admin with no root grant invites an existing account with pinning on: the outcome is
    /// <see cref="SpaceInviteOutcome.Granted"/>, the assignment lands under the Space, and the Space is
    /// pinned on the invitee's node — a write the inviter could not perform as themself.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASpaceAdminWithoutARootGrantGrantsAndPinsAnExistingAccount()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (access.ImpersonateAsSystem())
        {
            await SeedInvitee(meshService);
            await GrantAdmin(meshService, Inviter, Space);
        }
        await AccountIsQueryable(meshService);

        SpaceInviteOutcome outcome;
        using (access.SwitchAccessContext(InviterContext))
        {
            outcome = await NewService()
                .Invite(Space, InviteeEmail, "Editor", pin: true, invitedBy: Inviter)
                .FirstAsync().Timeout(TestTimeouts.CrossSilo);
        }

        Assert.Equal(SpaceInviteOutcome.Granted, outcome);

        await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{Invitee}_Access")
            .Where(n => n?.Content is AccessAssignment a
                        && a.AccessObject == Invitee
                        && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(TestTimeouts.CrossSilo);
        await Mesh.GetWorkspace().GetMeshNodeStream(Invitee)
            .Where(n => n?.Content is User u && u.PinnedPaths.Contains(Space))
            .FirstAsync().Timeout(TestTimeouts.CrossSilo);
    }

    /// <summary>
    /// The group twin: a group admin with no root grant invites an existing account and gets
    /// <see cref="GroupInviteOutcome.Added"/> with the membership under the group — not an invitation
    /// for someone who is already here.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AGroupAdminWithoutARootGrantAddsAnExistingAccount()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (access.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode("Team", Space)
            {
                NodeType = "Group",
                Name = "Team",
                Content = new AccessObject { Description = "Test group" },
            }).Should().Within(TestTimeouts.CrossSilo).Emit("the group must exist");
            await SeedInvitee(meshService);
            await GrantAdmin(meshService, Inviter, GroupPath);
        }
        await AccountIsQueryable(meshService);

        GroupInviteOutcome outcome;
        using (access.SwitchAccessContext(InviterContext))
        {
            outcome = await Mesh.InviteToGroup(GroupPath, InviteeEmail, invitedBy: Inviter)
                .FirstAsync().Timeout(TestTimeouts.CrossSilo);
        }

        Assert.Equal(GroupInviteOutcome.Added, outcome);

        await Mesh.GetWorkspace().GetMeshNodeStream($"{GroupPath}/{Invitee}_Membership")
            .Where(n => n?.Content is GroupMembership gm
                        && gm.Member == Invitee
                        && gm.Groups.Any(e => e.Group == GroupPath))
            .FirstAsync().Timeout(TestTimeouts.CrossSilo);
    }

    private static async Task SeedInvitee(IMeshService meshService)
        => await meshService.CreateNode(new MeshNode(Invitee)
        {
            NodeType = "User",
            Name = "Bob",
            Content = new User { Email = InviteeEmail, FullName = "Bob" },
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the invitee's account must exist");

    /// <summary>The inviter's ONLY grant: Admin on the node they invite to — no root, no auth.</summary>
    private static async Task GrantAdmin(IMeshService meshService, string subject, string scope)
        => await meshService.CreateNode(new MeshNode($"{subject}_Access", $"{scope}/_Access")
        {
            NodeType = AccessAssignmentNodeType.NodeType,
            Name = $"{subject} Access",
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = subject,
                DisplayName = subject,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }],
            },
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the inviter's grant must land");

    /// <summary>Waits until the account is in the query index the service reads (as System, the
    /// service's own shape) so the invite decides on a settled snapshot.</summary>
    private static async Task AccountIsQueryable(IMeshService meshService)
        => await meshService.Query<MeshNode>(SpaceInviteService.AccountLookup(InviteeEmail))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == Invitee))
            .FirstAsync().Timeout(TestTimeouts.CrossSilo);
}
