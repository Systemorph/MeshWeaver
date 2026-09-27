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
/// Every newly onboarded user — the instance's first administrator and an ordinary invited user alike
/// — gets the Inbox app (<c>{user}/_App/Inbox</c>), seeded by <see cref="SeedInboxAppLogonAction"/>
/// through the real logon-action runner, idempotently, independently of the deployment's
/// <c>Admin/HomeConfig.DefaultApps</c> list. Plus the page's own shape (<see cref="InboxLayoutArea"/>):
/// every leg anchored on the owner, bands in reading order, a query listed once.
/// </summary>
public class InboxAppTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FirstAdmin = "inbox-first-admin";
    private const string Invited = "inbox-invited";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            // The first administrator holds the canonical platform-admin grant; the invited user none.
            AssignmentNodeFactory.UserRole(FirstAdmin, "Admin", AdminAppNodeType.Path));

    [Theory(Timeout = 60000)]
    [InlineData(FirstAdmin)]
    [InlineData(Invited)]
    public async Task AFreshlyOnboardedUser_HasTheInboxApp(string userId)
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateUserAsync(userId, ct);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        var identity = new AccessContext { ObjectId = userId, Name = userId, Email = $"{userId}@meshweaver.io" };

        // The whole registered set, as a real sign-in runs it — twice: the second run must not
        // duplicate or fault (the ledger skips it; the create-if-absent makes it harmless anyway).
        await runner.RunFor(identity).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await runner.RunFor(identity).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var record = await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{userId}/{AppNodeType.UserNamespace}/{InboxLayoutArea.AreaName}")
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        record.NodeType.Should().Be(AppNodeType.NodeType);
        record.Name.Should().Be("Inbox");
        record.MainNode.Should().Be($"{userId}/{InboxLayoutArea.AreaName}", "the tile opens the Inbox area on the owner's own hub");
        record.ContentAs<App>(Mesh.JsonSerializerOptions)!.OpenPath.Should().Be($"{userId}/{InboxLayoutArea.AreaName}");

        var profile = await Mesh.GetWorkspace().GetMeshNodeStream(userId)
            .Select(n => n?.ContentAs<User>(Mesh.JsonSerializerOptions))
            .Where(u => u is not null && u.CompletedLogonActions.ContainsKey("seed-inbox-app"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        profile!.CompletedLogonActions.Keys.Should().Contain("seed-inbox-app", "the seed is recorded, so it never re-runs");
    }

    /// <summary>Create-if-absent: running the action itself twice leaves one record and no fault.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheSeed_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        const string userId = "inbox-idempotent";
        await CreateUserAsync(userId, ct);
        var action = new SeedInboxAppLogonAction();
        var context = new LogonActionContext(userId, new AccessContext { ObjectId = userId, Name = userId }, Mesh);

        await action.Run(context).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await action.Run(context).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var records = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{userId}/{AppNodeType.UserNamespace} nodeType:{AppNodeType.NodeType}"))
            .Select(c => c.Items.ToList())
            .Where(items => items.Count > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        records.Count(r => r.Id == InboxLayoutArea.AreaName).Should().Be(1);
    }

    /// <summary>
    /// The tile's label is catalog text, not a stored English string: the record carries the key the
    /// launcher resolves per viewer, and its stored name is seeded in the OWNER's language.
    /// </summary>
    [Fact]
    public void TheTile_IsLocalized()
    {
        var german = UserActivityLayoutAreas.BuildAppRecord("anna", UserActivityLayoutAreas.InboxAppSpec("anna", "de"));
        german.Name.Should().Be("Posteingang");
        german.ContentAs<App>(Mesh.JsonSerializerOptions)!.LabelKey.Should().Be("inbox.title");

        UserActivityLayoutAreas.BuildAppRecord("bob", UserActivityLayoutAreas.InboxAppSpec("bob", "en")).Name
            .Should().Be("Inbox");
    }

    [Fact]
    public void TheSeed_IsARunOnceActionWithAStableKey()
    {
        var action = new SeedInboxAppLogonAction();
        action.Mode.Should().Be(LogonActionMode.RunOnce);
        action.Id.Should().Be("seed-inbox-app", "the id IS the ledger key; renaming it re-seeds for everyone");
    }

    [Fact]
    public void ThePage_AnchorsEveryLegOnTheOwner_InBandOrder_EachQueryOnce()
    {
        var sections = InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice");

        sections.Should().NotBeEmpty();
        sections.Should().OnlyContain(s => s.Query.Contains("namespace:alice/"), "every leg is anchored on the owner");
        sections.Select(s => s.Query).Should().OnlyHaveUniqueItems("a row listed twice on one page is noise");
        var ranks = sections.Select(s => Rank(s.Band)).ToList();
        ranks.Should().Equal(ranks.OrderBy(r => r).ToList(), "bands read Needs you → Running → Recent");
    }

    /// <summary>
    /// All three bands render: Running and Recent are DIFFERENT queries (non-terminal vs stamped
    /// terminal activity), so the per-page dedup no longer swallows Recent.
    /// </summary>
    [Fact]
    public void ThePage_ShowsThreeDistinctBands()
        => InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice").Select(s => s.Band)
            .Should().Equal(InboxBand.NeedsYou, InboxBand.Running, InboxBand.Recent);

    /// <summary>
    /// The two activity bands split a real partition: a running activity (its default status is
    /// OMITTED from stored content) lands in Running only, a finished one in Recent only.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RunningAndRecent_SplitTheOwnersActivities()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-bands";
        await CreateUserAsync(owner, ct);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        MeshNode Activity(string id, ActivityStatus status) => new(id, $"{owner}/_Activity")
        {
            NodeType = ActivityNodeType.NodeType,
            Name = id,
            MainNode = owner,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.Import) { Id = id, HubPath = owner, Status = status },
        };
        foreach (var node in new[] { Activity("still-running", ActivityStatus.Running), Activity("finished", ActivityStatus.Succeeded) })
            await access.RunAsSystem(() => mesh.CreateNode(node)).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var legs = InboxQueries.Resolve(InboxQueries.BuiltIn, owner);
        async Task<string[]> Ids(string band)
        {
            var query = legs.Single(l => l.Band == band).Query;
            return await mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Select(c => c.Items.Select(n => n.Id).ToArray())
                .Where(ids => ids.Length > 0)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        }

        (await Ids(InboxBand.Running)).Should().Equal("still-running");
        (await Ids(InboxBand.Recent)).Should().Equal("finished");
    }

    private static int Rank(string band)
        => InboxLayoutArea.BandOrder.IndexOf(band) is var i and >= 0 ? i : InboxLayoutArea.BandOrder.Length;

    /// <summary>Creates the user's partition root as onboarding does — as System.</summary>
    private async Task CreateUserAsync(string path, CancellationToken ct)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
        {
            NodeType = UserNodeType.NodeType,
            Name = path,
            State = MeshNodeState.Active,
            Content = new User { FullName = path },
        })).FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await(ct);
    }
}

