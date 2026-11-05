using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🔒 <b>A locked account holds nothing, and an unlock gives it back unchanged</b>
/// (MeshWeaver.Plugins#4997, Doc/Architecture/AccountLockout).
///
/// <para>The lock is a record in the Admin partition (<see cref="AccountLock"/>), written by a
/// platform administrator through <see cref="AccountLocks.Lock"/> — authorized as the caller,
/// executed as System. These tests pin the four things the issue asks for at the mesh level: the
/// fold answers <see cref="Permission.None"/> for the locked person (their own home included) and
/// the query boundary answers their reads with nothing; an unlock restores both; a non-admin cannot
/// lock anyone, and no administrator can be locked; and — the negative control — every other
/// person's access is untouched throughout.</para>
///
/// <para>🚨 <c>ConfigureMeshBase</c>, not <c>base.ConfigureMesh</c>: the latter grants Public Admin
/// everywhere, under which a locked person's "nothing" would still be "everything" through the
/// Public leg the lock is meant to cut — and the control would pass proving nothing.</para>
///
/// <para><b>Negative controls,</b> run before committing: with the fold's lock branch disabled,
/// <see cref="ALockedAccount_HoldsNothing_AndAnUnlock_GivesItBack"/> fails at "the locked person holds
/// nothing on their home" while the refusal tests pass; with the query-boundary refusal disabled,
/// <see cref="ALockedViewersQuery_IsAnsweredAtTheBoundary_FromNoPartition"/> fails (the in-process
/// provider still returns no rows — the fold filters them — so only the boundary's own answer tells
/// the two apart).</para>
/// </summary>
public class AccountLockoutTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminPartition = "Admin";

    /// <summary>A platform administrator: <c>Permission.All</c> at scope <c>Admin</c>.</summary>
    private const string PlatformAdmin = "lock-admin";

    /// <summary>A second platform administrator — the one the first must not be able to lock.</summary>
    private const string OtherAdmin = "lock-admin-two";

    /// <summary>The person who is locked out.</summary>
    private const string Leaver = "lock-leaver";

    /// <summary>A bystander — the negative control.</summary>
    private const string Stayer = "lock-stayer";

    /// <summary>A shared space both people can edit.</summary>
    private const string SharedSpace = "LockShared";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                new MeshNode(Leaver) { Name = "Leaver", NodeType = "Markdown" },
                new MeshNode(Stayer) { Name = "Stayer", NodeType = "Markdown" },
                new MeshNode(SharedSpace) { Name = "Shared", NodeType = "Markdown" },
                new MeshNode("Note", SharedSpace) { Name = "A shared note", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition),
                AssignmentNodeFactory.UserRole(OtherAdmin, "Admin", AdminPartition),
                AssignmentNodeFactory.UserRole(Leaver, "Admin", Leaver),
                AssignmentNodeFactory.UserRole(Stayer, "Admin", Stayer),
                AssignmentNodeFactory.UserRole(Leaver, "Editor", SharedSpace),
                AssignmentNodeFactory.UserRole(Stayer, "Editor", SharedSpace));

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    private Task<Permission> Settle(string path, string user, Func<Permission, bool> until, string because)
        => Mesh.GetEffectivePermissions(path, user).Where(until).Take(1)
            .Should().Within(Budget).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    private static bool CanEdit(Permission p) => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Update);

    /// <summary>The query a person's reads of the shared space go through — the SQL providers' path.</summary>
    private Task<IReadOnlyCollection<QueryResult>> SharedListing(string user)
        => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query(MeshQueryRequest.FromQuery($"path:{SharedSpace} scope:children") with { UserId = user })
            .Take(1)
            .Should().Within(Budget).Emit($"the listing for {user} must answer",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The outcome of a cold verb: null on success, the fault otherwise.</summary>
    private static Task<Exception?> Outcome<T>(IObservable<T> verb, string because)
        => verb.Take(1).Select(_ => (Exception?)null)
            .Catch((Exception e) => Observable.Return<Exception?>(e))
            .Should().Within(Budget).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    private async Task WaitForCatalog(string user, bool locked)
    {
        var catalog = Mesh.ServiceProvider.GetRequiredService<AccountLockCatalog>();
        await catalog.Register.Where(r => AccountLocks.IsLockedIn(r, user) == locked).Take(1)
            .Should().Within(Budget).Emit($"the register must say {user} is {(locked ? "" : "not ")}locked",
                cancellationToken: TestContext.Current.CancellationToken);
        catalog.IsLocked(user).Should().Be(locked);
    }

    [Fact]
    public async Task ALockedAccount_HoldsNothing_AndAnUnlock_GivesItBack()
    {
        // Before: both people edit their homes and the shared space, and both list the shared space.
        await Settle(Leaver, Leaver, CanEdit, "the leaver owns their home before the lock");
        await Settle($"{SharedSpace}/Note", Leaver, CanEdit, "the leaver edits the shared space before the lock");
        (await SharedListing(Leaver)).Should().NotBeEmpty("control: the leaver's query lists the shared space");

        var locked = await Outcome(AccountLocks.Lock(Mesh, Identity(PlatformAdmin), Leaver, "left the company"),
            "the platform admin's lock must answer");
        locked.Should().BeNull("a platform administrator may lock a person's account");

        // Locked: nothing anywhere — their own home included — and their queries list nothing.
        (await Settle(Leaver, Leaver, p => p == Permission.None, "the locked person holds nothing on their home"))
            .Should().Be(Permission.None);
        (await Settle($"{SharedSpace}/Note", Leaver, p => p == Permission.None,
            "the locked person holds nothing on the shared space")).Should().Be(Permission.None);
        (await Mesh.IsGlobalAdmin(Leaver).TakeDecisionOutsideGate()
                .Should().Within(Budget).Emit("the admin predicate must answer",
                    cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse();
        await WaitForCatalog(Leaver, locked: true);
        (await SharedListing(Leaver)).Should().BeEmpty("a locked person's query answers nothing, on every backend");

        // Negative control: the bystander is untouched.
        await Settle(Stayer, Stayer, CanEdit, "the bystander still owns their home");
        await Settle($"{SharedSpace}/Note", Stayer, CanEdit, "the bystander still edits the shared space");
        (await SharedListing(Stayer)).Should().NotBeEmpty("the bystander's query still lists the shared space");

        // The record is kept and the data is untouched: unlocking gives the account back as it was.
        var unlocked = await Outcome(AccountLocks.Unlock(Mesh, Identity(PlatformAdmin), Leaver),
            "the platform admin's unlock must answer");
        unlocked.Should().BeNull("a platform administrator may lift a lock");
        await Settle(Leaver, Leaver, CanEdit, "after the unlock the person owns their home again");
        await Settle($"{SharedSpace}/Note", Leaver, CanEdit, "after the unlock the person edits the shared space again");
        await WaitForCatalog(Leaver, locked: false);
        (await SharedListing(Leaver)).Should().NotBeEmpty("after the unlock the person's query lists again");
    }

    /// <summary>
    /// The query BOUNDARY answers a locked viewer itself — one empty Initial read from NO partition —
    /// rather than handing the read to a provider. On the in-process provider the fold would filter
    /// the rows anyway; the SQL-backed providers filter in the database against projected grants and
    /// never ask the fold, so the boundary is the only thing standing between them and a locked
    /// person's listing. "Read from no partition" is what tells the boundary's answer apart.
    /// </summary>
    [Fact]
    public async Task ALockedViewersQuery_IsAnsweredAtTheBoundary_FromNoPartition()
    {
        var locked = await Outcome(AccountLocks.Lock(Mesh, Identity(PlatformAdmin), Leaver, "left"),
            "the lock must answer");
        locked.Should().BeNull();
        await WaitForCatalog(Leaver, locked: true);

        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var request = MeshQueryRequest.FromQuery($"path:{SharedSpace} scope:children");
        var refused = await mesh.Query<MeshNode>(request with { UserId = Leaver }).Take(1)
            .Should().Within(Budget).Emit("the locked viewer's query must answer",
                cancellationToken: TestContext.Current.CancellationToken);
        refused.Items.Should().BeEmpty();
        refused.Partitions.Should().NotBeNull().And.BeEmpty(
            "the boundary answers a locked viewer itself, reading no partition");

        var served = await mesh.Query<MeshNode>(request with { UserId = Stayer }).Take(1)
            .Should().Within(Budget).Emit("the bystander's query must answer",
                cancellationToken: TestContext.Current.CancellationToken);
        served.Items.Should().NotBeEmpty("control: the bystander's read reaches the provider and lists the space");
    }

    [Fact]
    public async Task ANonAdmin_CannotLockAnyone_AndNothingIsWritten()
    {
        var refused = await Outcome(AccountLocks.Lock(Mesh, Identity(Stayer), Leaver, "no reason"),
            "the non-admin's lock must answer");
        refused.Should().BeOfType<UnauthorizedAccessException>("only a platform administrator may lock an account");

        var noCaller = await Outcome(AccountLocks.Lock(Mesh, null, Leaver, "no caller"),
            "the caller-less lock must answer");
        noCaller.Should().BeOfType<UnauthorizedAccessException>("a lock without a caller is never anyone's");

        await WaitForCatalog(Leaver, locked: false);
        await Settle(Leaver, Leaver, CanEdit, "the refused lock left the person's access as it was");
    }

    [Fact]
    public async Task AnAdministrator_CannotBeLocked_NorLockThemselves()
    {
        var other = await Outcome(AccountLocks.Lock(Mesh, Identity(PlatformAdmin), OtherAdmin, "x"),
            "locking another administrator must answer");
        other.Should().BeOfType<InvalidOperationException>(
            "an administrator's Admin grant is removed by another administrator first");

        var self = await Outcome(AccountLocks.Lock(Mesh, Identity(PlatformAdmin), PlatformAdmin, "x"),
            "locking oneself must answer");
        self.Should().BeOfType<InvalidOperationException>("no one locks their own account");

        var reserved = await Outcome(AccountLocks.Lock(Mesh, Identity(PlatformAdmin), WellKnownUsers.Anonymous, "x"),
            "locking a reserved principal must answer");
        reserved.Should().BeOfType<InvalidOperationException>("only a person's account can be locked");

        await Mesh.IsGlobalAdmin(OtherAdmin).Should().Within(Budget).Match(a => a,
            "the other administrator is still one", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnlockingAnAccountThatIsNotLocked_WritesNothing()
    {
        var outcome = await Outcome(AccountLocks.Unlock(Mesh, Identity(PlatformAdmin), Stayer),
            "the unlock must answer");
        outcome.Should().BeOfType<InvalidOperationException>("there is no lock on the bystander to lift");
        await WaitForCatalog(Stayer, locked: false);
    }
}
