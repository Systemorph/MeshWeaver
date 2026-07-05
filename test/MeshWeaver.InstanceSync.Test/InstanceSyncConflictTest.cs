using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.InstanceSync.Test;

/// <summary>
/// Conflict-resolution + full round-trip coverage for instance sync. The model is
/// newest-writer-wins (by <see cref="MeshNode.LastModified"/>), applied SYMMETRICALLY on push and
/// pull — so the two instances always CONVERGE on the newest write (no split-brain, no older side
/// silently clobbering a newer one). These tests assert convergence on BOTH sides after a round
/// trip, including the offline "long flight" case (edits accumulate while the remote is unreachable,
/// then drain and converge on reconnect).
/// </summary>
public class InstanceSyncConflictTest(ITestOutputHelper output) : InstanceSyncTestBase(output)
{
    private const string Space = "conflict";
    private const string Doc = "conflict/doc";

    /// <summary>Sets up a Space + one Markdown doc synced bidirectionally, waits for the initial replication.</summary>
    private async Task<string> SetupSynced(string initialBody = "v0")
    {
        await CreateSpace(Space);
        await CreateMarkdown(Doc, "Doc", initialBody);
        var source = await AddConfiguredSource(Space);
        await WaitForConfig(Space, "partner", c => c.InitialSyncAt is not null);
        await WaitForRemote(r => MarkdownBody(r.Node(Doc)) == initialBody);
        return source;
    }

    private Task EditLocal(string path, string body) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Content = new MarkdownContent { Content = body } })
            .Timeout(30.Seconds()).ToTask();

    /// <summary>Waits until the LOCAL node's markdown body equals <paramref name="expected"/>.</summary>
    private Task WaitForLocalBody(string path, string expected) =>
        Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Mesh.GetWorkspace().GetMeshNodeStream(path).Take(1))
            .Where(n => MarkdownBody(n) == expected)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

    [Fact(Timeout = 60000)]
    public async Task Round_trip_local_and_remote_edits_converge()
    {
        await SetupSynced();

        // A → remote: a local edit propagates to the remote.
        await EditLocal(Doc, "from_A");
        await WaitForRemote(r => MarkdownBody(r.Node(Doc)) == "from_A");

        // remote → A: a remote edit (newer) propagates back to the local instance.
        Remote.Seed(Remote.Node(Doc)! with { Content = new MarkdownContent { Content = "from_B" } },
            DateTimeOffset.UtcNow.AddMinutes(1));
        await WaitForLocalBody(Doc, "from_B");

        // Converged: both sides equal.
        await WaitForRemote(r => MarkdownBody(r.Node(Doc)) == "from_B");
        Assert.Equal("from_B", MarkdownBody(Remote.Node(Doc)));
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_edit_newer_remote_wins_on_both_sides()
    {
        await SetupSynced();

        // Offline window: BOTH edit the same node. Remote's edit is NEWER.
        Remote.Unreachable = true;
        await EditLocal(Doc, "A_edit");                                   // local, ~now
        Remote.Seed(Remote.Node(Doc)! with { Content = new MarkdownContent { Content = "B_edit" } },
            DateTimeOffset.UtcNow.AddMinutes(1));                         // remote, newer
        Remote.Unreachable = false;

        // Newest-writer-wins: the remote edit wins on BOTH sides (the local push must NOT clobber the
        // newer remote — that's the convergence fix). A's older edit is superseded, not lost to a split.
        await WaitForLocalBody(Doc, "B_edit");
        await WaitForRemote(r => MarkdownBody(r.Node(Doc)) == "B_edit");
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_edit_newer_local_wins_on_both_sides()
    {
        await SetupSynced();

        Remote.Unreachable = true;
        Remote.Seed(Remote.Node(Doc)! with { Content = new MarkdownContent { Content = "B_old" } },
            DateTimeOffset.UtcNow.AddMinutes(-30));                       // remote, older
        await EditLocal(Doc, "A_new");                                   // local, ~now (newer)
        Remote.Unreachable = false;

        // The newer LOCAL edit wins on both sides.
        await WaitForRemote(r => MarkdownBody(r.Node(Doc)) == "A_new");
        await WaitForLocalBody(Doc, "A_new");
    }

    [Fact(Timeout = 60000)]
    public async Task Offline_long_flight_accumulates_then_drains_and_converges()
    {
        await SetupSynced();
        await CreateMarkdown("conflict/doc2", "Doc2", "d2_v0");
        await WaitForRemote(r => r.Node("conflict/doc2") is not null);

        // "On the flight": remote unreachable; edits pile up locally in the durable manifest.
        Remote.Unreachable = true;
        await EditLocal(Doc, "offline_1");
        await EditLocal("conflict/doc2", "offline_2");
        // Nothing transferred while offline.
        await WaitForConfig(Space, "partner", c => c.PendingChanges.Count >= 2);
        Assert.NotEqual("offline_1", MarkdownBody(Remote.Node(Doc)));

        // "Landed": reconnect → the manifest drains → both nodes converge on the remote.
        Remote.Unreachable = false;
        await WaitForRemote(r => MarkdownBody(r.Node(Doc)) == "offline_1"
                                 && MarkdownBody(r.Node("conflict/doc2")) == "offline_2");
        await WaitForConfig(Space, "partner", c => c.PendingChanges.Count == 0);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_edit_of_different_fields_resolves_whole_node_by_newest()
    {
        await SetupSynced();

        // A edits the NAME; the remote edits the CONTENT (newer), each unaware of the other.
        Remote.Unreachable = true;
        await Mesh.GetWorkspace().GetMeshNodeStream(Doc)
            .Update(n => n with { Name = "A_renamed" }).Timeout(30.Seconds()).ToTask();
        Remote.Seed(Remote.Node(Doc)! with { Content = new MarkdownContent { Content = "B_body" } },
            DateTimeOffset.UtcNow.AddMinutes(1));
        Remote.Unreachable = false;

        // Resolution is WHOLE-NODE newest-writer-wins (not a field-level merge): the newer remote
        // node wins entirely, so its Name (unchanged) + body converge on both sides. Documenting the
        // known trade-off — a field-level (RFC 7396) merge would preserve both edits.
        await WaitForLocalBody(Doc, "B_body");
        var local = await Mesh.GetWorkspace().GetMeshNodeStream(Doc).Take(1).ToTask();
        Assert.Equal("Doc", local!.Name);          // A's rename is superseded by the newer whole node
        Assert.Equal("B_body", MarkdownBody(Remote.Node(Doc)));
    }
}
