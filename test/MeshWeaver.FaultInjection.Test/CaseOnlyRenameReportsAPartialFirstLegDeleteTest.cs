using System;
using System.Collections.Generic;
using System.Linq;
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
/// 🚨 A case-only rename whose FIRST leg (source → staging) copied completely and then failed while
/// deleting the source must say where the subtree is (#6951, review of #6976).
///
/// <para>The staging path is minted per attempt (<c>{target}-recase-{token}</c>), so once the delete
/// leg has removed part of the source, the ONLY complete copy sits at an address the caller never
/// named. The failure used to fall through to the move's generic answer — the store's own exception
/// text — which named neither the staging path nor the fact that the source is now incomplete.</para>
///
/// <para>Isolated fault: <see cref="FaultInjectingStorageAdapter.FailDeleteMany"/> on the source root
/// lets the batch delete every descendant and then faults, leaving the root — the shape of a
/// sequential batch delete that died part-way. Nothing else is perturbed, and the control is the
/// staging path the answer names: it is read back and holds the whole subtree.</para>
/// </summary>
public class CaseOnlyRenameReportsAPartialFirstLegDeleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StagingMarker = "-recase-";

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage());

    [Fact(Timeout = 120_000)]
    public async Task ADeleteLegThatDiesPartWay_IsUnknown_AndNamesSourceStagingAndTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);

        MoveNodeResponse moved;
        using (var fault = Storage.FailDeleteMany(source))
        {
            moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
                .Select(d => d.Message)
                .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            await fault.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the first leg's delete batch reached the fault", ct);
        }

        moved.Success.Should().BeFalse("the first leg's delete failed part-way");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.Unknown,
            "part of the source is gone and the rest is not, so no verdict about the move can be given");
        moved.Error.Should().Contain($"'{source}'").And.Contain($"'{target}'");
        var staging = StagingNamedIn(moved.Error!, target);
        Output.WriteLine($"staging named in the answer: {staging}");
        moved.Log.Should().NotBeNull("the outcome travels as a keyed transcript");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameUnresolved", "a German viewer reads it in German");

        // The control: the staging path the answer names holds the complete subtree.
        (await StoredAt(staging, ct)).Should().NotBeNull("the complete copy is at the staging path the answer names");
        (await Descendants(staging, ct)).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [$"{staging}/birgit", $"{staging}/birgit/notes"], "the staging copy is complete");
        // And the source is indeed incomplete: the descendants went, the root stayed.
        (await StoredAt(source, ct)).Should().NotBeNull("the fault left the source root behind");
        (await Descendants(source, ct)).Should().BeEmpty("the batch removed every descendant before it faulted");
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
}
