using System;
using System.Reactive.Linq;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="SelfTypedDeclarationDurableRepair"/> tolerates a faulted read lane on purpose — one
/// partition that cannot answer must not stop the other lanes healing — but a tolerated fault is
/// still a FAILED step of the pass. It used to be visible only as a Warning beside a summary line
/// that said <c>sweep completed</c>, so the one line an operator reads claimed a clean pass while
/// every self-typed row that lane covered stayed unhealed. The lane now records the failure, and
/// the summary turns into an Error naming it.
/// </summary>
public class SelfTypedDeclarationRepairReportsFailedStepsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void AFaultedReadLane_IsRecordedAsAFailedStep_NotFoldedIntoACleanCompletion()
    {
        var stats = new SelfTypedDeclarationDurableRepair.SweepStats();
        var completed = false;
        Exception? escaped = null;

        SelfTypedDeclarationDurableRepair.Sweep(
                Observable.Throw<MeshNode>(new InvalidOperationException("42P01: relation \"auth.mesh_nodes\" does not exist")),
                Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
                "partition 'Auth' via test",
                Mesh.JsonSerializerOptions,
                stats,
                logger: null)
            .Subscribe(_ => { }, ex => escaped = ex, () => completed = true);

        escaped.Should().BeNull("the lane is fault-tolerant by design — the next lane must still run");
        completed.Should().BeTrue("Observable.Throw faults synchronously, so the tolerated lane ends synchronously");
        stats.Failures.Should().ContainSingle(
                "a tolerated read fault is a failed step of the pass, and the summary is built from this list")
            .Which.Should().Contain("partition 'Auth' via test").And.Contain("42P01");
    }
}
