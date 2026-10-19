using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// 🚨 Undoing a failed case-only rename leg must not discard a write somebody else made to what that
/// leg had written (#6951, review of #6976).
///
/// <para>The relocation scope guards the leg's SOURCE, not its target, so while the leg's remaining
/// creates finish, a row the copy already landed at the target can be edited. The clean-up used to
/// delete every path the copy wrote, path-only — so the edit was destroyed, and the subtree was then
/// moved back with the answer "nothing was renamed". The clean-up now runs inside a deletion scope on
/// the leg's target (admitted writes drained, new ones refused) and removes a row only while it is
/// still exactly what the copy wrote; a row that changed is kept and the answer is Unknown.</para>
///
/// <para>Isolated faults: <see cref="FaultInjectingStorageAdapter.HoldWrites"/> parks the second leg's
/// create of one descendant (at the target's casing) so the test can edit the already-landed target
/// root in that window, and one create validator refuses exactly the grandchild at the target's
/// casing so the leg fails and its clean-up runs. The controls: the edit is still stored afterwards,
/// and the staging path the answer names still holds the whole subtree.</para>
/// </summary>
public class CaseOnlyRenameKeepsATargetEditedBeforeItsCleanupTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StagingMarker = "-recase-";
    private const string EditedName = "Edited by someone else";

    private readonly RefuseExactPath refusing = new();

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private IStorageAdapter Store => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage())
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 120_000)]
    public async Task ATargetRowEditedBeforeTheCleanup_IsKept_AndTheAnswerIsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);
        refusing.Refuse($"{target}/birgit/notes");

        MoveNodeResponse moved;
        using (var hold = Storage.HoldWrites($"{target}/birgit"))
        {
            var move = ObserveNodeOperation(new MoveNodeRequest(source, target))
                .Select(d => d.Message)
                .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

            // The second leg has landed the target root and is parked on a descendant: edit the root.
            await Storage.HeldWrites($"{target}/birgit")
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the second leg's descendant create reached the store", ct);
            var landed = await StoredAt(target, ct);
            landed.Should().NotBeNull("the second leg created the target root before its descendants");
            Output.WriteLine($"landed root: path={landed!.Path} version={landed.Version} lastModified={landed.LastModified:O}");
            var edited = await Store.Write(landed with { Name = EditedName, LastModified = DateTimeOffset.UtcNow },
                    Mesh.JsonSerializerOptions)
                .Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            Output.WriteLine($"edited root: version={edited?.Version} lastModified={edited?.LastModified:O}");

            hold.Dispose();
            moved = await move;
        }

        // The control: the edit survived.
        var root = await StoredAt(target, ct);
        root.Should().NotBeNull("an edited target row is never removed by the clean-up");
        root!.Name.Should().Be(EditedName, "the edit made after the copy landed the row is kept");

        moved.Success.Should().BeFalse("the second leg's grandchild create was refused");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.Unknown,
            "a target row changed after the copy wrote it, so the subtree cannot be cleanly moved back");
        moved.Error.Should().NotContain("The subtree was moved back", "nothing was moved back over the edited target");
        var staging = StagingNamedIn(moved.Error!, target);
        moved.Log.Should().NotBeNull("the outcome travels as a keyed transcript");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameUndoUnproven", "a German viewer reads it in German");

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
    /// Refuses the create of exactly the paths it is told to, compared ORDINALLY. Instance state owned
    /// by the test's mesh, never static.
    /// </summary>
    private sealed class RefuseExactPath : INodeValidator
    {
        private ImmutableHashSet<string> refused = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        public void Refuse(string path) => ImmutableInterlocked.Update(ref refused, s => s.Add(path));

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Return(refused.Contains(context.Node.Path)
                ? NodeValidationResult.Invalid($"creating '{context.Node.Path}' is refused by this test")
                : NodeValidationResult.Valid());
    }
}
