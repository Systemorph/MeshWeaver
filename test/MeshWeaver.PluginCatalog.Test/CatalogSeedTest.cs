#pragma warning disable CS1591

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Proves the config-seed: <c>AddPluginCatalog(sourceRepoPath)</c> materializes a <c>Plugins</c>
/// Space with a <c>Plugins/catalog</c> catalog node on boot, so a portal shows the catalog without
/// anyone creating a node by hand. (The source path is irrelevant to seeding the node itself.)
/// </summary>
public class CatalogSeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog("/plugins", "catalog", "HEAD");

    [Fact(Timeout = 120000)]
    public async Task Seed_CreatesCatalogNode_UnderPluginsSpace()
    {
        var catalog = await Mesh.GetWorkspace().GetMeshNodeStream("Plugins/catalog")
            .Where(n => n is not null).FirstAsync().Timeout(30.Seconds()).ToTask();

        catalog.NodeType.Should().Be(PluginCatalogConfigurationExtensions.CatalogNodeType);
        var cfg = catalog.ContentAs<PluginCatalogContent>(Mesh.JsonSerializerOptions);
        cfg.Should().NotBeNull();
        cfg!.SourceRepoPath.Should().Be("/plugins");
        cfg.SourceSubdir.Should().Be("catalog");
    }
}
