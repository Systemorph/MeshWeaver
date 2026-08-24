using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The CONCRETE job the framework was built for: on each existing user's next logon, drop the
/// documentation pins and pin the deployment's courses instead — once, and never again.
///
/// <para>🚨 The targets are DATA, not constants in core, and these tests pin why. The same
/// declaration reaches every portal, but only some carry the content it names: a deployment without
/// the courses must pin NOTHING rather than write a dangling path onto every user's home. That is
/// <see cref="A_deployment_without_the_target_nodes_pins_nothing"/>, and it is the systemorph.com
/// case.</para>
/// </summary>
public class LogonPinMigrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The four documentation sections UserOnboardingService seeds for every new user — the
    // pre-migration state this job replaces.
    private static readonly string[] DocPins =
        ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"];

    private const string CourseA = "TestCourseA";
    private const string CourseB = "TestCourseB";
    private const string AbsentCourse = "CourseThisDeploymentDoesNotHave";

    [Fact(Timeout = 60000)]
    public async Task The_migration_swaps_the_doc_pins_for_the_courses_exactly_once()
    {
        const string user = "pinuser";
        await CreateCourseAsync(CourseA);
        await CreateCourseAsync(CourseB);
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("pins.docs-to-courses", DocPins, [CourseA, CourseB]);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        var identity = IdentityFor(user);

        await RunAsync(runner, identity, action);
        var afterFirst = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        afterFirst.PinnedPaths.Should().Equal(new[] { CourseA, CourseB },
            "the docs are unpinned and the courses pinned, in the order the declaration lists them");

        // The user then curates their own home — and a second logon must leave that alone.
        await UpdateProfileAsync(user, u => u with { PinnedPaths = [.. u.PinnedPaths, "something/i/pinned"] });
        await RunAsync(runner, identity, action);
        var afterSecond = await AwaitProfileAsync(user, u => u.PinnedPaths.Contains("something/i/pinned"));

        afterSecond.PinnedPaths.Should().Equal(new[] { CourseA, CourseB, "something/i/pinned" },
            "a run-once migration never looks at the user again, so their own curation survives");
    }

    [Fact(Timeout = 60000)]
    public async Task A_deployment_without_the_target_nodes_pins_nothing()
    {
        // The systemorph.com case: the declaration names courses this portal does not carry.
        const string user = "pinuser-nocourses";
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("pins.absent-targets", DocPins, [AbsentCourse]);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await RunAsync(runner, identity: IdentityFor(user), action);
        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        profile.PinnedPaths.Should().BeEmpty(
            "the unpins still apply, but a path this deployment does not have is never pinned");
        profile.PinnedPaths.Should().NotContain(AbsentCourse);
    }

    [Fact(Timeout = 60000)]
    public async Task A_partially_present_target_set_pins_only_what_exists()
    {
        const string user = "pinuser-partial";
        await CreateCourseAsync(CourseA);
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("pins.partial", DocPins, [CourseA, AbsentCourse]);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await RunAsync(runner, IdentityFor(user), action);
        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        profile.PinnedPaths.Should().Equal(new[] { CourseA },
            "a missing target is skipped without taking the present ones down with it");
    }

    [Fact(Timeout = 60000)]
    public async Task A_declaration_NODE_in_the_admin_partition_is_discovered_and_run()
    {
        // The deployment-specific route end to end: no code, no image roll — an admin creates a
        // LogonAction node and every user picks it up on their next logon.
        const string user = "pinuser-declared";
        const string actionId = "declared-course-pins";
        await CreateCourseAsync(CourseA);
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(MeshNode.FromPath(LogonActionNodeType.PathFor(actionId)) with
        {
            NodeType = LogonActionNodeType.NodeType,
            Name = "Docs to courses",
            State = MeshNodeState.Active,
            Content = new LogonAction
            {
                Description = "Replace the documentation pins with the courses",
                Mode = LogonActionMode.RunOnce,
                UnpinPaths = DocPins,
                PinPaths = [CourseA],
            },
        }).FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();

        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        await runner.RunFor(IdentityFor(user)).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(actionId));
        profile.PinnedPaths.Should().Equal(CourseA);
    }

    [Fact(Timeout = 60000)]
    public async Task A_portal_that_declares_no_actions_is_a_clean_no_op()
    {
        // The default state of every deployment that never opts in. It must not throw, and it must
        // not touch the profile.
        const string user = "pinuser-untouched";
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        await runner.RunFor(IdentityFor(user)).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var profile = await ReadProfileAsync(user);
        profile.PinnedPaths.Should().Equal(DocPins);
    }

    [Fact]
    public void The_pure_transform_preserves_order_and_never_duplicates()
    {
        // The lambda the runner may re-invoke on a rebase: it has to be pure and convergent.
        var action = Migration("pins.pure", DocPins, [CourseA, CourseB]);
        var user = new User { PinnedPaths = ["Doc/GUI", "mine/keep", "Doc/AI"] };

        var once = action.Apply(user, [CourseA, CourseB]);
        once.PinnedPaths.Should().Equal(new[] { "mine/keep", CourseA, CourseB },
            "declared unpins are removed, the user's own pins keep their place, new pins append");

        var twice = action.Apply(once, [CourseA, CourseB]);
        ReferenceEquals(twice, once).Should().BeTrue(
            "re-applying is a no-op and returns the SAME instance, which is how the runner decides not to write");
    }

    // ---------------------------------------------------------------- helpers

    private static PinMigrationLogonAction Migration(string id, string[] unpin, string[] pin) =>
        new(id, new LogonAction
        {
            Mode = LogonActionMode.RunOnce,
            UnpinPaths = unpin,
            PinPaths = pin,
        });

    /// <summary>Runs one action through the real runner by registering nothing — the runner takes
    /// its actions from DI and the Admin partition, so a test-constructed action is driven through
    /// the same commit path via <see cref="LogonActionRunner.RunFor"/> after registration.</summary>
    private static Task RunAsync(LogonActionRunner runner, AccessContext identity, PinMigrationLogonAction action) =>
        runner.RunFor(identity, [action]).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

    private static AccessContext IdentityFor(string userPath) =>
        new() { ObjectId = userPath, Name = userPath, Email = $"{userPath}@meshweaver.io" };

    private async Task CreateCourseAsync(string path)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(MeshNode.FromPath(path) with
        {
            // 🚨 A top-level node must be a PARTITION-OWNING type (PartitionWriteGuardValidator):
            // the root namespace is reserved for partition roots. Real courses are exactly that —
            // top-level partitions installed from the Store — so Space is also the realistic shape.
            NodeType = "Space",
            Name = path,
            State = MeshNodeState.Active,
        }).FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();
    }

    /// <summary>Creates the user's partition root as onboarding does — as System, because
    /// UserNodeType's access rule reserves creating a User node to the platform.</summary>
    private async Task CreateUserAsync(string path, User content)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
        {
            NodeType = "User",
            Name = path,
            State = MeshNodeState.Active,
            Content = content,
        })).FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();
    }

    private Task UpdateProfileAsync(string path, Func<User, User> change) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(node => node.ContentAs<User>(Mesh.JsonSerializerOptions) is { } u
                ? node with { Content = change(u) }
                : node)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();

    private async Task<User> ReadProfileAsync(string path)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();
        return node.ContentAs<User>(Mesh.JsonSerializerOptions)!;
    }

    private async Task<User> AwaitProfileAsync(string path, Func<User, bool> predicate)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<User>(Mesh.JsonSerializerOptions) is { } u && predicate(u))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        return node.ContentAs<User>(Mesh.JsonSerializerOptions)!;
    }
}
