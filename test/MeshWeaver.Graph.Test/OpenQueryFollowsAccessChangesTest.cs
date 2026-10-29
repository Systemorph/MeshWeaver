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
/// MeshWeaver#7365: an OPEN secured query (a live listing) follows grant changes, measured on the
/// in-memory query executor (<c>StorageAdapterMeshQueryProvider</c>) of a real monolith mesh. The
/// Postgres executor runs the same four scenarios in MeshWeaver.Plugins
/// (<c>OpenQueryFollowsAccessChangesPgTests</c>).
///
/// <para>Every grant sits on the partition root <c>t</c>, ABOVE the query's scope (<c>namespace:t/lib</c>).
/// A grant there is not a write under the query's scope, so before the fix nothing re-ran the query:
/// a revoked reader kept every row it held, and a new grant admitted nothing until the reader re-queried.</para>
/// </summary>
public class OpenQueryFollowsAccessChangesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "t";
    private const string Library = "t/lib";
    private const string Query = "namespace:t/lib nodeType:Markdown";

    private static readonly AccessContext Grace = new() { ObjectId = "grace", Name = "grace" };
    private static readonly AccessContext Henry = new() { ObjectId = "henry", Name = "henry" };
    private static readonly AccessContext Ivy = new() { ObjectId = "ivy", Name = "ivy" };

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .ConfigureServices(services => services.AddSingleton<INodeValidator>(new VerdictlessReadValidator()));

    /// <summary>
    /// A Read validator that reaches NO verdict on two named rows: on <c>silent</c> it completes without
    /// answering, on <c>faulty</c> it faults. On every other row it admits and leaves the decision to
    /// row-level security.
    /// </summary>
    private sealed class VerdictlessReadValidator : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Read];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => context.Node.Id switch
            {
                "silent" => Observable.Empty<NodeValidationResult>(),
                "faulty" => Observable.Throw<NodeValidationResult>(new InvalidOperationException("this read check cannot answer")),
                _ => Observable.Return(NodeValidationResult.Valid()),
            };
    }

    /// <summary>
    /// The re-check FAILS CLOSED: a row whose Read check reaches no verdict is withheld, never shown.
    /// Before the fix, a validator that answered nothing admitted the row (<c>.All</c> over an empty
    /// answer is true), and a validator that faulted ended the whole query.
    /// NEGATIVE CONTROL: the row the same validator admits is listed.
    /// </summary>
    [HubFact]
    public async Task AReadCheckWithNoVerdict_WithholdsTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        await Grant(Grace, ct);
        var doc1 = await AsSystem(Doc("doc1"), ct);
        var silent = await AsSystem(Doc("silent"), ct);
        var faulty = await AsSystem(Doc("faulty"), ct);

        var grace = Live(Grace);
        using var openGrace = grace.Connect();
        var initial = await grace.Should().Within(TestTimeouts.Convergence)
            .Match(view => view.Frames > 0, "grace's open query answers", cancellationToken: ct);
        Assert.DoesNotContain(silent, initial.Rows);
        Assert.DoesNotContain(faulty, initial.Rows);
        Assert.Equal([doc1], initial.Rows);
    }

    /// <summary>
    /// A revoked reader stops receiving rows: once grace's grant is deleted, a row inserted afterwards
    /// never reaches her open query. The revocation is applied by the query ITSELF, before any later
    /// write: the first frame grace receives after it is the removal of what she held. Before the fix
    /// nothing re-decided her query, so that frame never came.
    /// POSITIVE CONTROL: henry, whose grant stands, receives the inserted row on his open query.
    /// </summary>
    [HubFact]
    public async Task ARevokedReader_StopsReceivingNewRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        var graceGrant = await Grant(Grace, ct);
        await Grant(Henry, ct);
        var doc1 = await AsSystem(Doc("doc1"), ct);

        var grace = Live(Grace);
        var henry = Live(Henry);
        using var openGrace = grace.Connect();
        using var openHenry = henry.Connect();
        await Holds(grace, [doc1], "grace's grant on the partition admits the row", ct);
        await Holds(henry, [doc1], "henry's grant on the partition admits the row", ct);
        var framesBeforeRevocation = (await Latest(grace, ct)).Frames;

        await Revoke(graceGrant, ct);
        await WaitForRead(Grace.ObjectId, Library, granted: false, ct);
        var withdrawn = await grace.Should().Within(TestTimeouts.Convergence)
            .Match(view => view.Frames > framesBeforeRevocation,
                "the revocation itself re-decides grace's open query", cancellationToken: ct);
        Assert.Equal(QueryChangeType.Removed, withdrawn.Last);
        Assert.Empty(withdrawn.Rows);

        var doc2 = await AsSystem(Doc("doc2"), ct);
        await Holds(henry, [doc1, doc2], "henry's standing grant admits the row inserted after grace's revocation", ct);
        // The replay holds every view grace was ever handed, so this reads her whole history.
        await grace.Where(view => view.Rows.Contains(doc2))
            .Should().NotEmit(TimeSpan.FromMilliseconds(500), "a row inserted after grace's revocation must never reach her", cancellationToken: ct);
    }

    /// <summary>
    /// A revoked reader's existing rows disappear from the next snapshot: deleting grace's grant on the
    /// partition re-runs her open query and removes the row she can no longer read. Before the fix she
    /// kept it for the life of the query.
    /// NEGATIVE CONTROL: henry's open query, on the same rows, keeps its row through grace's revocation.
    /// </summary>
    [HubFact]
    public async Task ARevokedReader_LosesTheRowsSheHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        var graceGrant = await Grant(Grace, ct);
        await Grant(Henry, ct);
        var doc1 = await AsSystem(Doc("doc1"), ct);
        var doc2 = await AsSystem(Doc("doc2"), ct);

        var grace = Live(Grace);
        var henry = Live(Henry);
        using var openGrace = grace.Connect();
        using var openHenry = henry.Connect();
        await Holds(grace, [doc1, doc2], "grace's grant admits both rows", ct);
        await Holds(henry, [doc1, doc2], "henry's grant admits both rows", ct);

        await Revoke(graceGrant, ct);
        await Holds(grace, [], "the revocation removes every row grace held from her open query", ct);

        await henry.Where(view => !view.Rows.SetEquals([doc1, doc2]))
            .Should().NotEmit(TimeSpan.FromMilliseconds(500), "henry's rows do not move when grace's grant goes", cancellationToken: ct);
    }

    /// <summary>
    /// A newly granted reader starts seeing rows: ivy's open query starts empty, and a grant on the
    /// partition admits the row on her query's next emission, with no write under the query's scope.
    /// Before the fix her query stayed empty until she re-queried.
    /// </summary>
    [HubFact]
    public async Task ANewlyGrantedReader_StartsSeeingRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        var doc1 = await AsSystem(Doc("doc1"), ct);

        var ivy = Live(Ivy);
        using var openIvy = ivy.Connect();
        await Holds(ivy, [], "ivy holds no grant, so her open query starts empty", ct);

        await Grant(Ivy, ct);
        var admitted = await ivy.Should().Within(TestTimeouts.Convergence)
            .Match(view => view.Rows.Contains(doc1), "the grant admits the row on ivy's next emission", cancellationToken: ct);
        Assert.Equal(QueryChangeType.Added, admitted.Last);
    }

    /// <summary>
    /// NEGATIVE CONTROL: a grant change that concerns somebody else does not alter grace's result. The
    /// grant to ivy lands on the very scope grace's query is decided on, so it re-decides grace's query
    /// too; her rows stay exactly as they were, and her query emits nothing. So that this is not a
    /// control that passes because nothing ran, the same grant must reach ivy's open query and admit
    /// the row there — which before the fix it never did.
    /// </summary>
    [HubFact]
    public async Task AnUnrelatedGrantChange_LeavesTheResultAsItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLibrary(ct);
        await Grant(Grace, ct);
        var doc1 = await AsSystem(Doc("doc1"), ct);

        var grace = Live(Grace);
        var ivy = Live(Ivy);
        using var openGrace = grace.Connect();
        using var openIvy = ivy.Connect();
        await Holds(grace, [doc1], "grace's grant admits the row", ct);
        await Holds(ivy, [], "ivy holds no grant", ct);
        var before = await Latest(grace, ct);

        await Grant(Ivy, ct);
        await Holds(ivy, [doc1], "the grant to ivy admits the row on her open query", ct);

        await grace.Where(view => view.Frames > before.Frames)
            .Should().NotEmit(TimeSpan.FromMilliseconds(500), "a grant to somebody else leaves grace's result as it was", cancellationToken: ct);
        Assert.Equal([doc1], (await Latest(grace, ct)).Rows);
    }

    // ───────────────────────────── helpers ─────────────────────────────

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
        await AsSystem(new MeshNode("lib", Partition) { NodeType = "Markdown", Name = "lib" }, ct);
    }

    private static MeshNode Doc(string id) => new(id, Library) { NodeType = "Markdown", Name = id };

    private async Task<string> Grant(AccessContext reader, CancellationToken ct)
        => (await AsSystemNode(AssignmentNodeFactory.UserRole(reader.ObjectId, "Viewer", Partition), ct)).Path;

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

    private async Task<string> AsSystem(MeshNode node, CancellationToken ct)
        => (await AsSystemNode(node, ct)).Path;

    private async Task<MeshNode> AsSystemNode(MeshNode node, CancellationToken ct)
        => (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(node)).Should().Emit(cancellationToken: ct))!;
}
