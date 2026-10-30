using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#7365, the DECLARED-row half: an open secured query follows grant changes for a row the
/// mesh serves from configuration (<c>AddMeshNodes</c>, served by <c>StaticNodeQueryProvider</c>), not
/// from storage. #7565 made the storage executor follow grants (<see cref="OpenQueryFollowsAccessChangesTest"/>);
/// the static provider still decided each declared row ONCE — it emitted its <c>Initial</c> and completed —
/// so a revoked reader kept a declared row for the life of the query, and a newly granted reader never saw it.
///
/// <para>The grants sit on the partition root <c>t</c>, above the declared row <c>t/lib/handbook</c>.
/// Storage holds no row at that path, so no change-feed write can ever re-decide it.</para>
/// </summary>
public class OpenQueryFollowsAccessOnDeclaredRowsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "t";
    private const string Library = "t/lib";
    private const string Handbook = "t/lib/handbook";
    private const string Query = "namespace:t/lib nodeType:Markdown";

    private static readonly AccessContext Grace = new() { ObjectId = "grace", Name = "grace" };
    private static readonly AccessContext Henry = new() { ObjectId = "henry", Name = "henry" };
    private static readonly AccessContext Ivy = new() { ObjectId = "ivy", Name = "ivy" };

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(new MeshNode("handbook", Library) { NodeType = "Markdown", Name = "handbook" });

    /// <summary>
    /// A revoked reader loses the declared row on her OPEN query. Before the fix the static provider
    /// had completed after its Initial, so nothing re-decided the row and grace kept it.
    /// NEGATIVE CONTROL: henry, whose grant stands, keeps the row through grace's revocation.
    /// </summary>
    [HubFact]
    public async Task ARevokedReader_LosesTheDeclaredRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        var graceGrant = await Grant(Grace, ct);
        await Grant(Henry, ct);

        var grace = Live(Grace);
        var henry = Live(Henry);
        using var openGrace = grace.Connect();
        using var openHenry = henry.Connect();
        await Holds(grace, [Handbook], "grace's grant on the partition admits the declared row", ct);
        await Holds(henry, [Handbook], "henry's grant on the partition admits the declared row", ct);

        await Revoke(graceGrant, ct);
        await WaitForRead(Grace.ObjectId, Handbook, granted: false, ct);
        var withdrawn = await Holds(grace, [], "the revocation removes the declared row from grace's open query", ct);
        Assert.Equal(QueryChangeType.Removed, withdrawn.Last);

        await henry.Where(view => !view.Rows.SetEquals([Handbook]))
            .Should().NotEmit(TimeSpan.FromMilliseconds(500), "henry's declared row does not move when grace's grant goes", cancellationToken: ct);
    }

    /// <summary>
    /// A newly granted reader starts seeing the declared row on her OPEN query, with no write anywhere
    /// under the query's scope. Before the fix her query stayed empty until she re-queried.
    /// </summary>
    [HubFact]
    public async Task ANewlyGrantedReader_StartsSeeingTheDeclaredRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);

        var ivy = Live(Ivy);
        using var openIvy = ivy.Connect();
        await Holds(ivy, [], "ivy holds no grant, so the declared row is withheld from her open query", ct);

        await Grant(Ivy, ct);
        var admitted = await Holds(ivy, [Handbook], "the grant admits the declared row on ivy's open query", ct);
        Assert.Equal(QueryChangeType.Added, admitted.Last);
    }

    /// <summary>
    /// NEGATIVE CONTROL: a grant to somebody else re-decides grace's query (it lands on the scope her row
    /// is decided on) and leaves her result exactly as it was, with no frame. It is not vacuous: the same
    /// grant must admit the declared row on ivy's open query.
    /// </summary>
    [HubFact]
    public async Task AnUnrelatedGrantChange_LeavesTheDeclaredRowAsItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        await Grant(Grace, ct);

        var grace = Live(Grace);
        var ivy = Live(Ivy);
        using var openGrace = grace.Connect();
        using var openIvy = ivy.Connect();
        await Holds(grace, [Handbook], "grace's grant admits the declared row", ct);
        await Holds(ivy, [], "ivy holds no grant", ct);
        var before = await Latest(grace, ct);

        await Grant(Ivy, ct);
        await Holds(ivy, [Handbook], "the grant to ivy admits the declared row on her open query", ct);

        await grace.Where(view => view.Frames > before.Frames)
            .Should().NotEmit(TimeSpan.FromMilliseconds(500), "a grant to somebody else leaves grace's result as it was", cancellationToken: ct);
    }

    /// <summary>
    /// The SNAPSHOT surface (<see cref="IMeshService.Query(MeshQueryRequest)"/>, every frame the full
    /// current set, "updates until disposed") follows the revocation too, for a STORED row and a
    /// DECLARED one. Its per-provider bridge (<c>IMeshQueryProvider.Query(request, options)</c>) folded
    /// <c>Initial</c>/<c>Added</c>/<c>Updated</c> and dropped every <c>Removed</c>, so a provider's removal
    /// never left the snapshot: a revoked reader kept every row she held there.
    /// NEGATIVE CONTROL: henry's snapshot, through a grant that stands, keeps both rows.
    /// </summary>
    [HubFact]
    public async Task TheSnapshotSurface_DropsTheRowsARevokedReaderHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        var graceGrant = await Grant(Grace, ct);
        await Grant(Henry, ct);
        const string stored = "t/lib/stored";
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("stored", Library) { NodeType = "Markdown", Name = "stored" }))
            .Should().Emit(cancellationToken: ct);

        var grace = Snapshot(Grace);
        var henry = Snapshot(Henry);
        using var openGrace = grace.Connect();
        using var openHenry = henry.Connect();
        await grace.Should().Within(TestTimeouts.Convergence)
            .Match(f => f.Rows.SetEquals([Handbook, stored]), "grace's grant admits the declared and the stored row", cancellationToken: ct);
        // The snapshot is PROGRESSIVE (a fast provider's rows first), so the control reads only the
        // frames henry receives after his full set arrived.
        var henryFull = await henry.Should().Within(TestTimeouts.Convergence)
            .Match(f => f.Rows.SetEquals([Handbook, stored]), "henry's grant admits the declared and the stored row", cancellationToken: ct);

        await Revoke(graceGrant, ct);
        await grace.Should().Within(TestTimeouts.Convergence)
            .Match(f => f.Rows.Count == 0, "the revocation clears grace's open snapshot", cancellationToken: ct);
        await henry.Where(f => f.Frame > henryFull.Frame && !f.Rows.SetEquals([Handbook, stored]))
            .Should().NotEmit(TimeSpan.FromMilliseconds(500), "henry's snapshot does not move when grace's grant goes", cancellationToken: ct);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>The reader's open SNAPSHOT query on the library, as the set of paths per frame, replayed.</summary>
    private IConnectableObservable<(int Frame, ImmutableHashSet<string> Rows)> Snapshot(AccessContext reader)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(reader, () => NodeFactory.Query(MeshQueryRequest.FromQuery(Query) with { UserId = reader.ObjectId }))
            .Select((rows, frame) => (frame, rows.Select(r => r.Path).ToImmutableHashSet(StringComparer.Ordinal)))
            .Replay();

    /// <summary>The reader's view of an open query: the rows it holds, the frames it received, the last frame's kind.</summary>
    private sealed record View(ImmutableSortedSet<string> Rows, int Frames, QueryChangeType? Last)
    {
        public static readonly View Empty = new(ImmutableSortedSet.Create<string>(StringComparer.Ordinal), 0, null);

        public View Apply(QueryResultChange<MeshNode> change)
        {
            var paths = change.Items.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            var rows = change.ChangeType switch
            {
                QueryChangeType.Initial or QueryChangeType.Reset => ImmutableSortedSet.Create(StringComparer.Ordinal, paths),
                QueryChangeType.Added or QueryChangeType.Updated => Rows.Union(paths),
                QueryChangeType.Removed => Rows.Except(paths),
                _ => Rows,
            };
            return new View(rows, Frames + 1, change.ChangeType);
        }
    }

    /// <summary>The reader's open query on the library, folded into views and replayed.</summary>
    private IConnectableObservable<View> Live(AccessContext reader)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(reader, () => NodeFactory.Query<MeshNode>(MeshQueryRequest.FromQuery(Query) with { UserId = reader.ObjectId }))
            .Scan(View.Empty, (view, change) => view.Apply(change))
            .Replay();

    private static Task<View> Holds(IObservable<View> live, string[] rows, string because, CancellationToken ct)
        => live.Should().Within(TestTimeouts.Convergence)
            .Match(view => view.Rows.SetEquals(rows), because, cancellationToken: ct);

    /// <summary>The newest view the replay holds.</summary>
    private static Task<View> Latest(IConnectableObservable<View> live, CancellationToken ct)
        => live.Buffer(TimeSpan.FromMilliseconds(100)).Take(1).Select(views => views[^1])
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private async Task SeedLibrary(CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = "t" });
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("lib", Partition) { NodeType = "Markdown", Name = "lib" }))
            .Should().Emit(cancellationToken: ct);
    }

    private async Task<string> Grant(AccessContext reader, CancellationToken ct)
        => (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(reader.ObjectId, "Viewer", Partition)))
            .Should().Emit(cancellationToken: ct))!.Path;

    private Task Revoke(string grant, CancellationToken ct)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.DeleteNode(grant))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    /// <summary>Waits until the permission fold reports <paramref name="userId"/>'s Read on <paramref name="path"/> as <paramref name="granted"/>.</summary>
    private Task WaitForRead(string userId, string path, bool granted, CancellationToken ct)
        => Mesh.GetEffectivePermissions(path, userId)
            .Where(perms => perms.HasFlag(Permission.Read) == granted)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}
