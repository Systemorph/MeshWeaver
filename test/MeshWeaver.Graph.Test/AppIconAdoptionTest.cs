using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The decision an installed-app record makes about its own icon, and the logon action that applies
/// it.
///
/// <para>🚨 The two REJECTED homes for this repair are the point of the test. Inside the home's
/// reactive selector it re-ran per SUBSCRIPTION — every navigation and reconnect — and ran after the
/// ambient access context was cleared, so its query and writes executed with no viewer identity. On
/// the record hub's initialization the storm was fixed but the identity was not: initialization
/// carries no viewer context, so it needed <c>ImpersonateAsSystem</c> to do anything at all, and the
/// platform has no business writing a user's own records as itself. A logon action has a real user
/// identity by construction — which <see cref="It_writes_as_the_user_never_as_system"/> pins,
/// because that is the property most likely to regress silently.</para>
/// </summary>
public class AppIconAdoptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// One mesh for this class instead of one per case — the collection-scoped lifetime from
    /// Doc/Architecture/CollectionScopedTestFixtures. These cases only READ adoption behaviour off
    /// records they build themselves, so they neither depend on a pristine mesh nor leave one
    /// behind; measured green 20/20 shared before this was turned on.
    /// </summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string Generic = "/static/NodeTypeIcons/puzzlepiece.svg";
    private const string Real = "/static/NodeTypeIcons/chess.svg";

    private static MeshNode Record(string? icon, string? mainNode = "Chess") =>
        MeshNode.FromPath("rbuergi/_App/Chess") with
        {
            NodeType = AppNodeType.NodeType,
            Name = "Chess",
            Icon = icon,
            MainNode = mainNode ?? "rbuergi/_App/Chess",
        };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Generic)]
    public void A_record_without_a_real_icon_adopts_the_apps_own(string? current)
    {
        AppIconAdoption.IconToAdopt(Record(current), Real).Should().Be(Real);
    }

    [Fact]
    public void A_record_that_already_has_a_real_icon_is_left_alone()
    {
        // The Store stamping a real icon must win over this repair, always — including when the
        // repair happens to run afterwards.
        AppIconAdoption.IconToAdopt(Record("/covers/my-own.png"), Real).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Generic)]
    public void A_target_with_no_better_icon_changes_nothing(string? targetIcon)
    {
        // Convergence: rewriting the same placeholder would make every logon a write, and the grid
        // would still look identical. Nothing better available ⇒ nothing happens. This is what makes
        // an EveryLogon action affordable.
        AppIconAdoption.IconToAdopt(Record(Generic), targetIcon).Should().BeNull();
    }

    [Fact]
    public void A_record_pointing_at_itself_has_nothing_to_adopt_from()
    {
        // MainNode defaults to the node's own path; a record that never got a real target must not
        // resolve itself and copy its own placeholder back.
        AppIconAdoption.TargetOf(Record(Generic, mainNode: "rbuergi/_App/Chess")).Should().BeNull();
    }

    [Fact]
    public void A_record_with_a_real_target_resolves_it()
    {
        AppIconAdoption.TargetOf(Record(Generic)).Should().Be("Chess");
    }

    [Fact]
    public void The_content_is_the_reliable_target_because_the_create_pipeline_rewrites_MainNode()
    {
        // 🚨 THE PRODUCTION SHAPE, and the reason this action was inert before. A default app record
        // is built with Id = "Chess" and MainNode = "Chess" (the app it opens), and
        // HandleCreateNodeRequest step 1b' re-stamps any non-satellite node whose MainNode == Id to
        // its own full path. So every default record arrives pointing at ITSELF, and MainNode alone
        // resolves to null for exactly the records wearing the generic icon.
        var afterCreatePipeline = Record(Generic, mainNode: "rbuergi/_App/Chess");

        AppIconAdoption.TargetOf(afterCreatePipeline).Should().BeNull(
            "MainNode alone cannot answer for a record the create pipeline has re-stamped");
        AppIconAdoption.TargetOf(afterCreatePipeline, pluginPath: "Chess").Should().Be("Chess",
            "App.Plugin is the app's identity and is untouched by that repair");
    }

    [Fact]
    public void Content_wins_over_MainNode_but_neither_may_be_the_record_itself()
    {
        AppIconAdoption.TargetOf(Record(Generic, mainNode: "Stale"), pluginPath: "Chess")
            .Should().Be("Chess", "the content is authoritative when both are present");
        AppIconAdoption.TargetOf(Record(Generic), pluginPath: "  ")
            .Should().Be("Chess", "blank content falls back to MainNode");
        AppIconAdoption.TargetOf(Record(Generic), pluginPath: "rbuergi/_App/Chess")
            .Should().Be("Chess", "a self-referencing content path is skipped, not adopted from");
    }

    [Fact]
    public void NeedsIcon_is_the_single_definition_of_generic()
    {
        AppIconAdoption.NeedsIcon(Record(null)).Should().BeTrue();
        AppIconAdoption.NeedsIcon(Record(Generic)).Should().BeTrue();
        AppIconAdoption.NeedsIcon(Record(Generic.ToUpperInvariant())).Should().BeTrue(
            "a path differing only in case is still the placeholder");
        AppIconAdoption.NeedsIcon(Record(Real)).Should().BeFalse();
        AppIconAdoption.NeedsIcon(null).Should().BeFalse("a missing node is not a repair target");
    }

    [Fact]
    public void It_is_an_every_logon_action_because_a_new_app_needs_adopting_too()
    {
        // A run-once action would repair whatever was installed the day it first ran, record itself
        // as done, and leave every later install on the placeholder forever.
        new AppIconAdoptionLogonAction().Mode.Should().Be(LogonActionMode.EveryLogon);
    }

    [Fact(Timeout = 60000)]
    public async Task It_writes_as_the_user_never_as_system()
    {
        const string user = "iconuser";
        const string app = "IconTargetApp";
        await CreateAsync(MeshNode.FromPath(app) with
        {
            // Top-level nodes must be partition-owning (PartitionWriteGuardValidator); a real app
            // (a Store plugin, a course) is exactly that.
            NodeType = "Space", Name = "Target", Icon = Real, State = MeshNodeState.Active,
        });
        await CreateAsync(MeshNode.FromPath(user) with
        {
            NodeType = "User", Name = user, State = MeshNodeState.Active, Content = new User(),
        });
        // Built exactly as UserActivityLayoutAreas.BuildAppRecord builds a DEFAULT app record —
        // including MainNode = the app id, which the create pipeline will re-stamp to this node's
        // own path. That re-stamp is why the content has to carry the target too.
        await CreateAsync(MeshNode.FromPath(AppNodeType.PathFor(user, app)) with
        {
            NodeType = AppNodeType.NodeType,
            Name = app,
            Icon = Generic,
            MainNode = app,
            State = MeshNodeState.Active,
            Content = new App { Plugin = app, Source = "default" },
        });

        // Preconditions, not sleeps — and asserted rather than assumed. The action reads BOTH of
        // these through the mesh query index, under the user's own identity; if either is invisible
        // there the action is a legitimate no-op and the real assertion below would fail as an
        // unexplained timeout. Checking them here names which link broke.
        var records = await AwaitQueryableAsync(
            $"path:{user}/{AppNodeType.UserNamespace} scope:children nodeType:{AppNodeType.NodeType}");
        var row = records.Single();
        row.MainNode.Should().Be(AppNodeType.PathFor(user, app),
            "the create pipeline re-stamps a bare-id MainNode to the node's own path — which is "
            + "precisely why the target must come from the content");
        row.ContentAs<App>(Mesh.JsonSerializerOptions)?.Plugin.Should().Be(app,
            "TargetOf reads App.Plugin off the QUERY row; a projection that drops Content would make "
            + "the whole action silently inert");

        var targets = await AwaitQueryableAsync($"path:{app} select:path,id,namespace,name,nodeType,icon");
        targets.Single().Icon.Should().Be(Real, "there is a better icon available to adopt");

        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        await runner.RunFor(
                new AccessContext { ObjectId = user, Name = user },
                [new AppIconAdoptionLogonAction()])
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();

        var record = await Mesh.GetWorkspace().GetMeshNodeStream(AppNodeType.PathFor(user, app))
            .Where(n => n is not null && n.Icon == Real)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();

        record.Icon.Should().Be(Real, "the record adopted the app's icon");
        // 🚨 The assertion that matters: these are the USER's records, and the platform must not
        // write them as itself. A regression to ImpersonateAsSystem shows up here and nowhere else.
        record.LastModifiedBy.Should().NotBe(WellKnownUsers.System);
        record.LastModifiedBy.Should().Be(user);
    }

    /// <summary>Waits until a query answers with at least one row and returns them — the index seam
    /// the action reads through. Re-queries on an interval rather than sleeping, so it completes as
    /// soon as the condition holds (WritingTests.md → "Polling loops around QueryAsync").</summary>
    private Task<IReadOnlyCollection<MeshNode>> AwaitQueryableAsync(string query)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => (IReadOnlyCollection<MeshNode>)c.Items.ToArray())
                .Take(1))
            .Where(items => items.Count > 0)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await();
    }

    /// <summary>Seeds a node as System — the User partition root is reserved to the platform by
    /// UserNodeType's access rule, exactly as in onboarding.</summary>
    private async Task CreateAsync(MeshNode node)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(node))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await();
    }
}
