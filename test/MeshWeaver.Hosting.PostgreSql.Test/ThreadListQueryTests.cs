using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins <see cref="ThreadQueries"/> — the queries every THREAD LIST binds to (the side panel's
/// thread picker, the in-thread navigation menu) — against a REAL PG-backed mesh, because both ways
/// these lists have gone empty are invisible anywhere else.
///
/// <para>🚨 <b>The gap this closes.</b> <c>CrossPartitionThreadQueryTests</c> already pins
/// <c>content.createdBy</c> for the DASHBOARD's thread query, and the chat's own lists — written as
/// separate query strings that no test touched — each carried a different defect and rendered
/// "No threads yet" forever:</para>
/// <list type="number">
///   <item>The side panel's picker scoped itself to <c>namespace:{currentPage}/_Thread</c>. A thread
///     is created under whatever node it was started from, so on any page that never hosted one the
///     query asked for a namespace that holds nothing.</item>
///   <item>The nav menu filtered <c>MeshNode.CreatedBy == me</c> on the client. Threads live in the
///     <c>threads</c> satellite table, whose reads project <c>NULL::text AS created_by</c>
///     (<c>PostgreSqlStorageAdapter.AuthorCols</c>) — so that column is null on EVERY thread and the
///     filter matched nothing, for every signed-in user.</item>
/// </list>
/// <para>Only the second reproduces on PG specifically, which is why the whole set lives here rather
/// than on an in-memory mesh: the null is a property of satellite-table storage, not of the query.</para>
///
/// <para>Each test owns a UNIQUE creator id, so the deliberately unscoped (cross-partition) query
/// these lists issue in production matches only this test's rows — a sibling test writing threads
/// concurrently can neither page them out nor leak into the assertions (#834).</para>
/// </summary>
[Collection("PostgreSql")]
public class ThreadListQueryTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            // Registers the Thread NodeType + satellite routing rule and the AI type-registry
            // entries the polymorphic Thread content needs to round-trip.
            .AddAI();
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>A creator id unique to one test — see the class remarks on why that IS the scoping.</summary>
    private static string NewOwner() => $"tlq_{Guid.NewGuid():N}"[..18].ToLowerInvariant();

    /// <summary>
    /// Creates <paramref name="owner"/>'s partition root and, under <paramref name="contextPath"/>,
    /// a thread in that node's <c>_Thread</c> satellite — the shape
    /// <c>ThreadNodeType.BuildThreadNode</c> produces for a thread started from any page.
    /// </summary>
    private async Task<string> CreateThread(
        string owner, string contextPath, string name, ThreadExecutionStatus status = ThreadExecutionStatus.Idle)
    {
        var id = $"t{Guid.NewGuid():N}"[..10];
        var thread = new MeshNode(id, $"{contextPath}/{ThreadNodeType.ThreadPartition}")
        {
            Name = name,
            NodeType = ThreadNodeType.NodeType,
            MainNode = contextPath,
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = owner, Status = status },
        };
        await MeshService.CreateNode(thread).Should().Within(30.Seconds()).Emit();
        return thread.Path!;
    }

    private async Task CreatePartitionRoot(string owner)
    {
        await MeshService.CreateNode(new MeshNode(owner)
        {
            NodeType = "User",
            Name = owner,
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
    }

    private async Task CreateNode(string id, string ns)
    {
        await MeshService.CreateNode(new MeshNode(id, ns)
        {
            NodeType = "Markdown",
            Name = id,
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
    }

    /// <summary>
    /// Subscribes the query the way the chat lists do and waits for the first snapshot satisfying
    /// <paramref name="until"/> — never a fixed delay. The cache id is per-call so no test inherits
    /// a sibling's snapshot (GetQuery caches by id + caller identity).
    /// </summary>
    private Task<IEnumerable<MeshNode>> Snapshot(
        string id, string query, Func<IEnumerable<MeshNode>, bool> until) =>
        Mesh.GetWorkspace().GetQuery($"test:threadlist:{id}", query)
            .Where(until)
            .Take(1)
            .Should().Within(30.Seconds()).Emit();

    private static IEnumerable<string> Paths(IEnumerable<MeshNode> nodes) =>
        nodes.Where(n => !string.IsNullOrEmpty(n.Path)).Select(n => n.Path!);

    /// <summary>
    /// 🚨 THE SIDE-PANEL BUG. A thread started from an ordinary content page lives under THAT page,
    /// not under the user's home — so "my threads" must not be scoped to any one namespace. The
    /// picker used to scope itself to the page the user happened to be looking at and therefore
    /// listed nothing anywhere else.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task MyThreads_FindsAThreadStartedOnAnyNode()
    {
        var owner = NewOwner();
        using var _ = Access.ImpersonateAsSystem();
        await CreatePartitionRoot(owner);
        await CreateNode("Docs", owner);
        await CreateNode("Page", $"{owner}/Docs");

        var onHome = await CreateThread(owner, owner, "Thread on my home");
        var onPage = await CreateThread(owner, $"{owner}/Docs/Page", "Thread on a doc page");

        var snapshot = await Snapshot(
            $"any-node-{owner}", ThreadQueries.MyThreads(owner), s => Paths(s).Count() >= 2);

        Paths(snapshot).Should().Contain(onHome).And.Contain(onPage,
            "a thread is created under the node it was started from — the list is the USER's, not the page's");
    }

    /// <summary>Someone else's thread is not mine, in any partition.</summary>
    [Fact(Timeout = 120_000)]
    public async Task MyThreads_ExcludesAnotherUsersThread()
    {
        var owner = NewOwner();
        var other = NewOwner();
        using var _ = Access.ImpersonateAsSystem();
        await CreatePartitionRoot(owner);
        await CreatePartitionRoot(other);

        var mine = await CreateThread(owner, owner, "Mine");
        var theirs = await CreateThread(other, other, "Theirs");

        var snapshot = await Snapshot(
            $"others-{owner}", ThreadQueries.MyThreads(owner), s => Paths(s).Contains(mine));

        Paths(snapshot).Should().NotContain(theirs);
    }

    /// <summary>
    /// The nav menu lists OPEN threads: a thread marked done drops out of
    /// <see cref="ThreadQueries.MyOpenThreads"/> while staying in <see cref="ThreadQueries.MyThreads"/>
    /// (the picker can still resume it). Pins the <c>-content.status:Done</c> term — including that the
    /// status enum is matched by NAME in the stored JSON.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task MyOpenThreads_ExcludesDone_WhileMyThreadsKeepsIt()
    {
        var owner = NewOwner();
        using var _ = Access.ImpersonateAsSystem();
        await CreatePartitionRoot(owner);

        var open = await CreateThread(owner, owner, "Still open");
        var done = await CreateThread(owner, owner, "Finished", ThreadExecutionStatus.Done);

        var all = await Snapshot(
            $"all-{owner}", ThreadQueries.MyThreads(owner), s => Paths(s).Count() >= 2);
        Paths(all).Should().Contain(open).And.Contain(done,
            "the picker resumes any of my threads, done or not");

        var openOnly = await Snapshot(
            $"open-{owner}", ThreadQueries.MyOpenThreads(owner), s => Paths(s).Contains(open));
        Paths(openOnly).Should().NotContain(done);
    }

    /// <summary>
    /// 🚨 THE NAV-MENU BUG, pinned at its root: a thread ARRIVES with no envelope authorship, so
    /// ownership can only be decided server-side.
    ///
    /// <para>Authorship columns exist only on <c>mesh_nodes</c> — a satellite read projects
    /// <c>NULL::text AS created_by</c> (<c>PostgreSqlStorageAdapter.AuthorCols</c>), so a thread's
    /// <see cref="MeshNode.CreatedBy"/> is ALWAYS null however it was created. The nav menu asked the
    /// server for every thread and then kept the ones where <c>n.CreatedBy == me</c>: that predicate
    /// is false for every row, for every user, forever.</para>
    ///
    /// <para>The subtlety that makes this so easy to get wrong: the SERVER-side term is fine — the
    /// query layer maps a <c>createdBy:</c> filter onto the stored content, so it does select the
    /// right rows. It is only the projected VALUE that is null. Filter where the data is; never
    /// re-filter a thread list in the client on an envelope field.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ThreadOwnership_ArrivesWithNoEnvelopeAuthorship_SoTheClientCannotFilterOnIt()
    {
        var owner = NewOwner();
        using var _ = Access.ImpersonateAsSystem();
        await CreatePartitionRoot(owner);
        var mine = await CreateThread(owner, owner, "Mine");

        var byContent = await Snapshot(
            $"content-{owner}", ThreadQueries.MyThreads(owner), s => Paths(s).Contains(mine));

        byContent.Single(n => n.Path == mine).CreatedBy.Should().BeNull(
            "satellite reads project NULL::text AS created_by — see PostgreSqlStorageAdapter.AuthorCols");

        // Exactly what the nav menu used to do to the snapshot it had just been handed.
        byContent
            .Where(n => string.Equals(n.CreatedBy, owner, StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty(
                "re-filtering a thread list on MeshNode.CreatedBy drops EVERY row — that is the bug, "
                + "and it is invisible because the server already returned exactly the right ones");
    }
}
