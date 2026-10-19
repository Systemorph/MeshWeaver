using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// 🚨 A create that FAILED may still have WRITTEN (#6951, review of #6976). A case-only rename whose
/// second leg meets one must not move the subtree back over it and answer "nothing was renamed".
///
/// <para><c>HandleCreateNodeRequest</c> writes the row and only THEN runs the post-creation handlers.
/// When a critical one fails, the create rolls its row back — and when that rollback fails too, it
/// answers a failure over a row that is still stored. The copy used to count only creates that
/// SUCCEEDED as written, so the rename removed what it knew of, moved staging back to the source and,
/// on this case-insensitive store, RESUMED (#6310) onto the surviving new-cased row — reporting the
/// subtree restored while one node kept the new casing. The copy now reports such a path as possibly
/// written, and the rename answers Unknown, naming the staging path, without restoring.</para>
///
/// <para>Isolated faults: a critical post-creation handler that fails for exactly ONE path (the
/// descendant at the target's casing, compared ordinally — leg 1 and every other create are
/// untouched), and <see cref="FaultInjectingStorageAdapter.FailDeletes"/> on that path so the
/// create's own rollback dies on the store. The controls: the rollback reached the fault, the row it
/// failed to remove is still stored at the target's casing, and the staging path the answer names
/// still holds the whole subtree.</para>
/// </summary>
public class CaseOnlyRenameDoesNotRestoreOverACreateThatFailedAfterWritingTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string StagingMarker = "-recase-";

    private readonly FailAfterWritingHandler failing = new();

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage())
            .ConfigureServices(services => services.AddSingleton<INodePostCreationHandler>(failing));

    [Fact(Timeout = 120_000)]
    public async Task ASecondLegCreateThatFailedAfterWriting_IsUnknown_AndNothingIsMovedBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);
        failing.FailFor($"{target}/birgit");

        MoveNodeResponse moved;
        using (var fault = Storage.FailDeletes($"{target}/birgit"))
        {
            moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
                .Select(d => d.Message)
                .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            await fault.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the failed create's own rollback reached the store and died there", ct);
        }

        // The control: the create DID write, and its row is still there at the target's casing.
        (await StoredAt($"{target}/birgit", ct))!.Path.Should().Be($"{target}/birgit",
            "the create wrote its row before its post-creation step failed, and the rollback could not remove it");

        moved.Success.Should().BeFalse("the second leg's create failed");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.Unknown,
            "a create that failed after it may have written leaves the target unknown, so nothing may be moved back over it");
        moved.Error.Should().NotContain("The subtree was moved back", "the subtree was not restored");
        var staging = StagingNamedIn(moved.Error!, target);
        Output.WriteLine($"staging named in the answer: {staging}");
        moved.Log.Should().NotBeNull("the outcome travels as a keyed transcript");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameUndoUnproven", "a German viewer reads it in German");

        // Nothing was moved back: the staging path the answer names still holds the whole subtree.
        (await StoredAt(staging, ct)).Should().NotBeNull("the complete copy is at the staging path the answer names");
        (await Descendants(staging, ct)).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [$"{staging}/birgit", $"{staging}/birgit/notes"], "the staging copy is complete");
    }

    private static string StagingNamedIn(string error, string target)
    {
        var start = error.IndexOf($"'{target}{StagingMarker}", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the answer names the staging path; it said: {error}");
        var end = error.IndexOf('\'', start + 1);
        return error[(start + 1)..end];
    }

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Inner.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<IReadOnlyCollection<string>> Descendants(string path, CancellationToken ct) =>
        Storage.Inner.ListDescendantPaths(path)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    /// <summary>
    /// A CRITICAL post-creation handler (<see cref="INodePostCreationHandler.FailsCreateOnError"/>) that
    /// fails for exactly the paths it is told to, compared ORDINALLY — so the row is written and the
    /// create then fails. Instance state owned by the test's mesh, never static.
    /// </summary>
    private sealed class FailAfterWritingHandler : INodePostCreationHandler
    {
        private System.Collections.Immutable.ImmutableHashSet<string> failFor =
            System.Collections.Immutable.ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        public void FailFor(string path) =>
            System.Collections.Immutable.ImmutableInterlocked.Update(ref failFor, s => s.Add(path));

        /// <summary>Diagnostic label only — <see cref="Matches"/> decides.</summary>
        public string NodeType => "Markdown";

        public bool Matches(MeshNode createdNode) => failFor.Contains(createdNode.Path);

        public bool FailsCreateOnError => true;

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy) =>
            Observable.Throw<Unit>(new InvalidOperationException(
                $"post-creation step for '{createdNode.Path}' failed (injected by this test)"));
    }
}
