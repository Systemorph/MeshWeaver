using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins that a one-shot node read reports WHY it has nothing — specifically that the owner's
/// delete-in-progress tombstone is distinguishable from genuine absence.
///
/// <para><b>The defect (#1471).</b> The tombstone answers <c>GetDataResponse(null, 0)</c>
/// <em>by design</em> (<c>MeshDataSource.AddReadValidatorPipeline</c>): a read that lands after the
/// delete has been recorded but before the row is gone must not serve stale content. On the wire
/// that was byte-identical to "there is nothing at this path", so every caller collapsed the two.
/// <c>InstanceSyncWorker.PullOne</c> read the collapsed <c>null</c> as "absent ⇒ create it" and
/// re-applied a node the user had just deleted.</para>
///
/// <para>The window is reproduced exactly as production creates it: <c>HandleDeleteNodeRequest</c>
/// marks the path in <see cref="RecentlyDeletedRegistry"/> SYNCHRONOUSLY, before it returns and
/// therefore before the row is gone — so "marked, node still there" IS the in-flight state, not a
/// simulation of it. No sleeps, no racing.</para>
/// </summary>
public class DeleteInProgressReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60000)]
    public async Task ReadOfANodeWhoseDeleteIsInFlight_SaysSo_InsteadOfClaimingAbsence()
    {
        var path = $"{TestPartition}/delete-in-flight";
        await NodeFactory.CreateNode(
            new MeshNode("delete-in-flight", TestPartition)
            {
                Name = "Being Deleted",
                NodeType = "Markdown"
            }).Should().Emit();

        // Prove the happy path first, so a later "nothing" cannot be blamed on the node never
        // having existed.
        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var reader = GetClient(c => c.AddData());

        var present = await reader.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(30))
            .Should().Within(30.Seconds()).Emit();
        present.Status.Should().Be(NodeReadStatus.Present);
        present.Node!.Path.Should().Be(path);

        // 🔻 Enter the delete-in-flight window.
        var tombstones = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        tombstones.MarkDeleted(path);
        Output.WriteLine($"[TEST] tombstoned {path} — the row is still there, the delete is in flight");

        var inFlight = await reader.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(30))
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[TEST] outcome while the delete is in flight: {inFlight.Status}");

        inFlight.Status.Should().Be(NodeReadStatus.DeleteInProgress,
            "the owner ANSWERED — with its delete tombstone. Reporting that as Absent is what let "
            + "a replicator read 'not there ⇒ create it' and resurrect a node the user just deleted");
        inFlight.Node.Should().BeNull("there is deliberately no content to serve mid-delete");

        // 🚨 And the other direction must not have merged either: a path that genuinely does not
        // exist stays ABSENT. Without this, "distinguishable" could be satisfied by calling
        // everything DeleteInProgress.
        var absent = await reader
            .GetMeshNodeOutcome($"{TestPartition}/never-existed", TimeSpan.FromSeconds(30))
            .Should().Within(30.Seconds()).Emit();
        absent.Status.Should().Be(NodeReadStatus.Absent,
            "routing answers NotFound for a path with no node — that IS absence");

        // The convenience read keeps its documented MeshNode? contract exactly: both are null.
        var collapsed = await reader.GetMeshNode(path, TimeSpan.FromSeconds(30))
            .Should().Within(30.Seconds()).Emit();
        collapsed.Should().BeNull(
            "GetMeshNode is GetMeshNodeOutcome with the distinction discarded — callers that do "
            + "not ask for it see no behaviour change at all");
    }
}
