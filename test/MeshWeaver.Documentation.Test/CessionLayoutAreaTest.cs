using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Tests the Cession layout area renders correctly with sample data.
/// Uses HubTestBase pattern to create a hub with the cession data source and layout.
/// </summary>
public class CessionLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CessionPath = "TestData/Cession";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode("Cession", TestPartition)
            {
                Name = "Cession Example",
                NodeType = "Markdown",
            });

    [Fact]
    public async Task CessionEngine_ProducesCorrectResults()
    {
        // This test verifies the business logic independently
        var layer = BusinessRules.CessionSampleData.Layer;
        var results = BusinessRules.CessionEngine.CedeIntoLayer(
            BusinessRules.CessionSampleData.Claims, layer);

        results.Should().HaveCount(10);

        var summary = BusinessRules.CessionEngine.Summarize(results);
        summary.TotalGross.Should().Be(4_380_000);
        summary.TotalCeded.Should().Be(2_000_000);
        summary.CessionRatio.Should().BeApproximately(0.4566, 0.001);
    }
}
