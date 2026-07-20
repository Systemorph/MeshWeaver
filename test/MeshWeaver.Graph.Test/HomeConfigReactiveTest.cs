using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The home's display config is DATA-DRIVEN and read REACTIVELY: <see cref="HomeConfigNodeType.Observe"/>
/// emits the shipped defaults immediately (so the home paints), then re-emits live whenever the config
/// node is created/edited — the mechanism that lets an admin change every user's home without a deploy.
/// Uses a test-controlled path so the reactive composition is exercised deterministically.
/// </summary>
public class HomeConfigReactiveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30000)]
    public async Task Observe_EmitsDefaults_ThenTheConfigNode_LiveOnCreate()
    {
        var workspace = Mesh.GetWorkspace();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var options = Mesh.JsonSerializerOptions;
        // A path the auto-logged-in admin (rbuergi) owns — deterministic, no Admin-seed dependency.
        const string configPath = "rbuergi/HomeConfig";

        // Absent → the shipped defaults (FirstLevel + Flat + LastAccessed).
        var first = await HomeConfigNodeType.Observe(workspace, options, configPath)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();
        first.Should().Be(HomeConfigNodeType.Defaults);

        // An admin creates the config node with NON-default settings.
        await meshService.CreateNode(MeshNode.FromPath(configPath) with
        {
            NodeType = HomeConfigNodeType.NodeType,
            Name = "Home Page",
            State = MeshNodeState.Active,
            Content = new HomeConfig
            {
                Scope = HomeCatalogScope.Subtree,
                Render = HomeCatalogRender.Grouped,
                DefaultSort = HomeCatalogSort.Alphabetical,
            },
        }).FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();

        // Observe re-emits the edited config live — every open home would update without a deploy.
        var updated = await HomeConfigNodeType.Observe(workspace, options, configPath)
            .Where(c => c.Render == HomeCatalogRender.Grouped)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask();
        updated.Scope.Should().Be(HomeCatalogScope.Subtree);
        updated.DefaultSort.Should().Be(HomeCatalogSort.Alphabetical);
    }
}
