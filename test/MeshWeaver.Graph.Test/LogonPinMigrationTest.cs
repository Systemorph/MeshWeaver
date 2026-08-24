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
    // 🚨 THE REAL DECLARATION — the exact lists the memex node carries, not placeholders. Using the
    // production paths is what makes these tests say something about the shipped migration rather
    // than about a fixture: this test mesh carries NEITHER the doc sections nor the courses, so it
    // IS a portal that lacks the targets, and A_deployment_without_the_target_nodes_pins_nothing
    // exercises the systemorph.com case verbatim.

    /// <summary>The four documentation sections <c>UserOnboardingService</c> seeds for every new
    /// user — the pre-migration state this job replaces.</summary>
    private static readonly string[] DocPins =
        ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"];

    /// <summary>
    /// The three agentic courses, at their REAL paths.
    ///
    /// <para>🚨 <b>Top-level and bare — there is no <c>Store/</c> prefix.</b> Their
    /// <c>nodeType</c> is <c>Store/Plugin</c>, and a type name that looks like a path segment is a
    /// live trap: <c>Store/AgenticPrimer</c> does not resolve, so a declaration written that way
    /// silently pins NOTHING (the existence check does its job, and the migration records itself as
    /// done having achieved nothing). Verified against the mesh: on each of these,
    /// <c>path == id == mainNode</c>.</para>
    /// </summary>
    private static readonly string[] CoursePins =
        ["AgenticPrimer", "AgenticEngineering", "AgenticBusiness"];

    [Fact(Timeout = 60000)]
    public async Task The_migration_swaps_the_doc_pins_for_the_courses_exactly_once()
    {
        // A portal that DOES carry the courses — i.e. memex. Seeded at the real paths, so the
        // declaration under test is byte-for-byte the one the Admin node carries.
        const string user = "pinuser";
        foreach (var course in CoursePins)
            await CreateCourseAsync(course);
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("docs-to-courses", DocPins, CoursePins);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        var identity = IdentityFor(user);

        await RunAsync(runner, identity, action);
        var afterFirst = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        afterFirst.PinnedPaths.Should().Equal(CoursePins,
            "the four doc sections are unpinned and the three courses pinned, in declaration order");

        // The user then curates their own home — and a second logon must leave that alone.
        await UpdateProfileAsync(user, u => u with { PinnedPaths = [.. u.PinnedPaths, "something/i/pinned"] });
        await RunAsync(runner, identity, action);
        var afterSecond = await AwaitProfileAsync(user, u => u.PinnedPaths.Contains("something/i/pinned"));

        afterSecond.PinnedPaths.Should().Equal(
            new[] { "AgenticPrimer", "AgenticEngineering", "AgenticBusiness", "something/i/pinned" },
            "a run-once migration never looks at the user again, so their own curation survives");
    }

    [Fact(Timeout = 60000)]
    public async Task A_deployment_without_the_target_nodes_pins_nothing()
    {
        // 🚨 THE systemorph.com CASE, VERBATIM — the REAL declaration, on a mesh that carries none
        // of the three courses (this test seeds no course nodes, which is exactly that portal's
        // state). Nothing is substituted or stubbed: same action id, same unpin list, same pin list
        // as the node on memex.
        const string user = "pinuser-nocourses";
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("docs-to-courses", DocPins, CoursePins);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await RunAsync(runner, identity: IdentityFor(user), action);
        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        profile.PinnedPaths.Should().BeEmpty(
            "the unpins still apply, but a course this deployment does not carry is never pinned");
        foreach (var course in CoursePins)
            profile.PinnedPaths.Should().NotContain(course,
                "a dangling pin renders as a dead tile on every user's home");
    }

    [Fact(Timeout = 60000)]
    public async Task A_partially_present_target_set_pins_only_what_exists()
    {
        // The partly-migrated portal: it carries the Primer but not the other two — which is also
        // what a portal looks like MID-INSTALL, and the shape a typo'd path produces.
        const string user = "pinuser-partial";
        await CreateCourseAsync("AgenticPrimer");
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("docs-to-courses", DocPins, CoursePins);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await RunAsync(runner, IdentityFor(user), action);
        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        profile.PinnedPaths.Should().Equal(new[] { "AgenticPrimer" },
            "a missing target is skipped without taking the present ones down with it");
    }

    [Fact(Timeout = 60000)]
    public async Task Only_exact_path_matches_are_pinned_never_descendants()
    {
        // 🚨 The risk the SINGLE alternation query introduces. `path:a|b|c` is a match expression,
        // so a row for a DESCENDANT of a declared target comes back in the same result set. Reading
        // "the query returned something" as "the target exists" would pin a path that does not — the
        // dangling tile the existence check exists to prevent, arrived at from the other direction.
        // The intersection is therefore on EXACT path equality, and this pins that.
        const string user = "pinuser-descendants";
        await CreateCourseAsync("AgenticPrimer");
        await CreateChildAsync("AgenticPrimer", "Introduction");
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var action = Migration("docs-to-courses", DocPins, CoursePins);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await RunAsync(runner, IdentityFor(user), action);
        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(action.Id));

        profile.PinnedPaths.Should().Equal(new[] { "AgenticPrimer" },
            "only the declared path itself counts — a child of it is not the target, and the two "
            + "absent courses are still absent");
        profile.PinnedPaths.Should().NotContain("AgenticPrimer/Introduction");
    }

    [Fact(Timeout = 60000)]
    public async Task A_declaration_NODE_in_the_admin_partition_is_discovered_and_run()
    {
        // The deployment-specific route end to end: no code, no image roll — an admin creates a
        // LogonAction node and every user picks it up on their next logon.
        // The node created here is the SAME id, shape and target lists as the one that goes to
        // Admin/_LogonAction/docs-to-courses on memex — so this test is the rehearsal of the actual
        // production create, run against a mesh that carries the courses.
        const string user = "pinuser-declared";
        const string actionId = "docs-to-courses";
        foreach (var course in CoursePins)
            await CreateCourseAsync(course);
        await CreateUserAsync(user, new User { PinnedPaths = DocPins });

        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(MeshNode.FromPath(LogonActionNodeType.PathFor(actionId)) with
        {
            NodeType = LogonActionNodeType.NodeType,
            Name = "Swap the documentation pins for the courses",
            State = MeshNodeState.Active,
            Content = new LogonAction
            {
                Description = "Existing users pinned the four documentation sections at onboarding; "
                              + "pin the three agentic courses instead, once per user.",
                Mode = LogonActionMode.RunOnce,
                UnpinPaths = DocPins,
                PinPaths = CoursePins,
            },
        }).FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();

        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        await runner.RunFor(IdentityFor(user)).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var profile = await AwaitProfileAsync(user, u => u.CompletedLogonActions.ContainsKey(actionId));
        profile.PinnedPaths.Should().Equal(CoursePins);
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
        var action = Migration("docs-to-courses", DocPins, CoursePins);
        var user = new User { PinnedPaths = ["Doc/GUI", "mine/keep", "Doc/AI"] };

        var once = action.Apply(user, CoursePins);
        once.PinnedPaths.Should().Equal(
            new[] { "mine/keep", "AgenticPrimer", "AgenticEngineering", "AgenticBusiness" },
            "declared unpins are removed, the user's own pins keep their place, new pins append");

        var twice = action.Apply(once, CoursePins);
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

    /// <summary>A child node inside an existing course partition — used to prove a descendant row
    /// never stands in for its ancestor in the alternation query's result set.</summary>
    private async Task CreateChildAsync(string parent, string id)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(MeshNode.FromPath($"{parent}/{id}") with
        {
            NodeType = "Markdown",
            Name = id,
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
