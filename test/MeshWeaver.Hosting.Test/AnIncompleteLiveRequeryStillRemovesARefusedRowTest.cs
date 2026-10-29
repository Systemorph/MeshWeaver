using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An incomplete live re-query keeps what it could not READ, never what it read and REFUSED</b>
/// (MeshWeaver#7365, review of #7565).
///
/// <para><b>The defect.</b> An open secured query re-decides every row through the read check when a
/// grant changes. If, in that same run, a read of some OTHER row failed, the run is incomplete — and an
/// incomplete run removed nothing (MeshWeaver#7399), so a row the check had just REFUSED stayed with
/// the revoked reader for as long as no complete run came. A refusal is a decision, not a missing read:
/// the run records it apart from its dropped reads, and removes the row even when it is incomplete.</para>
///
/// <para>Deterministic: the in-memory store answers synchronously, the listing fault is injected on one
/// sub-tree exactly once, and the test resumes on the fault's own arrival, never on a clock.</para>
/// </summary>
public class AnIncompleteLiveRequeryStillRemovesARefusedRowTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const string Base = "acme";
    private const string Sub = "acme/sub";
    private const string Watched = "Markdown";
    private const string Reader = "reader";

    /// <summary>A Read check the test turns against one row: it admits everything until <see cref="Refuse"/> names a path.</summary>
    private sealed class SwitchableReadValidator : INodeValidator
    {
        private string? refused;

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Read];

        public void Refuse(string path) => Volatile.Write(ref refused, path);

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Return(string.Equals(context.Node.Path, Volatile.Read(ref refused), StringComparison.OrdinalIgnoreCase)
                ? NodeValidationResult.Unauthorized("revoked")
                : NodeValidationResult.Valid());
    }

    private static IObservable<QueryResultChange<MeshNode>> Carrying(
        IObservable<QueryResultChange<MeshNode>> changes, QueryChangeType type, string id) =>
        changes.Where(c => c.ChangeType == type
            && (c.Items ?? []).Any(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)));

    [Fact(Timeout = 60_000)]
    public async Task AnIncompleteRun_RemovesTheHeldRowItRefused_AndKeepsTheRowsItCouldNotRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        var adapter = new FaultInjectingStorageAdapter(store);
        await adapter.Write(new MeshNode("x1", Base) { Name = "x1", NodeType = Watched }, Options)
            .Should().Within(TestTimeouts.Convergence).Emit("write x1", cancellationToken: ct);
        await adapter.Write(new MeshNode("sub", Base) { Name = "sub", NodeType = "Folder" }, Options)
            .Should().Within(TestTimeouts.Convergence).Emit("write sub", cancellationToken: ct);
        await adapter.Write(new MeshNode("y1", Sub) { Name = "y1", NodeType = Watched }, Options)
            .Should().Within(TestTimeouts.Convergence).Emit("write y1", cancellationToken: ct);

        var validator = new SwitchableReadValidator();
        var provider = new StorageAdapterMeshQueryProvider(
            persistence: adapter,
            nodeValidators: [new Lazy<INodeValidator>(() => validator)]);
        var changes = new ReplaySubject<QueryResultChange<MeshNode>>();
        using var subscription = ((IMeshQueryProvider)provider)
            .Query<MeshNode>(
                MeshQueryRequest.FromQueries([$"path:{Base} scope:descendants nodeType:{Watched}"], Reader),
                Options)
            .Subscribe(changes);
        var seen = ImmutableList<QueryResultChange<MeshNode>>.Empty;
        using var recorder = changes.Subscribe(c => ImmutableInterlocked.Update(ref seen, l => l.Add(c)));

        var initial = await changes.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(TestTimeouts.Convergence).Emit("Initial", cancellationToken: ct);
        string.Join(",", initial.Items.Select(n => n.Id).Order()).Should().Be("x1,y1",
            "the reader holds both rows, or nothing below is about keeping or losing one");

        // The reader loses Read on x1, and the next run's listing of the sub-tree fails once: that run
        // READS x1 (and refuses it) but cannot read y1, so it is incomplete.
        validator.Refuse($"{Base}/x1");
        using var fault = adapter.FailOperations(Sub, StorageLegs.Enumerate, times: 1);
        await adapter.Write(new MeshNode("x2", Base) { Name = "x2", NodeType = Watched }, Options)
            .Should().Within(TestTimeouts.Convergence).Emit("write x2", cancellationToken: ct);
        await fault.Arrivals
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the live re-query triggered by x2 must have met the listing fault", cancellationToken: ct);

        await Carrying(changes, QueryChangeType.Added, "x2")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the incomplete run still lists what it read", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Removed, "x1")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a row the incomplete run READ and REFUSED leaves the revoked reader's result", cancellationToken: ct);

        fault.ArrivalCount.Should().Be(1, "the fault must actually have been injected, exactly once");
        // NEGATIVE CONTROL: the row the run could not read is kept, as #7399 requires.
        seen.Where(c => c.ChangeType == QueryChangeType.Removed)
            .SelectMany(c => c.Items).Select(n => n.Id)
            .Should().NotContain("y1", "a row the incomplete run could not read is kept, not removed");
    }
}
