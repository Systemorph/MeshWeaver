using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Application;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Northwind.Model;
using Xunit;

namespace MeshWeaver.Northwind.Test;

/// <summary>
/// Tests for the full Northwind application using MonolithMeshTestBase.
/// This tests the application as it runs in production.
/// </summary>
public class NorthwindApplicationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly Address NorthwindAddress = AddressExtensions.CreateAppAddress("Northwind");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .InstallAssemblies(typeof(NorthwindApplicationAttribute).Assembly.Location);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithType<NorthwindDataCube>(nameof(NorthwindDataCube));
    }

    [Fact]
    public void GetLayoutAreas_ShouldWork()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("LayoutAreas");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            NorthwindAddress,
            reference
        );

        var result = stream.Should().Within(TimeSpan.FromSeconds(10)).Emit();

        result.Should().NotBeNull();
    }

    [Fact]
    public void GetNorthwindDataCube_ShouldWork()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            NorthwindAddress,
            new CollectionReference(nameof(NorthwindDataCube))
        );

        var result = stream
            .Where(x => x.Value != null && x.Value.Instances.Count > 0)
            .Select(x => x.Value!.Get<NorthwindDataCube>())
            .Should().Within(TimeSpan.FromSeconds(30)).Match(x => x.Any());

        result.Should().HaveCountGreaterThan(0);
    }
}
