using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="RestoreSelfGrantLogonAction"/> puts back a user's own Admin grant on their home when it
/// is missing (MeshWeaver#5225), and does nothing else.
///
/// <para><b>Controls.</b> The restore is paired with the cases on the other side of every line it
/// draws, so no constant behaviour passes the class. A user whose home holds a DENIED assignment
/// naming them keeps it untouched: that is a decision, and restoring over it would widen access. A
/// context whose identity is not the home's owner restores nothing, which pins "authorize as
/// caller". Service principals and platform identities are never eligible. Every assertion reads the
/// authoritative store, never the eventually consistent query index.</para>
/// </summary>
public class RestoreSelfGrantLogonActionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService? Access => Mesh.ServiceProvider.GetService<AccessService>();

    private static string GrantPath(string user) => $"{user}/_Access/{user}_Access";

    private static AccessContext Person(string user) => new() { ObjectId = user, Name = user, Email = $"{user}@meshweaver.io" };

    /// <summary>Creates the user's partition root as onboarding does — as System. The User type's
    /// post-creation handler writes the self-grant.</summary>
    private async Task CreateUserAsync(string user, CancellationToken ct)
    {
        await Access.RunAsSystem(() => MeshService.CreateNode(MeshNode.FromPath(user) with
        {
            NodeType = UserNodeType.NodeType,
            Name = user,
            State = MeshNodeState.Active,
            Content = new User { FullName = user },
        })).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>The stored self-grant, read from the authoritative store, or null.</summary>
    private IObservable<AccessAssignment?> StoredGrant(string user) =>
        Storage.Read(GrantPath(user), Mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty()
            .Select(node => node?.ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions));

    /// <summary>Waits until the stored self-grant satisfies <paramref name="predicate"/>.</summary>
    private Task<AccessAssignment?> AwaitGrant(string user, Func<AccessAssignment?, bool> predicate, CancellationToken ct) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => StoredGrant(user))
            .Where(predicate)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>Waits until the query index the action reads agrees with the store, so the case
    /// tests the action and not index lag.</summary>
    private Task AwaitIndexedAssignments(string user, int count, CancellationToken ct) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Access.RunAsSystem(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{user}/_Access nodeType:{RestoreSelfGrantLogonAction.AssignmentNodeType}"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => c.Items.Count())
                .Take(1)))
            .Where(n => n == count)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>Removes the self-grant from the STORE — the state the affected account was found in
    /// (the grant write never landed). A mesh delete is refused here, rightly: the last-admin
    /// invariant protects a home from losing its only administrator through the front door.</summary>
    private async Task RemoveGrantAsync(string user, CancellationToken ct)
    {
        await AwaitGrant(user, g => g is not null, ct);
        await Storage.DeleteAsync(GrantPath(user), ct);
        await AwaitGrant(user, g => g is null, ct);
        await AwaitIndexedAssignments(user, 0, ct);
    }

    private Task Run(string userPath, AccessContext identity, CancellationToken ct) =>
        new RestoreSelfGrantLogonAction()
            .Run(new LogonActionContext(userPath, identity, Mesh))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 120_000)]
    public async Task AMissingSelfGrant_IsRestored_AsAdminOnTheirOwnHome()
    {
        var ct = TestContext.Current.CancellationToken;
        const string user = "selfgrant-missing";
        await CreateUserAsync(user, ct);
        await RemoveGrantAsync(user, ct);

        await Run(user, Person(user), ct);

        var restored = await AwaitGrant(user, g => g is not null, ct);
        restored!.AccessObject.Should().Be(user);
        restored.Roles.Should().ContainSingle(r => r.Role == Role.Admin.Id && !r.Denied,
            "exactly the shape account creation writes: Admin, allowing, on their own home");

        var node = await Storage.Read(GrantPath(user), Mesh.JsonSerializerOptions).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        node!.MainNode.Should().Be(user, "the grant is scoped to the user's own partition and nothing wider");

        // Idempotent: a second logon finds it and writes nothing (create, never upsert — no fault).
        await AwaitIndexedAssignments(user, 1, ct);
        await Run(user, Person(user), ct);
        (await StoredGrant(user).Timeout(TestTimeouts.Convergence).Await(ct))!.Roles.Should().HaveCount(1);
    }

    [Fact(Timeout = 120_000)]
    public async Task ADeniedSelfAssignment_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        const string user = "selfgrant-denied";
        await CreateUserAsync(user, ct);
        await RemoveGrantAsync(user, ct);

        // An admin removed the user's own rights: a DENIED assignment naming them.
        // It sits at its own id, not at the self-grant's path, so the create-not-upsert alone could
        // NOT protect it: only the "an assignment naming the user is a decision" rule does.
        var denied = new MeshNode($"{user}_Revoked", $"{user}/_Access")
        {
            NodeType = RestoreSelfGrantLogonAction.AssignmentNodeType,
            Name = $"{user} Access (revoked)",
            MainNode = user,
            Content = new AccessAssignment
            {
                AccessObject = user,
                DisplayName = user,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = true }],
            },
        };
        await Access.RunAsSystem(() => MeshService.CreateNode(denied))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await AwaitIndexedAssignments(user, 1, ct);

        await Run(user, Person(user), ct);

        (await StoredGrant(user).Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull(
            "negative control: a deliberate deny is a decision, and the restore must never add an Admin grant beside it");
        var deny = await Storage.Read(denied.Path, Mesh.JsonSerializerOptions).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        deny!.ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions)!.Roles.Should().ContainSingle()
            .Which.Denied.Should().BeTrue("the deny itself is untouched");
    }

    [Fact(Timeout = 120_000)]
    public async Task ACallerWhoIsNotTheOwner_RestoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "selfgrant-owner";
        await CreateUserAsync(owner, ct);
        await RemoveGrantAsync(owner, ct);

        // The identity is someone else: the authorization step must refuse to act on the owner's home.
        await Run(owner, Person("selfgrant-intruder"), ct);

        (await StoredGrant(owner).Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull(
            "negative control: the restore is authorized by the caller BEING the home's owner");
    }

    [Theory]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData(WellKnownUsers.Public)]
    [InlineData(WellKnownUsers.System)]
    [InlineData("svc-name-check")]
    [InlineData("User/legacy")]
    public void NonPersons_AreNeverEligible(string objectId) =>
        RestoreSelfGrantLogonAction.IsEligiblePerson(new AccessContext { ObjectId = objectId, Name = objectId })
            .Should().BeFalse();

    [Fact]
    public void APerson_IsEligible_AndTheActionRunsFirstOnEveryLogon()
    {
        RestoreSelfGrantLogonAction.IsEligiblePerson(Person("alice")).Should().BeTrue();
        var action = new RestoreSelfGrantLogonAction();
        action.Mode.Should().Be(LogonActionMode.EveryLogon, "a grant lost later must be restored later too");
        Mesh.ServiceProvider.GetServices<ILogonAction>().Min(a => a.Order).Should().Be(action.Order,
            "every other platform action writes into the home as the user and needs the grant first");
    }
}
