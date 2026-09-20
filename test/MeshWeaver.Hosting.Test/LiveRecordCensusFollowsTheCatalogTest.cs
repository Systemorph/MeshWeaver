using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The live census FOLLOWS the catalog — it is not a boot-time reading</b>
/// (MeshWeaver#4632).
///
/// <para>The pure fold is held in <see cref="LiveRecordCensusTest"/>; this holds the one property
/// the fold cannot: that the reading MOVES when the record does. A NodeType record stamped for
/// another framework after this replica's boot is on the census from the catalog's first emission,
/// and a re-stamp for the live framework — a mesh write, the change event the boot-time passes
/// never see — clears it from the NEXT emission. Both halves over a real monolith mesh, through the
/// same standing subscription the hosted service holds; a feed that took one snapshot
/// (<c>.Take(1)</c>, which is exactly what <c>ProbeDynamicTypes</c> and <c>WarmDynamicTypes</c>
/// do) passes the first assertion and fails the second.</para>
///
/// <para>The record is <c>CompilationStatus.Ok</c> on purpose: the NodeType hub's first-build
/// kickoff fires only on a NULL status with no usable build, so nothing here wakes a compile —
/// the census is a fold over records, and the test must not depend on a compiler.</para>
/// </summary>
public class LiveRecordCensusFollowsTheCatalogTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Other = "saec4a2dc1f075f1fff7cf076055e150e";

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string TypePath => $"{TestPartition}/Foreign{suffix}";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static NodeTypeDefinition Stamped(string framework, DateTimeOffset succeededAt) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = framework,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = $"Foreign/v844-{framework[..8]}-6ad498520d75.dll",
        LastCompiledVersion = 844,
        LastCompileSucceededAt = succeededAt,
    };

    [Fact]
    public async Task ARecordReKeyedAfterBoot_IsOnTheCensus_AndLeavesItWhenTheRecordMoves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // "Booted" an hour ago; the foreign stamp landed half an hour ago — after boot, the
        // mid-roll cross-stamp shape. Both are data, so the split needs no clock.
        var bootedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var stampedAt = bootedAt.AddMinutes(30);

        await MeshService.CreateNode(new MeshNode($"Foreign{suffix}", TestPartition)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = $"Foreign{suffix}",
                State = MeshNodeState.Active,
                Content = Stamped(Other, stampedAt),
            })
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the foreign record must exist before the census reads the catalog", cancellationToken);

        // The catalog listing the census subscribes to is the index, and the index trails the store.
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem())
                .Take(1))
            .Where(change => change.Items.Any(n => string.Equals(n.Path, TypePath, StringComparison.OrdinalIgnoreCase)))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the catalog must list the record before the census is asked about it", cancellationToken);

        var census = DynamicTypePreWarmer.ObserveLiveRecordCensus(Mesh, bootedAt);

        var foreign = await census
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c.ForeignSinceBoot >= 1,
                "a record stamped for another framework AFTER this replica booted must be on the "
                + "census from the catalog's first emission — nobody has opened the type, which is "
                + "precisely the case content-types cannot see",
                cancellationToken);
        foreign.Foreign.Should().BeGreaterThanOrEqualTo(1);
        foreign.ForeignDetail.Should().Contain($"saec4a2d×1 in {TestPartition}/…",
            "the partition, not the node title — this reaches a public body");
        foreign.IsClean.Should().BeFalse();

        // The record moves: re-stamped for THIS framework, as the type's own hub would after a
        // forced rebuild. A mesh write — the change event a one-shot enumeration never receives.
        await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
            .Update(node => node with
            {
                Content = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)! with
                {
                    CompiledFrameworkVersion = PrebuiltAssemblySeeder.LiveFrameworkMvid,
                    LatestAssemblyPath = $"Foreign/v845-{PrebuiltAssemblySeeder.LiveFrameworkMvid[..8]}-live.dll",
                    LastCompiledVersion = 845,
                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                },
            })
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the re-stamp lands on the record", cancellationToken);

        var healed = await census
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c.ForeignSinceBoot == 0 && c.Total >= 1,
                "the census must FOLLOW the catalog: once the record names a build for this "
                + "framework it leaves the reading on the next emission. A feed that enumerated once "
                + "at boot would still report the stale foreign stamp here — which is the defect",
                cancellationToken);
        healed.IsClean.Should().BeTrue();
        healed.ForeignDetail.Should().NotContain(TestPartition,
            "the only foreign record this mesh had is now keyed to the live framework");
    }
}