/// <summary>
/// The Inbox seed writes AS THE OWNER even when it is subscribed with a different ambient identity —
/// which is what happens when the runner Concats it after another action and subscribes it on that
/// action's completion thread. The ambient identity here is deliberately an intruder with no grant on
/// the owner's partition: without the <c>RunAs(context.Identity, …)</c> at the write site the create
/// runs as the intruder and is refused (the tile never lands); with it, it lands.
/// </summary>
public class InboxSeedWritesAsTheOwnerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Owner = "inbox-runas-owner";
    private const string Intruder = "inbox-runas-intruder";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    [Fact(Timeout = 60000)]
    public async Task TheCreate_RunsUnderTheLogonIdentity_NotTheAmbientOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(Owner) with
        {
            NodeType = UserNodeType.NodeType,
            Name = Owner,
            State = MeshNodeState.Active,
            Content = new User { FullName = Owner },
        })).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // The owner's own grant is written by the User post-creation handler; wait for it.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{Owner}/_Access/{Owner}_Access")
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // The ambient identity is somebody else — what a completion thread may carry.
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = Intruder, Name = Intruder });
        var context = new LogonActionContext(Owner, new AccessContext { ObjectId = Owner, Name = Owner }, Mesh);

        await new SeedInboxAppLogonAction().Run(context)
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = Owner, Name = Owner });
        var record = await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{Owner}/{AppNodeType.UserNamespace}/{InboxLayoutArea.AreaName}")
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        record.NodeType.Should().Be(AppNodeType.NodeType);
    }
}
