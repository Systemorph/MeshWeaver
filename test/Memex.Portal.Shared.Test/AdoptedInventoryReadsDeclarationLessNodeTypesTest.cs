using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.ControlLane;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A statically-registered NodeType that declares no <see cref="NodeTypeDefinition"/>
/// (<c>ControlLaneRecord</c> — every portal registers it) is code in the image: it adopts no
/// framework, and it must not make the adopted-artifact inventory INCOMPLETE. On 2026-10-06 it did,
/// on memex, memex-cloud and control alike: every report carried
/// <c>AdoptedFrameworkInventoryComplete=false</c> after a Warning naming
/// <c>NodeType adoption record ControlLaneRecord could not be read</c>, so the control instance's
/// retention refused every report. Content that is present but unreadable stays the loud case.
/// </summary>
/// <param name="output">The xUnit output sink.</param>
public class AdoptedInventoryReadsDeclarationLessNodeTypesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            // The production registration that carries the declaration-less static NodeType.
            .AddControlLane()
            .AddMeshNodes(new MeshNode(DeploymentReportService.InventoryNodeType)
            {
                Name = "Module Inventory",
                IsSatelliteType = false,
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
            })
            .ConfigureServices(services => services
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { [DeploymentReportService.DeploymentKey] = "unit-dep" }).Build())
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallPreInstalledPackages = false,
                    InstanceId = "instance-1",
                    HomeUrl = "https://unit.example",
                }));

    /// <summary>
    /// The production registration of the content-less <c>ControlLaneRecord</c> NodeType leaves the
    /// deployment report's adopted-framework inventory complete, with no "incomplete" warning.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADeclarationLessStaticNodeType_LeavesTheAdoptedInventoryComplete()
    {
        var outcome = await Mesh.ServiceProvider.GetRequiredService<DeploymentReportService>()
            .Report().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        var report = outcome.Report ?? throw new InvalidOperationException(
            $"the report carried no DeploymentReport: {outcome.Delivery} - {outcome.Detail}");
        report.AdoptedFrameworkInventoryComplete.Should().Be(true,
            $"'{ControlLaneExtensions.RecordNodeType}' is a static NodeType with no definition — it adopts "
            + "nothing, and reading it as 'unreadable' made every instance's inventory incomplete");
        report.Warnings.Should().NotContain(w => w.Contains("adopted artifact inventory is incomplete"));
    }

    /// <summary>
    /// <see cref="NodeTypeAdoptionStamp.CompiledFrameworkVersionOf"/> keeps its three states apart:
    /// no content is null, a readable definition is its stamp, and unreadable content throws with
    /// the established <c>could not be read</c> fingerprint.
    /// </summary>
    [Fact]
    public void TheStamp_TellsNoContentFromAStampFromUnreadableContent()
    {
        var options = Mesh.JsonSerializerOptions;

        NodeTypeAdoptionStamp.CompiledFrameworkVersionOf(
                new MeshNode(ControlLaneExtensions.RecordNodeType) { NodeType = MeshNode.NodeTypePath }, options)
            .Should().BeNull("no content declares nothing — it is an answer, not an unreadable record");

        NodeTypeAdoptionStamp.CompiledFrameworkVersionOf(
                new MeshNode("Stamped") { NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition { CompiledFrameworkVersion = "fw-1" } },
                options)
            .Should().Be("fw-1");

        var unreadable = new MeshNode("Unreadable")
        {
            NodeType = MeshNode.NodeTypePath,
            // A foreign CLR value: ContentAs answers null for it exactly as it does for no content,
            // which is the fold the readers made.
            Content = "not a NodeTypeDefinition",
        };
        Action read = () => NodeTypeAdoptionStamp.CompiledFrameworkVersionOf(unreadable, options);
        read.Should().Throw<InvalidOperationException>(
                "present-but-unreadable content means the stamp is UNKNOWN — a reference set that drops it "
                + "lets retention delete a bundle someone adopted")
            .WithMessage("NodeType adoption record Unreadable could not be read*String*");
    }
    /// <summary>
    /// The retention side, at the pass level: an unreadable adoption record makes the reference
    /// fold THROW (it is never dropped), that error ends the pass before the sweep is invoked — so
    /// nothing is deleted against an incomplete reference set — and the pass guard records the
    /// fault and completes, so the schedule survives to plan again on the next tick.
    /// </summary>
    [Fact]
    public void AnUnreadableRecord_AbortsTheRetentionPass_BeforeTheSweep_AndTheScheduleSurvives()
    {
        var options = Mesh.JsonSerializerOptions;
        var records = new[]
        {
            new MeshNode(ControlLaneExtensions.RecordNodeType) { NodeType = MeshNode.NodeTypePath },
            new MeshNode("Stamped") { NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition { CompiledFrameworkVersion = "fw-1" } },
        };
        PrebuiltBundleRetentionHostedService.StampedIdentitiesOf(records, options, null)
            .Should().ContainSingle("a declaration-less record contributes nothing, a stamped one its stamp")
            .Which.Should().Be("fw-1");

        var withUnreadable = records.Append(new MeshNode("Unreadable")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = "not a NodeTypeDefinition",
        }).ToArray();
        var stamps = Observable.Defer(() => Observable.Return(
            PrebuiltBundleRetentionHostedService.StampedIdentitiesOf(withUnreadable, options, null)));

        var swept = 0;
        var recorded = 0;
        Exception? faulted = null;
        var passes = 0;
        Exception? scheduleDied = null;
        PrebuiltBundleRetentionHostedService.GuardedPass(
                PrebuiltBundleRetentionHostedService.PassOver(
                    stamps,
                    Observable.Return(System.Collections.Immutable.ImmutableList<PinnedPlatformReference>.Empty),
                    (_, _) =>
                    {
                        swept++;
                        return Observable.Return(1);
                    }),
                _ => recorded++,
                ex => faulted = ex)
            .Subscribe(_ => passes++, ex => scheduleDied = ex);

        swept.Should().Be(0, "a reference set that could not be read must never reach the sweep");
        recorded.Should().Be(0);
        faulted.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().StartWith("NodeType adoption record Unreadable could not be read");
        passes.Should().Be(1, "the faulted pass completes as one pass, so the schedule's next tick runs");
        scheduleDied.Should().BeNull("a pass's fault is bounded to that pass, never the schedule");
    }
}
