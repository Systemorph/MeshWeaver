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
