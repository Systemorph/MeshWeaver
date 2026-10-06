#pragma warning disable CS1591
using System;
using System.Collections.Generic;
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
public class AdoptedInventoryReadsDeclarationLessNodeTypesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

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

    [Fact(Timeout = 180_000)]
    public async Task ADeclarationLessStaticNodeType_LeavesTheAdoptedInventoryComplete()
    {
        var outcome = await Mesh.ServiceProvider.GetRequiredService<DeploymentReportService>()
            .Report().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        var report = outcome.Report!;
        report.AdoptedFrameworkInventoryComplete.Should().Be(true,
            $"'{ControlLaneExtensions.RecordNodeType}' is a static NodeType with no definition — it adopts "
            + "nothing, and reading it as 'unreadable' made every instance's inventory incomplete");
        report.Warnings.Should().NotContain(w => w.Contains("adopted artifact inventory is incomplete"));
    }

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
            .WithMessage("*Unreadable*String*");
    }
}
