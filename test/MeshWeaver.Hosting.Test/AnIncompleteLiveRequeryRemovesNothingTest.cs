using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A live re-query that could not read part of its scope must not REMOVE what it could not
/// read</b> (MeshWeaver#7399).
///
/// <para><b>The defect.</b> <see cref="StorageAdapterMeshQueryProvider"/>'s live pipeline re-runs
/// the whole query on every relevant change and diffs the answer against the rows it holds. A read
/// fault inside that re-run (a <c>ListChildPaths</c> that throws, a per-path read that times out) is
/// caught, recorded and the rows behind it dropped — the same catch the Initial uses. But the
/// Initial carries the verdict (<see cref="QueryResultChange{T}.SnapshotIncomplete"/>, #1186) and the
/// live path had none: the short answer was diffed as if COMPLETE, so every row the run failed to
/// read was emitted as <c>Removed</c>. Nothing re-triggers a query that has no write to react to,
/// so the result stayed wrong until a later write under the same scope — measured in a Plugins
/// stress loop as a live listing going from 174 rows to 0 and staying there.</para>
///
/// <para><b>The rule.</b> Every live run carries its OWN completeness verdict (the Initial's sink is
/// per subscription and monotone, so it cannot say which run was short). A complete run diffs as
/// before. An incomplete run asserts only what it READ — additions and updates — and removes
/// nothing: a row it did not see may be a row it could not read. No retry: a persistent fault must
/// not turn every write into a storm of re-reads; the next trigger under the scope re-reads it.</para>
///
/// <para>Deterministic: the in-memory store answers synchronously, the fault is injected by path
/// exactly ONCE (<c>times: 1</c>) and the test resumes on the fault's own arrival, never on a clock.</para>
/// </summary>
public class AnIncompleteLiveRequeryRemovesNothingTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const string Base = "acme";
    private const string Watched = "Markdown";

    private static MeshNode Node(string id) => new(id, Base) { Name = id, NodeType = Watched };

    private static (ReplaySubject<QueryResultChange<MeshNode>> Changes, IDisposable Subscription) Open(
        StorageAdapterMeshQueryProvider provider)
    {
        var changes = new ReplaySubject<QueryResultChange<MeshNode>>();
        var subscription = ((IMeshQueryCore)provider)
            .Query<MeshNode>(
                MeshQueryRequest.FromQueries([$"path:{Base} scope:descendants nodeType:{Watched}"], "system-security"),
                Options)
            .Subscribe(changes);
        return (changes, subscription);
    }

    private static IObservable<QueryResultChange<MeshNode>> Carrying(
        IObservable<QueryResultChange<MeshNode>> changes, QueryChangeType type, string id) =>
        changes.Where(c => c.ChangeType == type
            && (c.Items ?? []).Any(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// 🚨 THE #7399 REPRO. A live re-query whose listing of the scope fails once must not emit
    /// <c>Removed</c> for the rows it could not list, and must not forget them: the next COMPLETE run
    /// adds only what is genuinely new.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AFaultedLiveRequery_RemovesNothing_AndTheNextCompleteRunAddsOnlyWhatIsNew()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        var adapter = new FaultInjectingStorageAdapter(store);
        await adapter.Write(Node("x1"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x1", cancellationToken: ct);
        await adapter.Write(Node("x2"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x2", cancellationToken: ct);

        var provider = new StorageAdapterMeshQueryProvider(persistence: adapter);
        var (changes, subscription) = Open(provider);
        using var _ = subscription;
        var seen = ImmutableList<QueryResultChange<MeshNode>>.Empty;
        using var recorder = changes.Subscribe(c => ImmutableInterlocked.Update(ref seen, l => l.Add(c)));

        var initial = await changes.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(TestTimeouts.Convergence).Emit("Initial", cancellationToken: ct);
        string.Join(",", initial.Items.Select(n => n.Id).Order()).Should().Be("x1,x2",
            "the Initial must hold both rows, or nothing below is about losing them");

        // The listing of the scope fails exactly once — the next live re-query's.
        using var fault = adapter.FailOperations(Base, StorageLegs.Enumerate, times: 1);
        await adapter.Write(Node("x3"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x3", cancellationToken: ct);
        await fault.Arrivals
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the live re-query triggered by x3 must have met the listing fault", cancellationToken: ct);

        // The fault is spent; this write's re-query is complete and reads x3 and x4.
        await adapter.Write(Node("x4"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x4", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Added, "x4")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the next complete live run must list the new row", cancellationToken: ct);

        fault.ArrivalCount.Should().Be(1, "the fault must actually have been injected, exactly once");

        var removed = seen.Where(c => c.ChangeType == QueryChangeType.Removed)
            .SelectMany(c => c.Items).Select(n => n.Id).ToList();
        removed.Should().BeEmpty(
            "no row was deleted — x1 and x2 were only UNREAD by a live run whose listing faulted, and "
            + "an incomplete run must not assert the absence of what it could not read");

        var readded = seen.Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items).Select(n => n.Id)
            .Where(id => id is "x1" or "x2").ToList();
        readded.Should().BeEmpty(
            "the rows an incomplete run could not read are still held, so the next complete run finds "
            + "them unchanged — re-adding them would mean they had been dropped in between");
    }

    /// <summary>
    /// THE SIDE THAT MUST NOT MOVE: a COMPLETE live run still removes a row that is genuinely gone,
    /// including one that an earlier incomplete run had kept. Without this case "never remove" would
    /// pass the test above and leave every deleted row in every live result forever.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACompleteLiveRequery_StillRemovesADeletedRow_AfterAFaultedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        var adapter = new FaultInjectingStorageAdapter(store);
        await adapter.Write(Node("x1"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x1", cancellationToken: ct);
        await adapter.Write(Node("x2"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x2", cancellationToken: ct);

        var provider = new StorageAdapterMeshQueryProvider(persistence: adapter);
        var (changes, subscription) = Open(provider);
        using var _ = subscription;
        await changes.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(TestTimeouts.Convergence).Emit("Initial", cancellationToken: ct);

        using var fault = adapter.FailOperations(Base, StorageLegs.Enumerate, times: 1);
        await adapter.Write(Node("x3"), Options).Should().Within(TestTimeouts.Convergence).Emit("write x3", cancellationToken: ct);
        await fault.Arrivals
            .Should().Within(TestTimeouts.Convergence).Emit("faulted live run", cancellationToken: ct);

        await adapter.Delete($"{Base}/x1").Should().Within(TestTimeouts.Convergence).Emit("delete x1", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Removed, "x1")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a complete live run must still remove a row that is genuinely gone", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Added, "x3")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("and add the row the faulted run could not list", cancellationToken: ct);
    }
}
